using System;
using System.IO;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 添付の置き場所とリンク先の解決（SPEC 9章）。
    ///
    /// 添付は文書の隣の、文書と同じ名前に「.」を付けたフォルダに置く（提案書.md なら .提案書/。2026-10-12）。
    /// 「.」で始めて、文書の並びに添付のフォルダが混ざらないようにする（Mac の Finder では隠れる）。
    /// 本文のリンクにはフォルダ名を書かず ![図](図.png) とだけ書き、ここで添付フォルダから探す。
    /// ファイラーで文書の名前を変えると、添付フォルダの名前も一緒に変わる（2026-11-03。前は利用者が手で揃えていた）。リンクは書き換えなくても切れない。
    /// 「.」の無い古い名前のフォルダは探さない。古い文書は、利用者がフォルダの名前を手で「.」付きに変える。
    /// </summary>
    public static class Attachments
    {
        /// <summary>文書の添付フォルダ。文書が保存されていなければ null。</summary>
        public static string FolderOf(string documentPath)
        {
            if (string.IsNullOrEmpty(documentPath)) return null;

            var name = "." + Path.GetFileNameWithoutExtension(documentPath);

            // ほかのソフトが使う名前とぶつかるときだけ、後ろに -files を付ける（git.md の添付が .git に入らないように）。
            if (Reserved.Contains(name)) name += "-files";

            return Path.Combine(Path.GetDirectoryName(documentPath), name);
        }

        private static readonly System.Collections.Generic.HashSet<string> Reserved =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".github", ".gitlab", ".svn", ".hg", ".vs", ".vscode", ".idea", ".dropbox", ".Trash", ".Trashes",
            };

        /// <summary>
        /// リンク先の相対パスを、まず添付フォルダから、無ければ文書のある場所から解決する。
        /// どちらにも無ければ文書のある場所からのパスを返す。パスとして読めなければ null。
        /// </summary>
        public static string Combine(string documentPath, string target)
        {
            try
            {
                if (Path.IsPathRooted(target)) return Path.GetFullPath(target);

                var attached = Path.GetFullPath(Path.Combine(FolderOf(documentPath), target));
                if (File.Exists(attached)) return attached;

                var baseDir = Path.GetDirectoryName(documentPath);
                return Path.GetFullPath(Path.Combine(baseDir, target));
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }
        }
    
        // =====================================================================
        // 本文から参照されていない添付（2026-10-18）
        //
        // 添付フォルダのファイルのうち、文書のどのページ（サブページも）のリンクからも指されていないもの。
        // ツールバーの「未参照の添付」で一覧にする（孤立ページと同じ考え）。リンクを消したあとの残りを見つけるため。
        // =====================================================================

        /// <summary>添付フォルダにあっても、一覧に出さないファイル（アプリや OS が置くもの）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> Housekeeping =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "state.json", "Thumbs.db", "desktop.ini", ".DS_Store",
            };

        /// <summary>
        /// 添付フォルダの中（下のフォルダも）で、文書のどのページからも参照されていないファイル（フルパス、名前の順）。
        /// 文書は開いているもの（保存前の編集も含む）を見る。ほかの .md からの参照は見ない。
        /// 図（同じ名前の .png と .json）は、.png が参照されていれば .json も参照されているとみなす。
        /// </summary>
        public static System.Collections.Generic.List<string> FindUnreferenced(PageDocument document)
        {
            var result = new System.Collections.Generic.List<string>();
            var folder = FolderOf(document.FilePath);
            if (folder == null || !Directory.Exists(folder)) return result;

            var referenced = ReferencedFiles(document);
            var texts = RawTexts(document);

            // 変更の履歴（git の中身）は添付ではない。数えると「未参照の添付」として消す候補に並んでしまう（2026-10-25）。
            var history = DocumentHistory.GitDirOf(document.FilePath) + Path.DirectorySeparatorChar;

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (file.StartsWith(history, StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileName(file);
                if (Housekeeping.Contains(name) || name.StartsWith("~$", StringComparison.Ordinal)) continue;   // ~$ は Office が開いている間に置く物

                var full = Normalize(file);
                if (referenced.Contains(full)) continue;

                var drawing = string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase);
                if (drawing && referenced.Contains(Path.ChangeExtension(full, ".png"))) continue;

                // 安全のための網（2026-10-27）。リンクとして読めなくても、ファイルの名前が本文のどこかに書いてあれば候補にしない。
                // 消す（\\Mac\ では完全に消える）より、残すほうへ倒す。
                if (NameAppears(texts, name)) continue;
                if (drawing && NameAppears(texts, Path.ChangeExtension(name, ".png"))) continue;

                result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// 文書のすべてのページのリンク（[名](先)・![名](先)・[名]: 先）が指すファイル（フルパス）。
        /// 解析は Markdig に任せる（コードの中のリンクの形は数えない）。リンク先は本文と同じ探し方（添付フォルダから、無ければ文書の場所から）で、
        /// %XX を戻したものと、そのままの綴りの両方を数える。
        /// HTML の書き方（&lt;img src="写真.png" width="400"&gt;・&lt;a href&gt;・&lt;video src&gt;・srcset など）の参照も数える（2026-10-27。
        /// 前は数えず、HTML で大きさを指定した画像が「未参照」として消す候補に並んだ）。
        /// </summary>
        private static System.Collections.Generic.HashSet<string> ReferencedFiles(PageDocument document)
        {
            var found = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var page in document.Pages)
            {
                Markdig.Syntax.MarkdownDocument parsed;
                try
                {
                    parsed = Markdig.Markdown.Parse(page.Text ?? string.Empty, MarkdownParser.Pipeline);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var link in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.LinkInline>(parsed))
                    AddTarget(found, document.FilePath, link.Url);

                foreach (var definition in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.LinkReferenceDefinition>(parsed))
                    AddTarget(found, document.FilePath, definition.Url);

                foreach (var html in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.Inlines.HtmlInline>(parsed))
                    AddHtmlTargets(found, document.FilePath, html.Tag);

                foreach (var block in Markdig.Syntax.MarkdownObjectExtensions.Descendants<Markdig.Syntax.HtmlBlock>(parsed))
                    AddHtmlTargets(found, document.FilePath, block.Lines.ToSlice().ToString());
            }

            return found;
        }

        /// <summary>HTML の属性のうち、ファイルを指すもの（src・href・srcset・poster・data）。値は "…"・'…'・囲まないものを受ける。</summary>
        private static readonly System.Text.RegularExpressions.Regex HtmlAttribute = new System.Text.RegularExpressions.Regex(
            @"(src|href|srcset|poster|data)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void AddHtmlTargets(System.Collections.Generic.HashSet<string> found, string documentPath, string html)
        {
            if (string.IsNullOrEmpty(html)) return;

            foreach (System.Text.RegularExpressions.Match match in HtmlAttribute.Matches(html))
            {
                var value = match.Groups[2].Success ? match.Groups[2].Value
                    : match.Groups[3].Success ? match.Groups[3].Value
                    : match.Groups[4].Value;

                value = System.Net.WebUtility.HtmlDecode(value ?? string.Empty).Trim();
                if (value.Length == 0) continue;

                if (!string.Equals(match.Groups[1].Value, "srcset", StringComparison.OrdinalIgnoreCase))
                {
                    AddTarget(found, documentPath, value);
                    continue;
                }

                // srcset は「先 幅」をカンマで並べたもの（写真.png 1x, 写真@2x.png 2x）。それぞれの先を数える。
                foreach (var candidate in value.Split(','))
                {
                    var parts = candidate.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) AddTarget(found, documentPath, parts[0]);
                }
            }
        }

        /// <summary>
        /// 安全のための網で使う、全部のページの本文（2026-10-27）。そのままのもの、%XX を戻したもの、&amp;amp; などを戻したもの。
        /// 濁点の表し方の違い（Mac の分解した形）を見逃さないよう、合成した形にそろえる。
        /// </summary>
        private static System.Collections.Generic.List<string> RawTexts(PageDocument document)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var page in document.Pages) builder.Append(page.Text ?? string.Empty).Append('\n');

            var raw = builder.ToString();
            var texts = new System.Collections.Generic.List<string> { raw };

            try
            {
                texts.Add(Uri.UnescapeDataString(raw));
            }
            catch (Exception)
            {
            }

            try
            {
                texts.Add(System.Net.WebUtility.HtmlDecode(raw));
            }
            catch (Exception)
            {
            }

            for (var i = 0; i < texts.Count; i++)
            {
                try
                {
                    texts[i] = texts[i].Normalize(System.Text.NormalizationForm.FormC);
                }
                catch (ArgumentException)
                {
                    // 正規化できない文字（壊れたサロゲート等）を含むときは、そのままで比べる。
                }
            }

            return texts;
        }

        /// <summary>ファイルの名前が、本文のどこかに書いてあるか（そのまま・%XX にしたもの。大文字小文字は区別しない）。</summary>
        private static bool NameAppears(System.Collections.Generic.List<string> texts, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            var names = new System.Collections.Generic.List<string>();
            try
            {
                names.Add(name.Normalize(System.Text.NormalizationForm.FormC));
            }
            catch (ArgumentException)
            {
                names.Add(name);
            }

            try
            {
                names.Add(Uri.EscapeDataString(names[0]));
                names.Add(names[0].Replace(" ", "%20"));
            }
            catch (Exception)
            {
            }

            foreach (var text in texts)
            {
                foreach (var candidate in names)
                {
                    if (text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }

            return false;
        }

        private static void AddTarget(System.Collections.Generic.HashSet<string> found, string documentPath, string url)
        {
            if (string.IsNullOrEmpty(url) || url.StartsWith("#", StringComparison.Ordinal) || url.IndexOf("://", StringComparison.Ordinal) >= 0) return;

            // ページ内の位置（#…）は除く。
            var hash = url.IndexOf('#');
            var target = hash >= 0 ? url.Substring(0, hash) : url;
            if (target.Length == 0) return;

            string decoded = null;
            try
            {
                decoded = Uri.UnescapeDataString(target);
            }
            catch (Exception)
            {
            }

            foreach (var candidate in new[] { decoded, target })
            {
                if (string.IsNullOrEmpty(candidate)) continue;

                var path = Combine(documentPath, candidate);
                if (path != null) found.Add(Normalize(path));
            }
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).Replace('/', '\\');
            }
            catch (Exception)
            {
                return path;
            }
        }
}
}
