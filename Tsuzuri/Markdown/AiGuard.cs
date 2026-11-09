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
    /// Bash で場所を書かずに読む道（cd を重ねる・* で広く読む）と、目印より上のフォルダからの grep は防げない。場所を渡さないので、Claude がそこへ向かうきっかけはまず無い。
    /// </summary>
    public static class AiGuard
    {
        public const string MarkerName = ".tsuzuri-no-ai";

        /// <summary>目印を見つけたフォルダ。Bash のコマンドの文字と、上のフォルダからの検索を断るのに使う（そこは上へたどっても目印に当たらない）。</summary>
        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>場所を見るツールの入力の項目。上へたどって目印を探す。</summary>
        private static readonly string[] PathKeys = { "file_path", "path", "notebook_path" };

        /// <summary>場所を文字で含みうる項目。目印を見つけたフォルダの場所が入っていれば断る。書く中身（content など）は見ない。</summary>
        private static readonly string[] TextKeys = { "file_path", "path", "notebook_path", "command", "pattern", "glob" };

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
                    if (File.Exists(Path.Combine(folder, MarkerName)))
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

        /// <summary>
        /// Claude のツールの呼び出しを断るか（PreToolUse のフック）。断るならその理由（Claude に見せる）、よければ null。
        /// </summary>
        /// <param name="input">ツールの入力の項目（名前と値）。</param>
        public static string Check(string tool, string cwd, IList<KeyValuePair<string, string>> input)
        {
            string[] known;
            lock (Known) known = Known.ToArray();

            foreach (var item in input)
            {
                if (string.IsNullOrEmpty(item.Value)) continue;

                if (PathKeys.Contains(item.Key))
                {
                    var path = Resolve(item.Value, cwd);
                    if (IsGuarded(path)) return Refusal;

                    // 上のフォルダからの検索（Grep・Glob）。下に目印のフォルダがあれば断る
                    var under = Normalize(path).TrimEnd('/') + "/";
                    if (known.Any(folder => Normalize(folder).StartsWith(under, StringComparison.Ordinal))) return Refusal;
                }

                if (TextKeys.Contains(item.Key) && Mentions(item.Value, known)) return Refusal;
            }

            // 場所を付けない検索は、作業のフォルダで探す
            if ((tool == "Grep" || tool == "Glob") && !input.Any(item => item.Key == "path" && !string.IsNullOrEmpty(item.Value)))
            {
                if (IsGuarded(cwd)) return Refusal;
                var under = Normalize(cwd ?? string.Empty).TrimEnd('/') + "/";
                if (under != "/" && known.Any(folder => Normalize(folder).StartsWith(under, StringComparison.Ordinal))) return Refusal;
            }

            return null;
        }

        private const string Refusal =
            "利用者が AI に渡さないと決めた場所（目印 " + MarkerName + " のあるフォルダの中）なので、Tsuzuri が断った。" +
            "別の方法で読もうとせず、読めなかったことを利用者に伝える。";

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

        /// <summary>text に、目印のあるフォルダの場所が入っているか。\ と / の違い・重ね（Bash の \\ や //）・大文字小文字・Git Bash の /c/ の書き方を問わない。</summary>
        private static bool Mentions(string text, IEnumerable<string> folders)
        {
            var normalized = Normalize(text);
            foreach (var folder in folders)
            {
                var target = Normalize(folder).TrimEnd('/');
                if (target.Length > 1 && normalized.Contains(target)) return true;

                var drive = Regex.Match(target, "^([a-z]):(/.*)$");
                if (drive.Success && normalized.Contains("/" + drive.Groups[1].Value + drive.Groups[2].Value)) return true;
            }
            return false;
        }

        private static string Normalize(string path)
        {
            return Regex.Replace(path.Replace('\\', '/'), "/{2,}", "/").ToLowerInvariant();
        }
    }
}
