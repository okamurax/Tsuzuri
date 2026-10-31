using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Tsuzuri.WordPress
{
    /// <summary>
    /// WordPress の本文（HTML）を Markdown に戻す（SPEC 20章。2026-10-30）。
    ///
    /// 方針は「失わない」。Markdown で同じに書けるものだけ Markdown にし、それ以外（属性の付いた画像・div・figure・iframe・
    /// 結合した表のセルなど）は、元の HTML の文字をそのまま本文に残す。Markdown は HTML をそのまま通すので、
    /// 保存して HTML に戻したときに、同じものが WordPress に返る。
    ///
    /// 改行の扱い: WordPress（クラシック）は表示するときに wpautop で、段落の中の改行を &lt;br&gt; にする。
    /// Markdown の段落の中の改行は HTML でも改行のまま出るので、WordPress では同じく &lt;br&gt; になる。
    /// そこで、本文の改行も &lt;br&gt; も、Markdown では段落の中の改行にする。
    ///
    /// コメントはそのまま残す。ブロックエディタの印（&lt;!-- wp:paragraph --&gt; など）も、サブページの印（&lt;!--page:…--&gt;）も。
    /// 印は1行ずつの塊になり、保存すると同じ形で WordPress に返る（ブロックエディタの記事はブロックのまま）。
    /// </summary>
    public static class HtmlToMarkdown
    {
        // =====================================================================
        // HTML を木に読む
        // =====================================================================

        private sealed class Node
        {
            /// <summary>要素の名前（小文字）。文字は "#text"、コメントは "#comment"。</summary>
            public string Name;
            public readonly Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Node> Children = new List<Node>();
            public Node Parent;

            /// <summary>文字（実体参照を戻したもの）。コメントは元の文字のまま。</summary>
            public string Text;

            /// <summary>元の HTML での範囲（開始タグの &lt; から終了タグの &gt; の後ろまで）。そのまま残すときに使う。</summary>
            public int Start;
            public int End;

            public bool IsText { get { return Name == "#text"; } }
            public bool IsComment { get { return Name == "#comment"; } }
            public bool IsElement { get { return Name[0] != '#'; } }
        }

        private static readonly HashSet<string> VoidElements = new HashSet<string>
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
        };

        private static readonly HashSet<string> RawTextElements = new HashSet<string> { "script", "style", "textarea", "title" };

        /// <summary>開くと、開いている &lt;p&gt; を閉じる要素。</summary>
        private static readonly HashSet<string> ClosesParagraph = new HashSet<string>
        {
            "address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer",
            "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
        };

        /// <summary>Markdown の段落の並びの中で、段落とは別の塊として扱う要素。</summary>
        private static readonly HashSet<string> BlockElements = new HashSet<string>
        {
            "address", "article", "aside", "audio", "blockquote", "canvas", "details", "div", "dl", "embed", "fieldset", "figcaption",
            "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "iframe", "main", "nav", "noscript",
            "object", "ol", "p", "pre", "script", "section", "style", "table", "ul", "video",
        };

        private static Node Parse(string html)
        {
            var root = new Node { Name = "#root", Start = 0, End = html.Length };
            var current = root;
            var i = 0;

            while (i < html.Length)
            {
                if (html[i] == '<')
                {
                    if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                    {
                        var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        var end = close < 0 ? html.Length : close + 3;
                        var raw = html.Substring(i, end - i);

                        // ブロックエディタの印（<!-- wp:… -->）も捨てない（2026-10-30 のレビュー）。印にしか中身が無いブロック
                        // （最新の投稿・再利用ブロック・プラグインのブロック）や、配置の設定が消えるため。
                        Add(current, new Node { Name = "#comment", Text = raw, Start = i, End = end });

                        i = end;
                        continue;
                    }

                    if (i + 1 < html.Length && html[i + 1] == '/' && i + 2 < html.Length && char.IsLetter(html[i + 2]))
                    {
                        var close = html.IndexOf('>', i);
                        var end = close < 0 ? html.Length : close + 1;
                        var name = ReadName(html, i + 2);
                        current = CloseElement(current, name, i, end);
                        i = end;
                        continue;
                    }

                    if (i + 1 < html.Length && char.IsLetter(html[i + 1]))
                    {
                        Node element;
                        bool selfClosing;
                        var end = ReadStartTag(html, i, out element, out selfClosing);

                        current = CloseImplicitly(current, element.Name, i);
                        element.Start = i;
                        Add(current, element);

                        if (VoidElements.Contains(element.Name) || selfClosing)
                        {
                            element.End = end;
                            i = end;
                            continue;
                        }

                        if (RawTextElements.Contains(element.Name))
                        {
                            var closeTag = html.IndexOf("</" + element.Name, end, StringComparison.OrdinalIgnoreCase);
                            var textEnd = closeTag < 0 ? html.Length : closeTag;
                            element.Children.Add(new Node { Name = "#text", Text = html.Substring(end, textEnd - end), Parent = element, Start = end, End = textEnd });

                            var close = closeTag < 0 ? -1 : html.IndexOf('>', closeTag);
                            element.End = close < 0 ? html.Length : close + 1;
                            i = element.End;
                            continue;
                        }

                        current = element;
                        i = end;
                        continue;
                    }
                }

                // 文字。次のタグかコメントの手前まで（タグにならない < は文字のうち）。
                var next = i + 1;
                while (next < html.Length && !(html[next] == '<' && next + 1 < html.Length
                                               && (char.IsLetter(html[next + 1]) || html[next + 1] == '/' || html[next + 1] == '!')))
                    next++;

                var text = WebUtility.HtmlDecode(html.Substring(i, next - i));
                var last = current.Children.Count > 0 ? current.Children[current.Children.Count - 1] : null;
                if (last != null && last.IsText)
                {
                    last.Text += text;
                    last.End = next;
                }
                else
                {
                    Add(current, new Node { Name = "#text", Text = text, Start = i, End = next });
                }

                i = next;
            }

            for (var node = current; node != null && node != root; node = node.Parent)
                node.End = html.Length;

            return root;
        }

        private static void Add(Node parent, Node child)
        {
            child.Parent = parent;
            parent.Children.Add(child);
        }

        private static string ReadName(string html, int from)
        {
            var end = from;
            while (end < html.Length && (char.IsLetterOrDigit(html[end]) || html[end] == '-' || html[end] == ':')) end++;
            return html.Substring(from, end - from).ToLowerInvariant();
        }

        /// <summary>開始タグを読む。返すのはタグの &gt; の後ろ。</summary>
        private static int ReadStartTag(string html, int start, out Node element, out bool selfClosing)
        {
            element = new Node { Name = ReadName(html, start + 1) };
            selfClosing = false;

            var i = start + 1 + element.Name.Length;
            while (i < html.Length)
            {
                var c = html[i];
                if (c == '>') return i + 1;

                if (c == '/' && i + 1 < html.Length && html[i + 1] == '>')
                {
                    selfClosing = true;
                    return i + 2;
                }

                if (char.IsWhiteSpace(c) || c == '/')
                {
                    i++;
                    continue;
                }

                var nameStart = i;
                while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '=' && html[i] != '>' && html[i] != '/') i++;
                var name = html.Substring(nameStart, i - nameStart);

                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;

                var value = string.Empty;
                if (i < html.Length && html[i] == '=')
                {
                    i++;
                    while (i < html.Length && char.IsWhiteSpace(html[i])) i++;

                    if (i < html.Length && (html[i] == '"' || html[i] == '\''))
                    {
                        var quote = html[i];
                        var close = html.IndexOf(quote, i + 1);
                        if (close < 0) close = html.Length;
                        value = html.Substring(i + 1, close - i - 1);
                        i = Math.Min(html.Length, close + 1);
                    }
                    else
                    {
                        var valueStart = i;
                        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                        value = html.Substring(valueStart, i - valueStart);
                    }
                }

                value = WebUtility.HtmlDecode(value);
                if (name.Length > 0 && !IsIgnorable(element.Name, name, value)) element.Attributes[name] = value;
            }

            return html.Length;
        }

        /// <summary>
        /// 見た目に関わらない属性（2026-10-30）。無いものとして扱い、Markdown にしてよいかの判断に入れない。
        /// Google ドキュメント等から貼った本文は、段落ごとに dir="ltr" が付いていて、前はそれだけで段落が全部 HTML のまま残った。
        /// 保存すると、これらの属性は落ちる（見た目は変わらない）。
        /// - dir="ltr"（左から右は既定）・表の見出しの scope・role="presentation"・aria-*・data-*
        /// - 画像の width・height・decoding・loading・srcset・sizes
        /// - 何もしないと分かっているクラス（IsJunkClass）
        /// - 表の cellspacing・cellpadding、セルの valign
        /// </summary>
        // Mac のメモ・テキストエディット等からコピーした文に付くクラス（p1・s1・t1・td1・li1）。
        private static readonly Regex MacClass = new Regex(@"^(p|s|li|ul|ol|t|td|tr|table|h)\d+$", RegexOptions.Compiled);

        // claude.ai などの画面からコピーした Tailwind のクラス（text-sm・py-2・border-b-0.5・print:overflow-x-visible など）。
        private static readonly Regex TailwindClass = new Regex(
            @"(\[|:|^font-claude|^(text|font|border|bg|p|px|py|pt|pb|pl|pr|m|mx|my|mt|mb|ml|mr|w|h|min-w|max-w|min-h|max-h|leading|tracking|whitespace|break|overflow|gap|space|rounded|shadow|list|items|justify|z|top|left|right|bottom|align|select|cursor)-|^(flex|grid|block|inline|inline-block|hidden|relative|absolute|border|rounded|shadow|truncate|underline|italic)$)",
            RegexOptions.Compiled);

        /// <summary>
        /// 貼ったときに元の場所から付いてきただけで、WordPress では何もしないと分かっているクラス（2026-10-30 のレビューで、許す方式から捨てる方式に替えた）。
        /// 分からないクラスは残す（テーマの装飾 blank-box・marker-under・btn・吹き出し等を消さないため）。
        /// </summary>
        private static bool IsJunkClass(string element, string token)
        {
            if (MacClass.IsMatch(token) || TailwindClass.IsMatch(token)) return true;

            // クラシックエディタの画像の大きさの印（大きさはファイルそのものの大きさ）。寄せ（alignleft 等）は見た目が変わるので残す。
            return element == "img" && (token == "alignnone" || token.StartsWith("size-", StringComparison.Ordinal) || Regex.IsMatch(token, @"^wp-image-\d+$"));
        }

        private static bool IsIgnorable(string element, string name, string value)
        {
            name = name.ToLowerInvariant();

            // クラシックエディタの画像（<img class="alignnone size-full wp-image-7838" width="766" height="468">）。
            // 大きさはファイルそのものの大きさ（size-full・縮小版のファイル）なので、width・height を落としても見た目はほぼ変わらない。
            // 寄せ（alignleft・alignright・aligncenter）は見た目が変わるので落とさない（HTML のまま残る）。
            // decoding・loading・srcset・sizes は WordPress が表示するときに足すもの（2026-10-30。#7825 で画像がリンクにならず、押せなかった）。
            if (element == "img")
            {
                if (name == "width" || name == "height" || name == "decoding" || name == "loading" || name == "srcset" || name == "sizes") return true;
            }

            // クラスは、全部が「何もしないと分かっているもの」（IsJunkClass）のときだけ捨てる。1つでも分からないものがあれば残す。
            // Mac のメモ等の p1・s1（#7906・#6758）、Tailwind（#6758）、クラシックエディタの画像の size-full・wp-image-123（#7825）など。
            // コード（pre・code）のクラスは言語や色付けのプラグインが読むので、ここでは落とさない（Code が見る）。
            if (name == "class" && element != "pre" && element != "code")
                return value.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).All(token => IsJunkClass(element, token));

            // 表の昔の見た目の属性（Mac から貼った表に付く。#6758）。見た目はテーマの表の CSS が決めるので、落としてもほぼ変わらない。
            if (element == "table" && (name == "cellspacing" || name == "cellpadding")) return true;
            if ((element == "td" || element == "th" || element == "tr") && name == "valign") return true;

            if (name == "dir") return value.Trim().Equals("ltr", StringComparison.OrdinalIgnoreCase);
            if (name == "scope") return element == "th" || element == "td";
            if (name == "role") return value.Trim().Equals("presentation", StringComparison.OrdinalIgnoreCase);
            return name.StartsWith("aria-", StringComparison.Ordinal) || name.StartsWith("data-", StringComparison.Ordinal);
        }

        /// <summary>HTML の決まりで、書かれていなくても閉じる要素を閉じる（&lt;p&gt; の中の塊・続く &lt;li&gt;・表のセル）。</summary>
        private static Node CloseImplicitly(Node current, string name, int at)
        {
            if (ClosesParagraph.Contains(name))
            {
                var p = FindOpen(current, "p", "li", "td", "th", "blockquote", "div");
                if (p != null && p.Name == "p") current = PopThrough(current, p, at);
            }

            if (name == "li")
            {
                var li = FindOpen(current, "li", "ul", "ol");
                if (li != null && li.Name == "li") current = PopThrough(current, li, at);
            }
            else if (name == "td" || name == "th")
            {
                var cell = FindOpen(current, "td", "th", "tr", "table");
                if (cell != null && (cell.Name == "td" || cell.Name == "th")) current = PopThrough(current, cell, at);
            }
            else if (name == "tr")
            {
                var row = FindOpen(current, "tr", "table");
                if (row != null && row.Name == "tr") current = PopThrough(current, row, at);
            }
            else if (name == "thead" || name == "tbody" || name == "tfoot")
            {
                var section = FindOpen(current, "thead", "tbody", "tfoot", "table");
                if (section != null && section.Name != "table") current = PopThrough(current, section, at);
            }

            return current;
        }

        /// <summary>開いている要素を内側から探し、names の最初に当たったものを返す。</summary>
        private static Node FindOpen(Node current, params string[] names)
        {
            for (var node = current; node != null && node.Name != "#root"; node = node.Parent)
            {
                if (names.Contains(node.Name)) return node;
            }

            return null;
        }

        /// <summary>target までを閉じ（終わりは at）、その親を返す。</summary>
        private static Node PopThrough(Node current, Node target, int at)
        {
            for (var node = current; node != null; node = node.Parent)
            {
                node.End = at;
                if (node == target) return node.Parent;
            }

            return current;
        }

        private static Node CloseElement(Node current, string name, int tagStart, int tagEnd)
        {
            var open = FindOpen(current, name);
            if (open == null) return current;   // 対になる開始タグが無い終了タグは無視する

            for (var node = current; node != open; node = node.Parent) node.End = tagStart;
            open.End = tagEnd;
            return open.Parent;
        }

        // =====================================================================
        // 木を Markdown にする
        // =====================================================================

        public static string Convert(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;

            html = html.Replace("\r\n", "\n").Replace('\r', '\n');
            var root = Parse(html);
            var blocks = Blocks(root.Children, html);
            return string.Join("\n\n", blocks).Trim('\n');
        }

        /// <summary>並んだ子を、塊（段落・見出し・リスト…）の並びにする。塊の間は空行。</summary>
        private static List<string> Blocks(List<Node> nodes, string html)
        {
            var blocks = new List<string>();
            var run = new List<Node>();

            foreach (var node in nodes)
            {
                if (node.IsComment || (node.IsElement && BlockElements.Contains(node.Name)))
                {
                    FlushParagraphs(run, blocks, html);
                    var block = node.IsComment ? node.Text : Block(node, html);
                    if (!string.IsNullOrWhiteSpace(block)) blocks.Add(block);
                }
                else
                {
                    run.Add(node);
                }
            }

            FlushParagraphs(run, blocks, html);
            return blocks;
        }

        /// <summary>塊の外に書かれた文字（クラシックエディタの本文など）。空行で段落に分ける。</summary>
        private static void FlushParagraphs(List<Node> run, List<string> blocks, string html)
        {
            if (run.Count == 0) return;

            var text = Inline(run, html);
            run.Clear();

            foreach (var paragraph in Regex.Split(text, @"\n[ \t]*\n"))
            {
                var finished = FinishParagraph(paragraph);
                if (finished.Length > 0) blocks.Add(finished);
            }
        }

        private static string Block(Node node, string html)
        {
            switch (node.Name)
            {
                case "p":
                    if (node.Attributes.Count > 0) return RawBlock(node, html);
                    return FinishParagraph(Inline(node.Children, html));

                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    // 見出しの中の改行（<br>）は Markdown の見出しに書けないので、HTML のまま。
                    if (node.Attributes.Count > 0 || Descendants(node).Any(d => d.Name == "br")) return RawBlock(node, html);
                    var text = FinishParagraph(Inline(node.Children, html)).Replace("\n", " ");
                    return new string('#', node.Name[1] - '0') + " " + text;
                }

                case "ul":
                case "ol":
                    return List(node, html) ?? RawBlock(node, html);

                case "blockquote":
                {
                    if (node.Attributes.Count > 0) return RawBlock(node, html);
                    var inner = string.Join("\n\n", Blocks(node.Children, html));
                    return string.Join("\n", inner.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));
                }

                case "pre":
                    return Code(node, html) ?? RawBlock(node, html);

                case "hr":
                    return node.Attributes.Count > 0 ? RawBlock(node, html) : "---";

                case "table":
                    return Table(node, html) ?? RawBlock(node, html);

                case "div":
                    // 属性の無い div は見た目に何もしないので、包みを外して中身だけにする（表を包む <div dir="ltr"> など）。
                    if (node.Attributes.Count > 0) return RawBlock(node, html);
                    return string.Join("\n\n", Blocks(node.Children, html));

                default:
                    return RawBlock(node, html);
            }
        }

        /// <summary>
        /// 元の HTML をそのまま残す。Markdown の HTML の塊は空行で終わるので、中の空行は詰める
        /// （詰めないと、空行から後ろが Markdown として読まれてしまう）。
        /// </summary>
        private static string RawBlock(Node node, string html)
        {
            var raw = html.Substring(node.Start, node.End - node.Start).Trim('\n');

            // pre・script・style・textarea で始まる塊は、Markdown では閉じタグまでが1つの塊（空行で切れない）。中身はそのまま（2026-10-30 のレビュー）。
            if (RawTextBlockStart.IsMatch(raw)) return raw;

            // 中にある pre の空行は、詰めずに改行の文字参照（&#10;）にする。見た目は同じ改行で、Markdown の塊も切れない。
            raw = PreBody.Replace(raw, match => Regex.Replace(match.Value, @"\n(?=[ \t]*\n)", "&#10;"));
            return Regex.Replace(raw, @"\n[ \t]*(?=\n)", string.Empty);
        }

        private static readonly Regex RawTextBlockStart = new Regex(@"^<(pre|script|style|textarea)[\s>]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PreBody = new Regex(@"<pre[\s>].*?</pre>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static string RawInline(Node node, string html)
        {
            return html.Substring(node.Start, node.End - node.Start);
        }

        // ---------------------------------------------------------------
        // リスト
        // ---------------------------------------------------------------

        private static string List(Node list, string html)
        {
            foreach (var attribute in list.Attributes.Keys)
            {
                if (!(list.Name == "ol" && attribute.Equals("start", StringComparison.OrdinalIgnoreCase))) return null;
            }

            var items = new List<List<string>>();
            foreach (var child in list.Children)
            {
                if (child.IsText && string.IsNullOrWhiteSpace(child.Text)) continue;
                if (!child.IsElement || child.Name != "li" || child.Attributes.Count > 0) return null;

                items.Add(ListItem(child, html));
            }

            if (items.Count == 0) return null;

            var number = 1;
            string start;
            if (list.Name == "ol" && list.Attributes.TryGetValue("start", out start) && !int.TryParse(start, out number)) number = 1;

            // 項目が <p> で包まれていれば（<li><p>…</p></li>）、項目の間も空ける（ゆるいリスト）。
            // 包まれていなければ、入れ子のリストがあっても詰める（空けると、HTML に戻したときに <p> が付く）。
            var loose = list.Children.Any(li => li.IsElement && li.Children.Any(c => c.IsElement && c.Name == "p"));

            var builder = new StringBuilder();
            for (var i = 0; i < items.Count; i++)
            {
                var marker = list.Name == "ol" ? (number + i) + ". " : "- ";
                var indent = new string(' ', marker.Length);

                var body = string.Join(loose ? "\n\n" : "\n", items[i]);
                var lines = body.Split('\n');

                if (i > 0) builder.Append(loose ? "\n\n" : "\n");
                builder.Append(marker).Append(lines[0]);
                for (var j = 1; j < lines.Length; j++)
                {
                    builder.Append('\n');
                    if (lines[j].Length > 0) builder.Append(indent).Append(lines[j]);
                }
            }

            return builder.ToString();
        }

        /// <summary>項目の中身の塊。チェックボックス（タスクリスト）は [ ] / [x] にする。</summary>
        private static List<string> ListItem(Node li, string html)
        {
            var children = li.Children.ToList();
            var task = string.Empty;

            var first = children.FirstOrDefault(c => !(c.IsText && string.IsNullOrWhiteSpace(c.Text)));
            string type;
            if (first != null && first.IsElement && first.Name == "input"
                && first.Attributes.TryGetValue("type", out type) && type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
            {
                task = first.Attributes.ContainsKey("checked") ? "[x] " : "[ ] ";
                children.Remove(first);
            }

            var blocks = Blocks(children, html);
            if (blocks.Count == 0) blocks.Add(string.Empty);
            blocks[0] = task + blocks[0];
            return blocks;
        }

        // ---------------------------------------------------------------
        // コード
        // ---------------------------------------------------------------

        private static string Code(Node pre, string html)
        {
            if (pre.Attributes.Count > 0) return null;

            var content = pre.Children.Where(c => !(c.IsText && string.IsNullOrWhiteSpace(c.Text))).ToList();
            var language = string.Empty;
            string text;

            if (content.Count == 1 && content[0].IsElement && content[0].Name == "code")
            {
                var code = content[0];
                foreach (var attribute in code.Attributes)
                {
                    if (!attribute.Key.Equals("class", StringComparison.OrdinalIgnoreCase)) return null;

                    var match = Regex.Match(attribute.Value, @"^\s*language-(\S+)\s*$");
                    if (!match.Success) return null;
                    language = match.Groups[1].Value;
                }

                if (code.Children.Any(c => !c.IsText)) return null;   // 色付けの span などが入っていれば HTML のまま
                text = string.Concat(code.Children.Select(c => c.Text));
            }
            else
            {
                if (pre.Children.Any(c => !c.IsText)) return null;
                text = string.Concat(pre.Children.Select(c => c.Text));
            }

            if (text.EndsWith("\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);

            var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
            return fence + language + "\n" + text + "\n" + fence;
        }

        private static int LongestRun(string text, char c)
        {
            int longest = 0, run = 0;
            foreach (var ch in text)
            {
                run = ch == c ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            return longest;
        }

        // ---------------------------------------------------------------
        // 表
        // ---------------------------------------------------------------

        /// <summary>Markdown の表で書けるとき（見出しの行がある・セルの結合が無い・セルの中が1行）だけ、表にする。</summary>
        private static string Table(Node table, string html)
        {
            if (table.Attributes.Count > 0) return null;

            var rows = new List<Node>();
            var headerRows = 0;
            foreach (var child in table.Children)
            {
                if (child.IsText && string.IsNullOrWhiteSpace(child.Text)) continue;
                if (!child.IsElement || child.Attributes.Count > 0) return null;

                if (child.Name == "tr")
                {
                    rows.Add(child);
                }
                else if (child.Name == "thead" || child.Name == "tbody" || child.Name == "tfoot")
                {
                    foreach (var row in child.Children)
                    {
                        if (row.IsText && string.IsNullOrWhiteSpace(row.Text)) continue;
                        if (!row.IsElement || row.Name != "tr" || row.Attributes.Count > 0) return null;
                        rows.Add(row);
                        if (child.Name == "thead") headerRows++;
                    }
                }
                else
                {
                    return null;
                }
            }

            if (rows.Count == 0) return null;

            var cells = new List<List<Node>>();
            var contents = new Dictionary<Node, List<Node>>();
            foreach (var row in rows)
            {
                var list = new List<Node>();
                foreach (var cell in row.Children)
                {
                    if (cell.IsText && string.IsNullOrWhiteSpace(cell.Text)) continue;
                    if (!cell.IsElement || (cell.Name != "td" && cell.Name != "th")) return null;
                    if (cell.Attributes.Keys.Any(key => !key.Equals("style", StringComparison.OrdinalIgnoreCase)
                                                        && !key.Equals("align", StringComparison.OrdinalIgnoreCase))) return null;
                    // セルの中の | は、文字のものだけ \| で逃がせる。コード・属性・コメントの中の | は逃がせない（\ が残る）ので、表を HTML のまま。
                    if (Descendants(cell).Any(d => (d.IsText && d.Parent != null && d.Parent.Name == "code" && d.Text.IndexOf('|') >= 0)
                                                   || (d.IsElement && d.Attributes.Values.Any(v => v.IndexOf('|') >= 0))
                                                   || (d.IsComment && d.Text.IndexOf('|') >= 0))) return null;

                    var content = CellContent(cell);
                    if (content == null || content.Any(c => c.IsElement && (BlockElements.Contains(c.Name) || c.Name == "br"))) return null;
                    contents[cell] = content;
                    list.Add(cell);
                }

                cells.Add(list);
            }

            // 見出しの行: thead が1行、または thead が無く1行目が全部 th。
            // どちらでもなくても、1行目のセルが全部まるごと太字なら、それを見出しの行にする（見出しは太字で出るので、太字は外す。#6758）。
            if (headerRows > 1) return null;
            if (headerRows == 0 && !cells[0].All(cell => cell.Name == "th"))
            {
                var bold = cells[0].Select(cell => WholeBold(contents[cell])).ToList();
                if (bold.Any(inner => inner == null)) return null;
                for (var i = 0; i < cells[0].Count; i++) contents[cells[0][i]] = bold[i];
            }

            var columns = cells[0].Count;
            if (columns == 0 || cells.Any(row => row.Count != columns)) return null;

            var builder = new StringBuilder();
            for (var r = 0; r < cells.Count; r++)
            {
                builder.Append('|');
                foreach (var cell in cells[r])
                {
                    var text = FinishParagraph(Inline(contents[cell], html));
                    if (text.IndexOf('\n') >= 0) return null;
                    builder.Append(' ').Append(text.Replace("|", "\\|")).Append(" |");
                }

                builder.Append('\n');

                if (r == 0)
                {
                    builder.Append('|');
                    foreach (var cell in cells[0]) builder.Append(' ').Append(AlignmentOf(cell)).Append(" |");
                    builder.Append('\n');
                }
            }

            return builder.ToString().TrimEnd('\n');
        }

        private static IEnumerable<Node> Descendants(Node node)
        {
            foreach (var child in node.Children)
            {
                yield return child;
                foreach (var grand in Descendants(child)) yield return grand;
            }
        }

        /// <summary>
        /// セルの中身。段落1つに包まれていれば（<td><p>…</p></td>）、包みを外した中身。段落が2つ以上なら null（1行に書けない）。
        /// </summary>
        private static List<Node> CellContent(Node cell)
        {
            var children = cell.Children.Where(c => !(c.IsText && string.IsNullOrWhiteSpace(c.Text))).ToList();
            var paragraphs = children.Where(c => c.IsElement && c.Name == "p").ToList();
            if (paragraphs.Count == 0) return cell.Children;
            if (paragraphs.Count > 1 || children.Count > 1 || paragraphs[0].Attributes.Count > 0) return null;
            return paragraphs[0].Children;
        }

        /// <summary>中身がまるごと1つの太字（<strong>…</strong>）なら、その中身。そうでなければ null。</summary>
        private static List<Node> WholeBold(List<Node> content)
        {
            var children = content.Where(c => !(c.IsText && string.IsNullOrWhiteSpace(c.Text))).ToList();
            if (children.Count != 1 || !children[0].IsElement || (children[0].Name != "strong" && children[0].Name != "b")
                || children[0].Attributes.Count > 0) return null;
            return children[0].Children;
        }

        private static string AlignmentOf(Node cell)
        {
            string value;
            var align = cell.Attributes.TryGetValue("align", out value) ? value : null;

            string style;
            if (cell.Attributes.TryGetValue("style", out style))
            {
                var match = Regex.Match(style, @"text-align\s*:\s*(left|right|center)", RegexOptions.IgnoreCase);
                if (match.Success) align = match.Groups[1].Value;
            }

            switch ((align ?? string.Empty).ToLowerInvariant())
            {
                case "left": return ":---";
                case "right": return "---:";
                case "center": return ":---:";
                default: return "---";
            }
        }

        // ---------------------------------------------------------------
        // 段落の中
        // ---------------------------------------------------------------

        private static string Inline(List<Node> nodes, string html)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < nodes.Count; i++)
                AppendInline(builder, nodes[i], i + 1 < nodes.Count ? nodes[i + 1] : null, html);
            return builder.ToString();
        }

        private static void AppendInline(StringBuilder builder, Node node, Node next, string html)
        {
            if (node.IsText)
            {
                builder.Append(EscapeText(node.Text));
                return;
            }

            if (node.IsComment)
            {
                builder.Append(node.Text);
                return;
            }

            switch (node.Name)
            {
                case "br":
                    // 強い改行（行末の \）。保存すると <br /> に戻る。
                    // 前は段落の中の改行（WordPress のクラシックが表示するときに <br> にする）にしていたが、
                    // ブロックエディタの記事ではそれが効かず、<br><br> も1つになった（2026-10-30 のレビュー）。
                    builder.Append("\\\n");
                    return;

                case "strong":
                case "b":
                    AppendEmphasis(builder, node, next, "**", html);
                    return;

                case "em":
                case "i":
                    AppendEmphasis(builder, node, next, "*", html);
                    return;

                case "del":
                case "s":
                    AppendEmphasis(builder, node, next, "~~", html);
                    return;

                case "code":
                {
                    if (node.Attributes.Count > 0 || node.Children.Any(c => !c.IsText))
                    {
                        builder.Append(RawInline(node, html));
                        return;
                    }

                    var text = string.Concat(node.Children.Select(c => c.Text)).Replace('\n', ' ');
                    if (text.Length == 0) return;

                    var ticks = new string('`', LongestRun(text, '`') + 1);
                    var pad = text.StartsWith("`", StringComparison.Ordinal) || text.EndsWith("`", StringComparison.Ordinal) ? " " : string.Empty;
                    builder.Append(ticks).Append(pad).Append(text).Append(pad).Append(ticks);
                    return;
                }

                case "a":
                {
                    string href;
                    if (!node.Attributes.TryGetValue("href", out href) || node.Attributes.Keys.Any(key =>
                            !key.Equals("href", StringComparison.OrdinalIgnoreCase) && !key.Equals("title", StringComparison.OrdinalIgnoreCase)))
                    {
                        builder.Append(RawInline(node, html));
                        return;
                    }

                    // 文字が URL そのもの（<a href="https://…">https://…</a>）なら、<URL> と書く（CommonMark の自動リンク。[URL](URL) と二重に書かなくて済む）。
                    // 前は URL をそのまま書いていたが、アップロードでは裸の URL をリンクにしなくなったので、<…> で囲む（2026-10-30 のレビュー）。
                    if (!node.Attributes.ContainsKey("title") && node.Children.All(c => c.IsText)
                        && string.Concat(node.Children.Select(c => c.Text)) == href
                        && Regex.IsMatch(href, @"^https?://[^\s<>]+$"))
                    {
                        builder.Append('<').Append(href).Append('>');
                        return;
                    }

                    var label = Inline(node.Children, html);
                    if (label.IndexOf('\n') >= 0 || label.Length == 0)
                    {
                        builder.Append(RawInline(node, html));
                        return;
                    }

                    builder.Append('[').Append(label).Append("](").Append(LinkTarget(href)).Append(TitlePart(node)).Append(')');
                    return;
                }

                case "img":
                {
                    string src;
                    if (!node.Attributes.TryGetValue("src", out src) || node.Attributes.Keys.Any(key =>
                            !key.Equals("src", StringComparison.OrdinalIgnoreCase) && !key.Equals("alt", StringComparison.OrdinalIgnoreCase)
                            && !key.Equals("title", StringComparison.OrdinalIgnoreCase)))
                    {
                        builder.Append(RawInline(node, html));
                        return;
                    }

                    string alt;
                    node.Attributes.TryGetValue("alt", out alt);
                    builder.Append("![").Append(EscapeText(alt ?? string.Empty).Replace('\n', ' ')).Append("](")
                           .Append(LinkTarget(src)).Append(TitlePart(node)).Append(')');
                    return;
                }

                case "span":
                    // 属性の無い span は見た目に何もしないので、包みを外す。
                    if (node.Attributes.Count > 0) builder.Append(RawInline(node, html));
                    else for (var i = 0; i < node.Children.Count; i++)
                        AppendInline(builder, node.Children[i], i + 1 < node.Children.Count ? node.Children[i + 1] : next, html);
                    return;

                default:
                    // sup・u など Markdown に無いもの、段落の中に書かれた塊の要素は、HTML のまま。
                    builder.Append(RawInline(node, html));
                    return;
            }
        }

        /// <summary>
        /// 太字・斜体・打消し。CommonMark は、記号の内側が句読点・括弧で、外側が文字だと強調にならない（例: 文字**「語」**文字）。
        /// そうなるときは HTML のまま残す（Markdown にすると、保存したときに強調が消えるため）。
        /// 属性の付いたもの（style など）も HTML のまま。
        /// </summary>
        private static void AppendEmphasis(StringBuilder builder, Node node, Node next, string mark, string html)
        {
            if (node.Attributes.Count > 0)
            {
                builder.Append(RawInline(node, html));
                return;
            }

            var inner = Inline(node.Children, html);
            var trimmed = inner.Trim(' ', '\n', '\t');
            if (trimmed.Length == 0)
            {
                builder.Append(inner);
                return;
            }

            var leading = inner.Substring(0, inner.IndexOf(trimmed, StringComparison.Ordinal));
            var trailing = inner.Substring(leading.Length + trimmed.Length);

            var before = leading.Length > 0 ? ' ' : (builder.Length > 0 ? builder[builder.Length - 1] : ' ');
            var after = trailing.Length > 0 ? ' ' : FirstCharOf(next);

            var opens = !IsPunctuation(trimmed[0]) || char.IsWhiteSpace(before) || IsPunctuation(before);
            var closes = !IsPunctuation(trimmed[trimmed.Length - 1]) || char.IsWhiteSpace(after) || IsPunctuation(after);

            // 直前が同じ記号（太字が2つ隣り合う <strong>c</strong><strong>d</strong> → **c****d**）だと、記号が文字として残る。HTML のまま。
            var touching = leading.Length == 0 && builder.Length > 0 && builder[builder.Length - 1] == mark[0];

            if (!opens || !closes || touching || trimmed.IndexOf("\n\n", StringComparison.Ordinal) >= 0)
            {
                builder.Append(RawInline(node, html));
                return;
            }

            builder.Append(leading).Append(mark).Append(trimmed).Append(mark).Append(trailing);
        }

        private static bool IsPunctuation(char c)
        {
            return char.IsPunctuation(c) || char.IsSymbol(c);
        }

        /// <summary>次の兄弟の最初の文字。無ければ空白とみなす。</summary>
        private static char FirstCharOf(Node node)
        {
            while (node != null)
            {
                if (node.IsText) return node.Text.Length > 0 ? node.Text[0] : ' ';
                if (node.IsComment || node.Name == "br" || node.Children.Count == 0) return ' ';
                node = node.Children[0];
            }

            return ' ';
        }

        private static string LinkTarget(string url)
        {
            url = url ?? string.Empty;
            if (url.Length == 0 || url.IndexOfAny(new[] { ' ', '(', ')', '<', '>' }) >= 0)
                return "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">";
            return url;
        }

        private static string TitlePart(Node node)
        {
            string title;
            if (!node.Attributes.TryGetValue("title", out title) || string.IsNullOrEmpty(title)) return string.Empty;
            return " \"" + title.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>見出し1行ぶんの文字（記事のタイトル）を、Markdown の記号として読まれないようにする。</summary>
        public static string EscapeLine(string text)
        {
            var escaped = EscapeText(WebUtility.HtmlDecode(text ?? string.Empty).Replace("\r", string.Empty).Replace('\n', ' ').Trim());

            // 末尾の「 #」は、見出しの閉じの記号として読まれて消える。逃がす。
            return Regex.Replace(escaped, @"(\s)(#+)$", m => m.Groups[1].Value + "\\" + m.Groups[2].Value);
        }

        private static readonly Regex EntityAt = new Regex(@"\G&#?[A-Za-z0-9]+;", RegexOptions.Compiled);

        /// <summary>本文の文字のうち、Markdown の記号として読まれてしまうものを逃がす。行頭の記号は FinishParagraph で。</summary>
        private static string EscapeText(string text)
        {
            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\\': case '*': case '_': case '`': case '[': case ']': case '~':
                        builder.Append('\\').Append(c);
                        break;
                    case '&':
                        // 文字参照に見える &（&copy; と書いた文字）は逃がす。
                        if (EntityAt.Match(text, i).Success) builder.Append('\\');
                        builder.Append(c);
                        break;
                    case '\u00A0':
                        // &nbsp; は文字参照のまま書く。文字のままだと、Markdown が段落の頭と末尾の空白として削る（&nbsp; だけの段落・字下げが消える）。
                        builder.Append("&nbsp;");
                        break;
                    case '<':
                        if (i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/' || text[i + 1] == '!' || text[i + 1] == '?'))
                            builder.Append('\\');
                        builder.Append(c);
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        private static readonly Regex LineStartMarker = new Regex(
            @"^(#{1,6}(?=[ \t]|$)|>|[-+*](?=[ \t]|$)|=+[ \t]*$|-+[ \t]*$|```|~~~)", RegexOptions.Compiled);

        private static int TrailingBackslashes(string line)
        {
            var count = 0;
            for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) count++;
            return count;
        }

        private static readonly Regex LoneTag = new Regex(@"^</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>$", RegexOptions.Compiled);

        private static readonly Regex OrderedMarker = new Regex(@"^(\d{1,9})([.)])(?=[ \t]|$)", RegexOptions.Compiled);

        /// <summary>
        /// 段落を仕上げる。行頭・行末の空白を落とし（行頭の空白4つはコードに、行末の空白2つは改行になってしまう）、
        /// 行頭に来た見出し・引用・リストの記号を逃がす。
        /// </summary>
        private static string FinishParagraph(string text)
        {
            // 行頭・行末の空白とタブは落とす。&nbsp;（U+00A0）は落とさない（Markdown は空白とみなさないので害が無く、
            // 落とすと &nbsp; だけの段落（行の空き）や行頭の字下げが消える。2026-10-30 のレビュー）。
            var lines = text.Split('\n').Select(line => line.Trim(' ', '\t')).ToList();

            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;

                if (LineStartMarker.IsMatch(line)) line = "\\" + line;
                else line = OrderedMarker.Replace(line, "$1\\$2");

                lines[i] = line;
            }

            // 段落の中に空行が残ると、別の段落に割れる。詰める。
            lines = lines.Where(line => line.Length > 0).ToList();

            // 1行目がタグ1つだけ（<img class="aligncenter" …> など HTML のまま残したもの）で、次の行があると、
            // Markdown はその段落をまるごと HTML の塊として読み、2行目からの逃がし（\* など）がそのまま送られて、往復のたびに \ が増えた。
            // 1行目の末尾に強い改行（\）を付けて、タグだけの行でなくする（WordPress のクラシックが表示するときの <br> と同じ見た目。2026-10-30 のレビュー）。
            if (lines.Count > 1 && LoneTag.IsMatch(lines[0])) lines[0] += "\\";

            // 段落の最後の強い改行（<br> で終わる段落）は、Markdown では \ が文字として出てしまう。最後の行の末尾の \ を外す
            // （\ が奇数個なら最後の1つが強い改行の印。偶数個なら文字の \ を逃がしたもの）。
            while (lines.Count > 0 && TrailingBackslashes(lines[lines.Count - 1]) % 2 == 1)
            {
                var last = lines[lines.Count - 1];
                last = last.Substring(0, last.Length - 1).TrimEnd(' ', '\t');
                if (last.Length > 0) lines[lines.Count - 1] = last;
                else lines.RemoveAt(lines.Count - 1);
            }

            return string.Join("\n", lines);
        }
    }
}
