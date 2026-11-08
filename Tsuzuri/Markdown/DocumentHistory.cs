using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 文書の変更の履歴（SPEC 10章。2026-10-25）。保存するたびに git で記録する。
    ///
    /// 利用者が見るためのものではなく、AI（Claude）に「この文書がどう変わってきたか」を読ませるためのもの。
    /// 画面には何も出さない。履歴は .文書名/git に溜める（git の中身の置き場所。作業場所は文書のあるフォルダで、記録するのは文書の .md だけ）。
    /// git は、exe の隣の git フォルダに置いた MinGit（Git for Windows の同梱用の最小版）の git\cmd\git.exe を先に使う。
    /// 無ければ PATH や Git for Windows の決まった入れ場所から探す。見つからなければ何もしない（文書の保存には関わらない）。
    /// \\Mac\ 共有は遅いので、裏の1本のスレッドで順に動かす。保存は待たない。
    /// </summary>
    public static class DocumentHistory
    {
        private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();
        private static Thread _worker;
        private static string _git;
        private static bool _searched;

        /// <summary>記録できなかったとき（裏のスレッドから呼ばれる）。理由の文。</summary>
        public static event Action<string> Failed;

        /// <summary>この文書の履歴（git の中身）の置き場所。</summary>
        public static string GitDirOf(string documentPath)
        {
            return Path.Combine(Attachments.FolderOf(documentPath), "git");
        }

        /// <summary>
        /// 今のファイルの中身を記録する。前の記録と同じなら何もしない。裏で動くので、すぐ戻る。
        /// 保存したとき（「保存」）と、開いたとき（エディタの外での変更も残すため）に呼ぶ。
        /// </summary>
        public static void Record(string documentPath, string message)
        {
            if (string.IsNullOrEmpty(documentPath) || Attachments.IsBare(documentPath)) return;   // 添付フォルダ無しの文書は記録しない（2026-11-08）

            lock (Queue)
            {
                if (_worker == null)
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "DocumentHistory" };
                    _worker.Start();
                }
            }

            Queue.Add(() => Commit(documentPath, message));
        }

        /// <summary>
        /// 文書の名前を変えたことを記録する（2026-11-03。ファイラーで名前を変えたとき）。添付フォルダ（履歴の入れ物）は、もう新しい名前に変えてある。
        /// 古い名前を記録から外し、新しい名前で記録し直す。記録にはこの文書の .md しか無いので、ファイル名を付けない log で前の記録も続けて読める（大文字小文字だけの変更は --follow では辿れない）。
        /// まだ記録が無ければ、今の中身を記録するだけ。
        /// </summary>
        public static void RecordRename(string documentPath, string oldName)
        {
            if (string.IsNullOrEmpty(documentPath) || Attachments.IsBare(documentPath)) return;

            lock (Queue)
            {
                if (_worker == null)
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "DocumentHistory" };
                    _worker.Start();
                }
            }

            Queue.Add(() => CommitRename(documentPath, oldName));
        }

        private static void CommitRename(string documentPath, string oldName)
        {
            var git = FindGit();
            if (git == null) return;

            var gitDir = GitDirOf(documentPath);
            if (!Directory.Exists(Path.Combine(gitDir, "objects")))
            {
                Commit(documentPath, "名前を変えた: " + oldName + " → " + Path.GetFileName(documentPath));
                return;
            }

            var folder = Path.GetDirectoryName(documentPath);
            var name = Path.GetFileName(documentPath);
            EnsureWorktree(git, gitDir, folder);

            Run(git, gitDir, folder, "rm --cached -q --ignore-unmatch -- " + Quote(oldName));
            Run(git, gitDir, folder, "add -- " + Quote(name));
            if (Run(git, gitDir, folder, "diff --cached --quiet", false) == 0) return;

            Run(git, gitDir, folder, "commit -q -m " + Quote("名前を変えた: " + oldName + " → " + name));
        }

        /// <summary>
        /// 記録に覚えさせた作業場所（文書のあるフォルダ）が今の場所と違えば、覚え直す（2026-11-03）。
        /// 文書をフォルダごと動かした・フォルダの名前を変えたあと、Claude が git --git-dir=... だけで履歴を読むと、古い場所へ行こうとして失敗するため。
        /// </summary>
        private static void EnsureWorktree(string git, string gitDir, string folder)
        {
            try
            {
                var config = File.ReadAllText(Path.Combine(gitDir, "config"));
                var written = folder.TrimEnd('\\').Replace("\\", "\\\\");   // git は設定のファイルに \ を \\ と書く
                if (config.IndexOf("worktree = " + written, StringComparison.OrdinalIgnoreCase) >= 0
                    || config.IndexOf("worktree = \"" + written + "\"", StringComparison.OrdinalIgnoreCase) >= 0) return;
            }
            catch (Exception)
            {
                return;   // 読めなければ触らない（保存の記録を止めない）
            }

            Run(git, gitDir, folder, "config core.worktree " + Quote(folder), false);
        }

        private static void Work()
        {
            foreach (var job in Queue.GetConsumingEnumerable())
            {
                try
                {
                    job();
                }
                catch (Exception ex)
                {
                    Report("変更の履歴を記録できませんでした: " + ex.Message);
                }
            }
        }

        private static void Commit(string documentPath, string message)
        {
            var git = FindGit();
            if (git == null) return;   // git が無ければ記録しない。保存はできている

            var folder = Path.GetDirectoryName(documentPath);
            var gitDir = GitDirOf(documentPath);
            var name = Path.GetFileName(documentPath);

            if (!Directory.Exists(Path.Combine(gitDir, "objects")))
            {
                Directory.CreateDirectory(gitDir);
                Run(git, gitDir, folder, "init -q");

                // 作業場所を覚えさせておく。Claude が git --git-dir=... だけで差分を見られるように。
                Run(git, gitDir, folder, "config core.worktree " + Quote(folder));
            }

            else
            {
                EnsureWorktree(git, gitDir, folder);
            }

            Run(git, gitDir, folder, "add -- " + Quote(name));

            // 前の記録と同じなら（保存したが中身は変わっていない等）記録しない。差分が無ければ 0 で返る。
            if (Run(git, gitDir, folder, "diff --cached --quiet -- " + Quote(name), false) == 0) return;

            Run(git, gitDir, folder, "commit -q -m " + Quote(message));
        }

        /// <summary>git を1回動かす。check なら、失敗したときに知らせて投げる。終了コードを返す。</summary>
        private static int Run(string git, string gitDir, string folder, string command, bool check = true)
        {
            // 毎回つける設定:
            // ・safe.directory=* … \\Mac\ 共有では持ち主が違って見え、git が「怪しい場所」として断るため
            // ・user.name / user.email … git の利用者の設定が無くても記録できるように
            // ・core.quotepath=false … 日本語のファイル名を \343... にしない
            var arguments = "-c safe.directory=* -c user.name=\"Tsuzuri\" -c user.email=tsuzuri@localhost"
                            + " -c core.quotepath=false -c core.autocrlf=false"
                            + " --git-dir=" + Quote(gitDir) + " --work-tree=" + Quote(folder) + " " + command;

            var start = new ProcessStartInfo(git, arguments)
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            using (var process = Process.Start(start))
            {
                // 出力を読み切らないと、多いときに git が止まる。
                var error = process.StandardError.ReadToEndAsync();
                process.StandardOutput.ReadToEnd();

                if (!process.WaitForExit(60000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    throw new IOException("git が終わりません（" + command + "）");
                }

                if (check && process.ExitCode != 0)
                    throw new IOException("git " + command + ": " + error.Result.Trim());

                return process.ExitCode;
            }
        }

        /// <summary>exe の隣に置いた git（MinGit を展開したもの）の、git.exe のあるフォルダ。</summary>
        public static string BundledGitFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "git", "cmd"); }
        }

        /// <summary>git.exe の場所。exe の隣の git、PATH の通った場所、Git for Windows の決まった入れ場所の順に探す。無ければ null。</summary>
        private static string FindGit()
        {
            if (_searched) return _git;
            _searched = true;

            var candidates = new System.Collections.Generic.List<string> { BundledGitFolder };
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
            {
                if (folder.Trim().Length > 0) candidates.Add(folder.Trim().Trim('"'));
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd"));

            foreach (var folder in candidates)
            {
                try
                {
                    var path = Path.Combine(folder, "git.exe");
                    if (File.Exists(path))
                    {
                        _git = path;
                        return _git;
                    }
                }
                catch (Exception)
                {
                    // PATH に変な項目があっても飛ばす。
                }
            }

            Report("git が見つからないため、変更の履歴は記録しません（exe の隣の git フォルダに MinGit を展開すると記録します）。");
            return null;
        }

        /// <summary>コマンドの引数にする。中の " は外す（パスにもメッセージにも使わない文字）。</summary>
        private static string Quote(string text)
        {
            return "\"" + text.Replace("\"", string.Empty).TrimEnd('\\') + "\"";
        }

        private static void Report(string message)
        {
            var failed = Failed;
            if (failed != null) failed(message);
        }
    }
}
