using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using AiLink.Agents;

namespace AiLink.Views
{
    /// <summary>ファイラーの行に付ける git の状態（VS Code のエクスプローラーと同じ考え）。</summary>
    public enum GitMark
    {
        None,
        Ignored,
        Untracked,
        Added,
        Renamed,
        Modified,
        Deleted,
        Conflict,
    }

    /// <summary>
    /// ファイラーの git の状態（2026-11-24。利用者の考え。SPEC 4章）。
    /// 見ているフォルダが git の管理の中なら、`git status` でそのフォルダの直下の名前ごとの状態を読む（フォルダは中の変更をまとめる）。
    /// git は文書の変更の履歴と同じもの（exe の隣の MinGit か、PC の Git。GitLocator.GitPath）。無ければ何もしない。
    /// 読むだけで、利用者の git には書かない（--no-optional-locks で index も書き換えない）。どれも裏のスレッドから呼ぶ。
    /// </summary>
    public static class FilerGit
    {
        /// <summary>\\Mac\ 共有などで遅いとき、これを過ぎたら打ち切る（印を出さないだけ）。</summary>
        private const int StatusTimeout = 5000;

        /// <summary>
        /// フォルダの直下の名前（大文字小文字は区別しない）ごとの状態。git の管理の外・git が無い・読めない・時間切れなら null。
        /// フォルダの名前には、中の変更のうちいちばん目立つもの（無視は数えない）か、そのフォルダ自身が無視・新しいなら、その状態が入る。
        /// </summary>
        public static Dictionary<string, GitMark> Status(string folder)
        {
            if (string.IsNullOrEmpty(folder) || GitLocator.GitPath == null) return null;

            string prefix;
            if (Run(folder, "rev-parse --show-prefix", StatusTimeout, out prefix) != 0 || prefix == null) return null;
            prefix = prefix.Trim();

            string output;
            if (Run(folder, "--no-optional-locks status --porcelain=v1 -z --ignored=matching -unormal -- .", StatusTimeout, out output) != 0 || output == null)
                return null;

            var marks = new Dictionary<string, GitMark>(StringComparer.OrdinalIgnoreCase);
            var whole = GitMark.None;   // 見ているフォルダごと新しい・無視のとき
            var parts = output.Split('\0');
            for (var i = 0; i < parts.Length; i++)
            {
                var entry = parts[i];
                if (entry.Length < 4) continue;
                var x = entry[0];
                var y = entry[1];
                var path = entry.Substring(3);
                if (x == 'R' || x == 'C') i++;   // 名前を変えた・写したときは、次に元の名前が来る

                var mark = MarkOf(x, y);
                if (mark == GitMark.None) continue;

                string rel;
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rel = path.Substring(prefix.Length);
                else if (path.EndsWith("/") && prefix.StartsWith(path, StringComparison.OrdinalIgnoreCase)) rel = string.Empty;
                else continue;

                if (rel.Length == 0 || rel == "/")
                {
                    whole = mark;   // 見ているフォルダが、新しいフォルダ・無視するフォルダの中
                    continue;
                }

                var slash = rel.IndexOf('/');
                var name = slash < 0 ? rel : rel.Substring(0, slash);
                var direct = slash < 0 || slash == rel.Length - 1;   // 直下のファイルか、直下のフォルダそのもの（"名前/"）
                if (!direct && mark == GitMark.Ignored) continue;   // 中に無視するものがあっても、フォルダは無視にしない

                GitMark before;
                if (!marks.TryGetValue(name, out before) || mark > before || before == GitMark.Ignored) marks[name] = mark;
            }

            if (whole != GitMark.None) marks["\0whole"] = whole;
            return marks;
        }

        /// <summary>見ているフォルダごとの状態（Status の結果から）。無ければ None。</summary>
        public static GitMark Whole(Dictionary<string, GitMark> marks)
        {
            GitMark mark;
            return marks != null && marks.TryGetValue("\0whole", out mark) ? mark : GitMark.None;
        }

        private static GitMark MarkOf(char x, char y)
        {
            if (x == '?' && y == '?') return GitMark.Untracked;
            if (x == '!' && y == '!') return GitMark.Ignored;
            if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) return GitMark.Conflict;
            if (x == 'D' || y == 'D') return GitMark.Deleted;
            if (y == 'M' || y == 'T' || x == 'M' || x == 'T') return GitMark.Modified;
            if (x == 'R') return GitMark.Renamed;
            if (x == 'A' || x == 'C') return GitMark.Added;
            return GitMark.None;
        }

        /// <summary>行の右端に出す字（フォルダは点）。</summary>
        public static string Letter(GitMark mark, bool isFolder)
        {
            if (mark == GitMark.None || mark == GitMark.Ignored) return null;
            if (isFolder) return "●";
            switch (mark)
            {
                case GitMark.Untracked: return "U";
                case GitMark.Added: return "A";
                case GitMark.Renamed: return "R";
                case GitMark.Modified: return "M";
                case GitMark.Deleted: return "D";
                case GitMark.Conflict: return "!";
                default: return null;
            }
        }

        /// <summary>名前と印の色（VS Code の既定の暗い色合いに寄せる）。</summary>
        public static Brush BrushOf(GitMark mark)
        {
            switch (mark)
            {
                case GitMark.Ignored: return Ignored;
                case GitMark.Untracked:
                case GitMark.Added:
                case GitMark.Renamed: return Green;
                case GitMark.Modified: return Yellow;
                case GitMark.Deleted: return Red;
                case GitMark.Conflict: return Conflicted;
                default: return null;
            }
        }

        /// <summary>印の説明（ツールチップ）。</summary>
        public static string Describe(GitMark mark, bool isFolder)
        {
            string text;
            switch (mark)
            {
                case GitMark.Untracked: text = isFolder ? "git の管理に入っていないものがある" : "git の管理に入っていない新しいファイル"; break;
                case GitMark.Added: text = isFolder ? "追加したもの（ステージ済み）がある" : "追加した（ステージ済み）"; break;
                case GitMark.Renamed: text = isFolder ? "名前を変えたものがある" : "名前を変えた"; break;
                case GitMark.Modified: text = isFolder ? "変更がある" : "変更あり"; break;
                case GitMark.Deleted: text = isFolder ? "削除したものがある" : "削除した"; break;
                case GitMark.Conflict: text = isFolder ? "競合がある" : "競合"; break;
                default: return null;
            }
            return "git: " + text;
        }

        private static readonly Brush Green = Freeze("#FF73C991");
        private static readonly Brush Yellow = Freeze("#FFE2C08D");
        private static readonly Brush Red = Freeze("#FFC74E39");
        private static readonly Brush Conflicted = Freeze("#FFE4676B");
        private static readonly Brush Ignored = Freeze("#FF6B717C");

        private static Brush Freeze(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        /// <summary>git を動かして、標準出力を受け取る。終わらなければ止めて -1。動かせなければ -1。</summary>
        private static int Run(string folder, string arguments, int timeout, out string output)
        {
            output = null;
            var git = GitLocator.GitPath;
            if (git == null) return -1;
            try
            {
                // ・safe.directory=* … \\Mac\ 共有では持ち主が違って見え、git が断るため（文書の変更の履歴と同じ）
                // ・core.fsmonitor=false … リポジトリの設定で外のプログラムを動かさない（読むだけなので要らない）
                var start = new ProcessStartInfo(git, "-c safe.directory=* -c core.fsmonitor=false " + arguments)
                {
                    WorkingDirectory = folder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";   // 何かを尋ねて待たない
                start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";

                using (var process = Process.Start(start))
                {
                    var error = process.StandardError.ReadToEndAsync();
                    var read = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(timeout))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                        }
                        return -1;
                    }
                    process.WaitForExit();
                    if (!read.Wait(5000)) return -1;
                    output = read.Result;
                    return process.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
