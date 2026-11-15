using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// AI に渡さない場所（2026-11-09。SPEC 19章）。目印のファイル（.tsuzuri-no-ai。中身は空でよい）を置いたフォルダの下は、Claude に渡さない。
    /// パスワードや銀行の情報を書いた文書を、それだけを入れた暗号化のディスクイメージに置き、その一番上に目印を置く使い方のため。
    ///
    /// ・話しかけたとき、見ている文書がその下なら、今の状態に場所・ページ・カーソルの行・選んだ文字を書かない（MainWindow.Ai.cs の AiContext）
    /// ・Claude がその下を読み書きしようとしたら、PreToolUse のフックで断る（Check）
    /// ・その下では会話を始めない
    /// 文書の先頭の印は使わない（フォルダの目印なら、添付フォルダと履歴の git も一緒に守れる）。
    /// Bash で場所を書かずに読む道（cd を重ねる・* で広く読む）は防げない。場所を渡さないので、Claude がそこへ向かうきっかけはまず無い。
    /// </summary>
    public static class AiGuard
    {
        public const string MarkerName = ".tsuzuri-no-ai";

        /// <summary>目印を見つけたフォルダ。Bash のコマンドの文字と、上のフォルダからの検索を断るのに使う（そこは上へたどっても目印に当たらない）。目印を消したら外す（KnownFolders）。</summary>
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// フォルダごとの「目印があるか」と確かめた時刻（2026-11-15）。話しかけるたびに、開いている全部の文書について上のフォルダをたどっていて、
        /// \\Mac\ の上では 1 回ごとに時間がかかり、画面が止まった。少しの間（CacheSeconds）覚えて、同じ親を何度も確かめない。
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<bool, DateTime>> Markers = new Dictionary<string, KeyValuePair<bool, DateTime>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>目印の有無を覚えておく秒数。目印を置いた・消したのが効くまで、最長でこれだけかかる。</summary>
        private const int CacheSeconds = 30;

        /// <summary>場所を見るツールの入力の項目。上へたどって目印を探す。</summary>
        private static readonly string[] PathKeys = { "file_path", "path", "notebook_path" };

        /// <summary>場所を文字で含みうる項目。目印を見つけたフォルダの場所が入っていれば断る。書く中身（content など）は見ない。フックが送るのもこの項目だけ。</summary>
        public static readonly string[] TextKeys = { "file_path", "path", "notebook_path", "command", "pattern", "glob" };

        /// <summary>コマンドの中の、場所らしい部分（C:\…・C:/…・\\server\…・/c/…・/…）。引用符で囲んだもの（空白を含みうる）と、囲んでいないもの。</summary>
        private static readonly Regex PathLike = new Regex(
            @"""((?:[A-Za-z]:[\\/]|\\\\|/)[^""]*)""|'((?:[A-Za-z]:[\\/]|\\\\|/)[^']*)'|(?<![\w.:/\\-])((?:[A-Za-z]:[\\/]|\\\\|/)[^\s""'|;&<>()*?]*)",
            RegexOptions.Compiled);

        /// <summary>path（ファイルかフォルダ）が、目印のあるフォルダの下なら、そのフォルダ。無ければ null。</summary>
        public static string GuardedFolderOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                var full = Path.GetFullPath(path);
                var folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(folder))
                {
                    if (HasMarker(folder))
                    {
                        lock (Known) Known.Add(folder);
                        return folder;
                    }
                    folder = Path.GetDirectoryName(folder);
                }
            }
            catch (Exception)
            {
                // 場所として読めない（* を含む等）。文字での照合（Check）に任せる
            }
            return null;
        }

        public static bool IsGuarded(string path)
        {
            return GuardedFolderOf(path) != null;
        }

        /// <summary>そのフォルダに目印があるか。CacheSeconds の間は覚えたものを返す。</summary>
        private static bool HasMarker(string folder)
        {
            var now = DateTime.UtcNow;
            lock (Markers)
            {
                KeyValuePair<bool, DateTime> seen;
                if (Markers.TryGetValue(folder, out seen) && (now - seen.Value).TotalSeconds < CacheSeconds) return seen.Key;
            }

            bool found;
            try
            {
                found = File.Exists(Path.Combine(folder, MarkerName));
            }
            catch (Exception)
            {
                found = false;
            }

            lock (Markers)
            {
                if (Markers.Count > 4096) Markers.Clear();
                Markers[folder] = new KeyValuePair<bool, DateTime>(found, now);
            }
            return found;
        }

        /// <summary>覚えた目印の有無を捨てる（試験から。tools/ai-guard）。</summary>
        internal static void ForgetMarkers()
        {
            lock (Markers) Markers.Clear();
        }

        /// <summary>目印を見つけたフォルダのうち、今も目印があるもの。消えたものは外す（2026-11-15。前は再起動するまで断り続けた）。</summary>
        private static string[] KnownFolders()
        {
            string[] known;
            lock (Known) known = Known.ToArray();

            var gone = known.Where(folder => !HasMarker(folder)).ToList();
            if (gone.Count == 0) return known;

            lock (Known)
            {
                foreach (var folder in gone) Known.Remove(folder);
            }
            return known.Except(gone).ToArray();
        }

        /// <summary>
        /// Claude のツールの呼び出しを断るか（PreToolUse のフック）。断るならその理由（Claude に見せる）、よければ null。
        /// </summary>
        /// <param name="input">ツールの入力の項目（名前と値）。</param>
        public static string Check(string tool, string cwd, IList<KeyValuePair<string, string>> input)
        {
            cwd = FullPath(cwd);

            // コマンド・パターンに書いた場所は、まだ知らない目印のフォルダでも上へたどって確かめる（2026-11-15。前は知っているフォルダの文字だけを見た）
            foreach (var item in input)
            {
                if (string.IsNullOrEmpty(item.Value) || !TextKeys.Contains(item.Key) || PathKeys.Contains(item.Key)) continue;
                foreach (var path in PathsIn(item.Value).Take(32))
                {
                    if (IsGuarded(Resolve(path, cwd))) return Refusal;
                }
            }

            var known = KnownFolders();

            foreach (var item in input)
            {
                if (string.IsNullOrEmpty(item.Value)) continue;

                if (PathKeys.Contains(item.Key))
                {
                    // . や .. を解決してから比べる（2026-11-15。Grep の path に . を渡すと、下の目印のフォルダを見落とした）
                    var path = FullPath(Resolve(item.Value, cwd));
                    if (IsGuarded(path)) return Refusal;

                    // 上のフォルダからの検索（Grep・Glob）。下に目印のフォルダがあれば断る
                    if (ContainsKnown(path, known)) return Refusal;
                }

                if (TextKeys.Contains(item.Key) && Mentions(item.Value, known)) return Refusal;
            }

            // 場所を付けない検索は、作業のフォルダで探す
            if ((tool == "Grep" || tool == "Glob") && !input.Any(item => item.Key == "path" && !string.IsNullOrEmpty(item.Value)))
            {
                if (IsGuarded(cwd)) return Refusal;
                if (!string.IsNullOrEmpty(cwd) && ContainsKnown(cwd, known)) return Refusal;
            }

            return null;
        }

        private const string Refusal =
            "利用者が AI に渡さないと決めた場所（目印 " + MarkerName + " のあるフォルダの中）なので、Tsuzuri が断った。" +
            "別の方法で読もうとせず、読めなかったことを利用者に伝える。";

        /// <summary>folder の下に、目印を見つけたフォルダがあるか。</summary>
        private static bool ContainsKnown(string folder, IEnumerable<string> known)
        {
            var under = WithSlash(Normalize(folder));
            return known.Any(guarded => WithSlash(Normalize(guarded)).StartsWith(under, StringComparison.Ordinal));
        }

        private static string Resolve(string path, string cwd)
        {
            try
            {
                return string.IsNullOrEmpty(cwd) || Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>text の中の、場所らしい部分。Windows では Git Bash の /c/… を C:/… に直す。</summary>
        private static IEnumerable<string> PathsIn(string text)
        {
            foreach (Match match in PathLike.Matches(text))
            {
                var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                if (path.Length < 2) continue;

                if (Path.DirectorySeparatorChar == '\\')
                {
                    var bash = Regex.Match(path, "^/([A-Za-z])(/.*)?$");
                    if (bash.Success) path = bash.Groups[1].Value + ":" + (bash.Groups[2].Success ? bash.Groups[2].Value : "/");
                    else if (path[0] == '/' && (path.Length < 2 || path[1] != '/')) continue;   // /dev/null・/tmp など。ドライブの付かない場所は見ない
                }
                yield return path;
            }
        }

        /// <summary>
        /// text に、目印のあるフォルダの場所が入っているか。\ と / の違い・重ね（Bash の \\ や //）・大文字小文字・Git Bash の /c/ の書き方を問わない。
        /// 前後が名前の続きでないところだけを当てる（2026-11-15。ドライブの一番上 E:\ に目印を置くと「e:」で照合し、「fix: …」まで断っていた。vault が vault2 にも当たっていた）。
        /// </summary>
        internal static bool Mentions(string text, IEnumerable<string> folders)
        {
            var normalized = Normalize(text);
            foreach (var folder in folders)
            {
                var target = Normalize(folder).TrimEnd('/');
                var root = Regex.IsMatch(target, "^[a-z]:$");
                if (root) target += "/";   // ドライブの一番上は「e:/」で照合する
                if (target.Length > 2 && Occurs(normalized, target)) return true;

                var drive = Regex.Match(target, "^([a-z]):(/.*)$");
                if (drive.Success && Occurs(normalized, "/" + drive.Groups[1].Value + drive.Groups[2].Value)) return true;
                if (root && Occurs(normalized, "/" + target[0])) return true;   // Git Bash の /e（/e/… も）
            }
            return false;
        }

        /// <summary>text の中に target が、前後とも名前の途中でない形で現れるか。target が / で終わるなら、後ろは見ない。</summary>
        private static bool Occurs(string text, string target)
        {
            var index = text.IndexOf(target, StringComparison.Ordinal);
            while (index >= 0)
            {
                var before = index == 0 ? ' ' : text[index - 1];
                var after = index + target.Length >= text.Length ? ' ' : text[index + target.Length];
                var startOk = !IsNameChar(before) && (target[0] != '/' || before != '/' && before != '.');
                var endOk = target.EndsWith("/", StringComparison.Ordinal) || after == '/' || !IsNameChar(after);
                if (startOk && endOk) return true;
                index = text.IndexOf(target, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '~';
        }

        private static string WithSlash(string path)
        {
            return path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
        }

        private static string Normalize(string path)
        {
            return Regex.Replace(path.Replace('\\', '/'), "/{2,}", "/").ToLowerInvariant();
        }
    }
}
