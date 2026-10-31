using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tsuzuri.Markdown;

namespace Tsuzuri.Export
{
    /// <summary>書き出しの結果。終わったあとに知らせる。</summary>
    public sealed class ExportReport
    {
        public string DocxPath;

        /// <summary>添付をコピーしたフォルダ。添付が無ければ null。</summary>
        public string AttachmentFolder;

        public int AttachmentCount;

        /// <summary>ルートから辿れず、書き出しに含めなかったページの数。</summary>
        public int OrphanCount;

        /// <summary>確かめてもらいたいこと。書き出し自体は止めない。</summary>
        public readonly List<string> Problems = new List<string>();
    }

    /// <summary>
    /// 文書を Word（.docx）に書き出す（SPEC 13章）。社外に渡す資料の下書きを作るためのもの。
    ///
    /// - サブページはリンクの場所に展開する。手元のファイルには触れず、「戻す」を全ページに
    ///   かけたのと同じ結果を書き出しの中だけで作る
    /// - 別の Markdown ファイルへのリンクも同じように展開する
    /// - 画像は docx に埋め込む。大きさは画面と同じ計算で、本文の幅を超えるものだけ縮める
    /// - それ以外の添付は「※参照 ○○」という文字に置き換え、ファイルは docx の隣のフォルダにコピーする
    /// - URL は Word のリンクとして残す
    /// </summary>
    public sealed class DocxExporter
    {
        /// <summary>今どこを書いているか。展開のたびに複製して下へ渡す。</summary>
        private sealed class Context
        {
            /// <summary>相対パスとページ番号の解決に使う文書。</summary>
            public PageDocument Document;

            public int Indent;
            public int ListLevel;
            public bool Quote;
            public bool InTable;
            public bool Header;
            public string Align;

            /// <summary>画像の幅の上限（EMU）。</summary>
            public long ImageWidth;

            public Context Clone()
            {
                return (Context)MemberwiseClone();
            }
        }

        /// <summary>
        /// 書きかけの段落。展開や画像で段落を途中で切るため、すぐには書かずに溜めておく。
        /// </summary>
        private sealed class Segment
        {
            public ParagraphFormat Format;
            public readonly List<Piece> Pieces = new List<Piece>();
        }

        private sealed class EmbeddedImage
        {
            public byte[] Data;
            public string Extension;
            public int Width;
            public int Height;
        }

        private static readonly Regex BreakTag = new Regex(@"^<br\s*/?>$", RegexOptions.IgnoreCase);

        private readonly Func<string, PageDocument> _open;
        private readonly double _scale;

        private readonly Dictionary<string, PageDocument> _documents =
            new Dictionary<string, PageDocument>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>添付の元のパス → 書き出し先での名前。</summary>
        private readonly Dictionary<string, string> _attachments =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private DocxBuilder _docx;
        private ExportReport _report;

        /// <summary>
        /// 直前の見出しのレベル。本文はこの深さまで字下げする（エディタの SPEC 8.4 と同じ規則）。
        /// ページを展開しても引き継ぐ。「戻す」をかけたあとの1本の文書として数えるため。
        /// </summary>
        private int _depth;

        /// <param name="open">
        /// パスから文書を得る。編集中の文書は保存前の内容を使いたいので、呼び出し側に任せる。
        /// </param>
        /// <param name="scale">画面の拡大率。画像の大きさを画面と揃えるのに使う。</param>
        /// <param name="page">用紙・向き・余白。画像を縮める上限（本文の幅）もここから決まる。</param>
        public DocxExporter(Func<string, PageDocument> open, double scale, PageSetup page)
        {
            _open = open;
            _scale = scale > 0 ? scale : 1.0;
            _page = page;
        }

        private readonly PageSetup _page;

        /// <summary>サブページを本文に戻して書き出すか。false なら除外する（SPEC 13章）。</summary>
        private bool _includeSubpages;

        /// <param name="pageId">
        /// 書き出し始めるページ。ルートならファイル全体、サブページならそのページと、その下のサブページだけ（SPEC 13章）。
        /// </param>
        /// <param name="includeSubpages">
        /// true なら、リンクしているサブページを本文に展開する（「戻す」をかけた1本の文書として）。
        /// false なら、サブページは書き出さない。どちらも、サブページへのリンクの記号（[>>](#ID)）は書き出さない。
        /// </param>
        public ExportReport Export(PageDocument document, string path, string pageId, bool includeSubpages)
        {
            _includeSubpages = includeSubpages;
            _docx = new DocxBuilder(_page);
            _report = new ExportReport();
            _report.DocxPath = path;
            _depth = 0;

            _documents[FullPath(document.FilePath)] = document;

            var context = new Context();
            context.Document = document;
            context.ImageWidth = _docx.TextWidthEmu;

            var start = string.IsNullOrEmpty(pageId) ? PageDocument.RootId : pageId;
            ExportPage(document, start, context);

            // どこからもリンクされていないページの数は、ファイル全体をサブページごと書き出したときだけ知らせる。
            // サブページだけのとき・サブページを除外したときは、ほかのページを書き出さないのが当たり前なので数えない。
            _report.OrphanCount = start == PageDocument.RootId && includeSubpages ? document.FindUnreferenced().Count : 0;

            SaveAtomically(path);
            CopyAttachments(path);

            return _report;
        }

        // =====================================================================
        // ページと段落
        // =====================================================================

        private void ExportPage(PageDocument document, string pageId, Context context)
        {
            _expanded.Add(PageKey(document, pageId));

            var text = document.TextOf(pageId) ?? string.Empty;
            var tree = Markdig.Markdown.Parse(text, MarkdownParser.Pipeline);

            var inner = context.Clone();
            inner.Document = document;

            WriteBlocks(tree, inner);
        }

        private void WriteBlocks(ContainerBlock container, Context context)
        {
            foreach (var block in container)
            {
                WriteBlock(block, context);
            }
        }

        private void WriteBlock(Block block, Context context)
        {
            var heading = block as HeadingBlock;
            if (heading != null)
            {
                var level = Math.Max(1, Math.Min(6, heading.Level));
                WriteInlines(heading.Inline, Format(context, "Heading" + level), context, null);

                if (!context.InTable) _depth = level;
                return;
            }

            var paragraph = block as ParagraphBlock;
            if (paragraph != null)
            {
                WriteInlines(paragraph.Inline, Format(context, null), context, null);
                return;
            }

            var code = block as CodeBlock;
            if (code != null)
            {
                WriteCode(code, context);
                return;
            }

            if (block is ThematicBreakBlock)
            {
                var rule = Format(context, null);
                rule.Rule = true;
                _docx.Paragraph(rule, new Piece[0]);
                return;
            }

            // ページの印などの HTML は紙には出さない。
            if (block is HtmlBlock) return;

            var list = block as ListBlock;
            if (list != null)
            {
                WriteList(list, context);
                return;
            }

            var quote = block as QuoteBlock;
            if (quote != null)
            {
                var inner = context.Clone();
                inner.Quote = true;
                WriteBlocks(quote, inner);
                return;
            }

            var table = block as Table;
            if (table != null)
            {
                WriteTable(table, context);
                return;
            }

            var footnotes = block as FootnoteGroup;
            if (footnotes != null)
            {
                WriteFootnotes(footnotes, context);
                return;
            }

            var container = block as ContainerBlock;
            if (container != null)
            {
                WriteBlocks(container, context);
                return;
            }

            // 参照リンクの定義などは Inline を持たない。
            var leaf = block as LeafBlock;
            if (leaf != null && leaf.Inline != null)
            {
                WriteInlines(leaf.Inline, Format(context, null), context, null);
            }
        }

        /// <summary>
        /// 段落の書式。字下げの決め方:
        ///
        /// - 見出しは、スタイル（見出し 1〜6）が自分の段の字下げを持つ
        /// - ただの本文は「本文 1〜6」スタイルにし、字下げはスタイルに持たせる。
        ///   Word 側でスタイルの字下げを直せば、同じ深さの段落が全部そろって動く
        /// - 箇条書き・引用・コードなどは、深さの分に自分の字下げを足して段落に直接書く
        /// </summary>
        private ParagraphFormat Format(Context context, string style)
        {
            var format = new ParagraphFormat();
            format.Align = context.Align;

            var depth = DepthOf(context);

            if (style != null && style.StartsWith("Heading", StringComparison.Ordinal))
            {
                format.Style = style;
                return format;
            }

            if (style == null && !context.Quote && context.Indent == 0 && depth > 0)
            {
                format.Style = "Body" + depth;
                return format;
            }

            format.Style = style ?? (context.Quote ? "Quote" : null);

            var left = LeftIndent(context);
            if (left > 0) format.Indent = left;

            return format;
        }

        /// <summary>表の中と脚注は字下げしない。</summary>
        private int DepthOf(Context context)
        {
            return context.InTable ? 0 : Math.Min(_depth, DocxBuilder.MaxDepth);
        }

        /// <summary>段落の左端（twip）。見出しの深さ＋箇条書きの段＋引用の段。</summary>
        private int LeftIndent(Context context)
        {
            return DepthOf(context) * DocxBuilder.DepthStep
                + context.Indent
                + (context.Quote ? DocxBuilder.IndentStep : 0);
        }

        private void WriteInlines(ContainerInline inline, ParagraphFormat format, Context context, string prefix)
        {
            if (inline == null) return;

            var segment = new Segment();
            segment.Format = format;

            // 表の見出し行も太字にしない（2026-10-16）。太字は本文の **強調** だけ（SPEC 13章）。
            var style = RunStyle.None;

            AddText(segment, prefix, style);
            WriteChildren(inline, segment, style, context);
            Flush(segment, context);
        }

        /// <summary>溜めた段落を書く。中身が空白だけなら捨てる。</summary>
        private void Flush(Segment segment, Context context)
        {
            Trim(segment.Pieces);

            if (HasVisibleText(segment.Pieces))
            {
                _docx.Paragraph(segment.Format, segment.Pieces);
                segment.Format = Continuation(segment.Format);
            }

            segment.Pieces.Clear();
        }

        /// <summary>
        /// 途中で切れた段落の続き。番号はもう振らず、項目の本文の位置（Indent）に揃える。
        /// 見出しの続きはその見出しの本文にする（同じ見出しがアウトラインに2つ並ばないように）。
        /// </summary>
        private static ParagraphFormat Continuation(ParagraphFormat format)
        {
            var next = format.Clone();

            if (next.NumberingId > 0)
            {
                next.NumberingId = 0;
                next.Level = 0;
                next.Hanging = 0;
            }

            // Heading2 の続きは Body2（見出しの配下と同じ深さ）。
            if (next.Style != null && next.Style.StartsWith("Heading", StringComparison.Ordinal))
                next.Style = "Body" + next.Style.Substring("Heading".Length);

            return next;
        }

        private static void Trim(List<Piece> pieces)
        {
            while (pieces.Count > 0 && IsBlank(pieces[0])) pieces.RemoveAt(0);
            while (pieces.Count > 0 && IsBlank(pieces[pieces.Count - 1])) pieces.RemoveAt(pieces.Count - 1);

            if (pieces.Count == 0) return;

            var first = pieces[0];
            if (first.Kind == PieceKind.Text) first.Text = first.Text.TrimStart();

            var last = pieces[pieces.Count - 1];
            if (last.Kind == PieceKind.Text) last.Text = last.Text.TrimEnd();
        }

        private static bool IsBlank(Piece piece)
        {
            if (piece.Kind == PieceKind.Break) return true;
            return piece.Kind == PieceKind.Text && piece.Text.Trim().Length == 0;
        }

        private static bool HasVisibleText(List<Piece> pieces)
        {
            foreach (var piece in pieces)
            {
                if (piece.Kind == PieceKind.Text && piece.Text.Trim().Length > 0) return true;
            }

            return false;
        }

        // =====================================================================
        // 行内の要素
        // =====================================================================

        private void WriteChildren(ContainerInline container, Segment segment, RunStyle style, Context context)
        {
            for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
            {
                WriteInline(inline, segment, style, context);
            }
        }

        private void WriteInline(Inline inline, Segment segment, RunStyle style, Context context)
        {
            var literal = inline as LiteralInline;
            if (literal != null)
            {
                AddText(segment, literal.Content.ToString(), style);
                return;
            }

            var code = inline as CodeInline;
            if (code != null)
            {
                AddText(segment, code.Content.ToString(), style | RunStyle.Code);
                return;
            }

            // 画面では Markdown の改行がそのまま行になって見えているので、紙でも改行にする。
            if (inline is LineBreakInline)
            {
                AddBreak(segment);
                return;
            }

            var emphasis = inline as EmphasisInline;
            if (emphasis != null)
            {
                WriteChildren(emphasis, segment, style | EmphasisStyle(emphasis), context);
                return;
            }

            var task = inline as TaskList;
            if (task != null)
            {
                AddText(segment, task.Checked ? "☑ " : "☐ ", style);
                return;
            }

            var footnote = inline as FootnoteLink;
            if (footnote != null)
            {
                if (!footnote.IsBackLink && footnote.Footnote != null)
                {
                    AddText(segment, footnote.Footnote.Order.ToString(CultureInfo.InvariantCulture),
                        style | RunStyle.Superscript);
                }
                return;
            }

            var auto = inline as AutolinkInline;
            if (auto != null)
            {
                BeginLink(segment, auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                AddText(segment, auto.Url, style | RunStyle.Link);
                EndLink(segment);
                return;
            }

            var link = inline as LinkInline;
            if (link != null)
            {
                WriteLink(link, segment, style, context);
                return;
            }

            var entity = inline as HtmlEntityInline;
            if (entity != null)
            {
                AddText(segment, entity.Transcoded.ToString(), style);
                return;
            }

            var html = inline as HtmlInline;
            if (html != null)
            {
                if (BreakTag.IsMatch(html.Tag ?? string.Empty)) AddBreak(segment);
                return;
            }

            var container = inline as ContainerInline;
            if (container != null) WriteChildren(container, segment, style, context);
        }

        /// <summary>~ は画面の解釈（打消し）に揃える。</summary>
        private static RunStyle EmphasisStyle(EmphasisInline emphasis)
        {
            switch (emphasis.DelimiterChar)
            {
                case '~': return RunStyle.Strike;
                case '^': return RunStyle.Superscript;
                case '+': return RunStyle.Underline;
                case '=': return RunStyle.Highlight;
                default: return emphasis.DelimiterCount >= 2 ? RunStyle.Bold : RunStyle.Italic;
            }
        }

        // =====================================================================
        // リンク
        // =====================================================================

        private void WriteLink(LinkInline link, Segment segment, RunStyle style, Context context)
        {
            var url = (link.Url ?? string.Empty).Trim();
            var label = PlainText(link);

            if (link.IsImage)
            {
                WriteImage(url, label, segment, style, context);
                return;
            }

            if (url.StartsWith("#", StringComparison.Ordinal))
            {
                var anchor = url.Substring(1);

                if (anchor.Length > 0 && context.Document.Contains(anchor))
                {
                    // サブページへのリンク。記号（>> など）は書き出さない。除外するなら本文も入れない。
                    if (_includeSubpages) Expand(context.Document, anchor, link, segment, style, context, false);
                    return;
                }

                // ページの中の見出しへのリンク。紙では飛べないので名前だけ残す。
                WriteChildren(link, segment, style, context);
                return;
            }

            if (IsWeb(url))
            {
                BeginLink(segment, url);
                if (label.Length > 0) WriteChildren(link, segment, style | RunStyle.Link, context);
                else AddText(segment, url, style | RunStyle.Link);
                EndLink(segment);
                return;
            }

            string fragment;
            var path = Resolve(context.Document.FilePath, url, out fragment);

            if (path == null)
            {
                WriteChildren(link, segment, style, context);
                return;
            }

            if (IsMarkdown(path))
            {
                var target = OpenDocument(path, url);
                if (target == null)
                {
                    WriteChildren(link, segment, style, context);
                    return;
                }

                var pageId = fragment.Length > 0 && target.Contains(fragment) ? fragment : PageDocument.RootId;
                Expand(target, pageId, link, segment, style, context, true);
                return;
            }

            AddText(segment, AttachmentText(label, path), style);
        }

        /// <summary>
        /// リンクの場所にページの本文を入れる。「戻す」と同じ結果になる。
        ///
        /// 同じページを2か所から指していたら、本文を入れるのは最初の1か所だけにする。互いにリンクし合うページがあっても止まる。
        /// </summary>
        /// <param name="keepLabel">
        /// 本文を入れられないときに、リンクの名前を残すか。別のファイルへのリンクは名前（ファイル名など）を残す。
        /// サブページへのリンクは記号（>> など）なので残さない。
        /// </param>
        private void Expand(PageDocument document, string pageId, LinkInline link, Segment segment,
            RunStyle style, Context context, bool keepLabel)
        {
            if (_expanded.Contains(PageKey(document, pageId)))
            {
                if (keepLabel) WriteChildren(link, segment, style, context);
                return;
            }

            // 表のセルの中には見出しや別の表を入れられない。
            if (context.InTable)
            {
                if (keepLabel) WriteChildren(link, segment, style, context);
                _report.Problems.Add("表の中のリンク先は展開していません: " + PlainText(link));
                return;
            }

            Flush(segment, context);
            ExportPage(document, pageId, context);
        }

        private PageDocument OpenDocument(string path, string url)
        {
            var full = FullPath(path);

            PageDocument document;
            if (_documents.TryGetValue(full, out document)) return document;

            // 読めなかったことも覚えておく。同じ知らせを何度も出さないため。
            _documents[full] = null;

            if (!File.Exists(full))
            {
                _report.Problems.Add("リンク先の文書が見つかりません: " + url);
                return null;
            }

            try
            {
                document = _open(full);
            }
            catch (Exception ex)
            {
                _report.Problems.Add("読めない文書を飛ばしました: " + full + "（" + ex.Message + "）");
                return null;
            }

            _documents[full] = document;
            return document;
        }

        private void BeginLink(Segment segment, string url)
        {
            var piece = new Piece();
            piece.Kind = PieceKind.LinkStart;
            piece.RelationshipId = _docx.AddHyperlink(url);
            segment.Pieces.Add(piece);
        }

        private static void EndLink(Segment segment)
        {
            var piece = new Piece();
            piece.Kind = PieceKind.LinkEnd;
            segment.Pieces.Add(piece);
        }

        private static void AddText(Segment segment, string text, RunStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;

            var piece = new Piece();
            piece.Kind = PieceKind.Text;
            piece.Text = text;
            piece.Style = style;
            segment.Pieces.Add(piece);
        }

        private static void AddBreak(Segment segment)
        {
            var piece = new Piece();
            piece.Kind = PieceKind.Break;
            segment.Pieces.Add(piece);
        }

        // =====================================================================
        // 添付
        // =====================================================================

        /// <summary>
        /// 「※参照 見積書.xlsx」または「概算の内訳 ※参照 見積書.xlsx」。
        /// 表示名がファイル名と同じなら、同じ名前を2度並べない。
        /// </summary>
        private string AttachmentText(string label, string path)
        {
            var name = Attach(path);
            var original = Path.GetFileName(path);
            var plain = label.Trim();

            var sameAsName = plain.Length == 0
                || SameName(plain, name)
                || SameName(plain, original)
                || SameName(plain, Path.GetFileNameWithoutExtension(name))
                || SameName(plain, Path.GetFileNameWithoutExtension(original));

            return sameAsName
                ? "※参照 " + name
                : plain + " ※参照 " + name;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>コピーする添付に加え、書き出し先での名前を返す。</summary>
        private string Attach(string path)
        {
            var full = FullPath(path);

            string name;
            if (_attachments.TryGetValue(full, out name)) return name;

            name = Path.GetFileName(full);

            if (!File.Exists(full))
            {
                if (_missing.Add(full)) _report.Problems.Add("添付ファイルが見つかりません: " + full);
                return name;
            }

            // 別の場所にある同じ名前のファイル。本文の「参照」もずらした名前にする。
            if (_names.Contains(name)) name = UniqueName(name);

            _names.Add(name);
            _attachments[full] = name;

            return name;
        }

        private string UniqueName(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);

            for (var n = 2; ; n++)
            {
                var candidate = stem + " (" + n + ")" + extension;
                if (!_names.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// 参照されている添付だけを「文書名_添付」フォルダにコピーする。
        /// 添付フォルダを丸ごと出すと、リンクを消したあとの残骸まで社外に出てしまう。
        /// </summary>
        private void CopyAttachments(string docxPath)
        {
            if (_attachments.Count == 0) return;

            var folder = Path.Combine(Path.GetDirectoryName(docxPath),
                Path.GetFileNameWithoutExtension(docxPath) + "_添付");

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                _report.Problems.Add("添付のフォルダを作れませんでした: " + folder + "（" + ex.Message + "）");
                return;
            }

            _report.AttachmentFolder = folder;

            foreach (var entry in _attachments)
            {
                var destination = Path.Combine(folder, entry.Value);

                try
                {
                    // 前回の書き出しと同じ場所から書き出し直した場合、元と先が同じファイルになる。
                    if (!SameName(FullPath(entry.Key), FullPath(destination)))
                        File.Copy(entry.Key, destination, true);

                    _report.AttachmentCount++;
                }
                catch (Exception ex)
                {
                    _report.Problems.Add("添付をコピーできませんでした: " + entry.Value + "（" + ex.Message + "）");
                }
            }
        }

        // =====================================================================
        // 画像
        // =====================================================================

        /// <summary>
        /// 画面に出ている画像は埋め込み、出ていないもの（SVG・URL・読めないもの）は「参照」にする。
        /// 見たままが引き継がれるようにするため。
        /// </summary>
        private void WriteImage(string url, string label, Segment segment, RunStyle style, Context context)
        {
            if (IsWeb(url))
            {
                // 外の画像は取りに行かない（画面と同じ）。URL を参照先として残す。
                AddText(segment, label.Length > 0 ? label + " ※参照 " : "※参照 ", style);
                BeginLink(segment, url);
                AddText(segment, url, style | RunStyle.Link);
                EndLink(segment);
                return;
            }

            string fragment;
            var path = Resolve(context.Document.FilePath, url, out fragment);

            if (path == null)
            {
                AddText(segment, label, style);
                return;
            }

            var svg = string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);
            var image = !svg && File.Exists(path) ? LoadImage(path) : null;

            if (image == null)
            {
                if (!svg && File.Exists(path))
                    _report.Problems.Add("画像として読めないため、添付として扱いました: " + path);

                AddText(segment, AttachmentText(label, path), style);
                return;
            }

            Flush(segment, context);

            // 画面と同じく、撮ったときの大きさ（ピクセル ÷ 画面の拡大率）で置く。
            var width = (long)Math.Round(image.Width / _scale * DocxBuilder.EmuPerDip);
            var height = (long)Math.Round(image.Height / _scale * DocxBuilder.EmuPerDip);

            var room = context.ImageWidth - LeftIndent(context) * DocxBuilder.EmuPerTwip;
            if (room > 0 && width > room)
            {
                height = (long)Math.Round((double)height * room / width);
                width = room;
            }

            // 高さも、用紙の本文の高さに収める（図エディタの縦に長いボードなど）。段落の間の空きの分、少し残す。
            var headroom = (long)(_docx.TextHeightEmu * 0.92);
            if (headroom > 0 && height > headroom)
            {
                width = (long)Math.Round((double)width * headroom / height);
                height = headroom;
            }

            _docx.Image(Format(context, null), image.Data, image.Extension, width, height, label);
        }

        private static EmbeddedImage LoadImage(string path)
        {
            try
            {
                var data = File.ReadAllBytes(path);

                BitmapFrame frame;
                int orientation;
                using (var stream = new MemoryStream(data))
                {
                    var decoder = BitmapDecoder.Create(stream,
                        BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count == 0) return null;
                    frame = decoder.Frames[0];
                    orientation = ImageOrientation.Read(frame);   // 印は、読んでいる間に読む
                }

                var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

                // 写真の向きの印（EXIF）があれば、向きを直した画像を入れる（本文の表示と同じ。2026-10-18）。
                // Word が印を見るとは限らないので、直したものを入れ直す。写真は JPEG のまま（PNG にすると大きくなる）。
                if (orientation != 1)
                {
                    var upright = ImageOrientation.Apply(frame, orientation);
                    var jpeg = extension == "jpg" || extension == "jpeg";
                    BitmapEncoder rotated = jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
                    rotated.Frames.Add(BitmapFrame.Create(upright));

                    using (var output = new MemoryStream())
                    {
                        rotated.Save(output);
                        data = output.ToArray();
                    }

                    var turned = new EmbeddedImage();
                    turned.Data = data;
                    turned.Extension = jpeg ? "jpg" : "png";
                    turned.Width = upright.PixelWidth;
                    turned.Height = upright.PixelHeight;
                    return turned;
                }

                // webp など、古い Word が開けない形式は PNG にしてから入れる。
                if (!DocxBuilder.CanEmbed(extension))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));

                    using (var output = new MemoryStream())
                    {
                        encoder.Save(output);
                        data = output.ToArray();
                    }

                    extension = "png";
                }

                var image = new EmbeddedImage();
                image.Data = data;
                image.Extension = extension;
                image.Width = frame.PixelWidth;
                image.Height = frame.PixelHeight;

                return image;
            }
            catch (Exception)
            {
                // 壊れた画像や、Windows に読む部品が無い形式。
                return null;
            }
        }

        // =====================================================================
        // 箇条書き・表・コード・脚注
        // =====================================================================

        /// <summary>
        /// 箇条書き。記号の箇条書き（- や *）は記号を付けず、字下げだけの段落にする。
        /// 資料では全段落に • が並ぶと煩いため。入れ子は1段ずつ深くする。
        /// 番号付き（1.）は Word の番号を振る。
        /// </summary>
        private void WriteList(ListBlock list, Context context)
        {
            var level = Math.Min(context.ListLevel, 8);
            var numbering = 0;

            if (list.IsOrdered)
            {
                int start;
                if (!int.TryParse(list.OrderedStart, out start) || start < 0) start = 1;
                numbering = _docx.AddOrderedList(level, start);
            }

            foreach (var block in list)
            {
                var item = block as ListItemBlock;
                if (item == null) continue;

                // 記号の箇条書きは一番外側を本文と同じ位置に置く。番号付きは番号の分だけ右に置く。
                var inner = context.Clone();
                inner.ListLevel = level + 1;
                inner.Indent = DocxBuilder.IndentStep * (list.IsOrdered ? level + 1 : level);

                var format = Format(inner, null);

                if (numbering > 0)
                {
                    // 番号の定義も字下げを持つが、見出しの深さの分を足せないので段落で上書きする。
                    // 本文は Indent の位置、番号はそこから Hanging だけ左にぶら下げる。
                    format.NumberingId = numbering;
                    format.Level = level;
                    format.Indent = LeftIndent(inner);
                    format.Hanging = DocxBuilder.IndentStep;
                }

                var first = true;

                foreach (var child in item)
                {
                    var paragraph = child as ParagraphBlock;

                    if (first && paragraph != null)
                    {
                        WriteInlines(paragraph.Inline, format, inner, null);
                        first = false;
                        continue;
                    }

                    // 本文の無い項目は、番号付きなら番号だけ残す。記号の箇条書きなら何も出さない。
                    if (first && numbering > 0) _docx.Paragraph(format, new Piece[0]);
                    first = false;

                    WriteBlock(child, inner);
                }

                // 中身の無い項目も、番号付きなら番号だけは残す。
                if (first && numbering > 0) _docx.Paragraph(format, new Piece[0]);
            }
        }

        private void WriteTable(Table table, Context context)
        {
            var columns = 0;
            foreach (var block in table)
            {
                var row = block as TableRow;
                if (row != null) columns = Math.Max(columns, row.Count);
            }

            if (columns == 0) return;

            var widths = ColumnWidths(table, columns, _page.TextWidthTwips - LeftIndent(context));
            _docx.BeginTable(widths, LeftIndent(context));

            foreach (var block in table)
            {
                var row = block as TableRow;
                if (row == null) continue;

                _docx.BeginRow(row.IsHeader);

                var index = 0;

                foreach (var cellBlock in row)
                {
                    var cell = cellBlock as TableCell;
                    if (cell == null) continue;

                    var inner = context.Clone();
                    inner.InTable = true;
                    inner.Header = row.IsHeader;
                    inner.Indent = 0;
                    inner.ListLevel = 0;
                    inner.Quote = false;
                    inner.Align = AlignOf(table, index);
                    inner.ImageWidth = DocxBuilder.CellContentWidthEmu(widths[Math.Min(index, widths.Length - 1)]);

                    _docx.BeginCell(row.IsHeader);
                    WriteBlocks(cell, inner);
                    _docx.EndCell();

                    index++;
                }

                // 足りないセルを埋める。欠けたままだと列がずれる。
                for (; index < columns; index++)
                {
                    _docx.BeginCell(row.IsHeader);
                    _docx.EndCell();
                }

                _docx.EndRow();
            }

            _docx.EndTable();
        }

        /// <summary>
        /// 列ごとの幅（twip）を、中身に合わせて決める（SPEC 13章。2026-10-16。前は本文の幅を列の数で等分）。
        ///
        /// 列ごとに、いちばん広いセルの文字の幅を本文と同じフォント（BIZ UDPゴシック 10.5pt）で測り、セルの余白と少しの余裕を足す。
        /// 合計が本文の幅に収まるならその幅（短い表は狭く、左に寄る）。収まらなければ本文の幅いっぱいを、
        /// 中身の量に合わせて割り振る（1列が広くなりすぎないよう、本文の幅の6割で頭打ち。短い列にも最低限の幅）。
        /// 画像の入った列は、画像の大きさが分からないので広く取る。
        /// </summary>
        private static int[] ColumnWidths(Table table, int columns, int available)
        {
            var minimum = DocxBuilder.CellMargin * 4;
            available = Math.Max(minimum * columns, available);

            var natural = new double[columns];
            for (var c = 0; c < columns; c++) natural[c] = minimum;

            foreach (var block in table)
            {
                var row = block as TableRow;
                if (row == null) continue;

                var index = 0;
                foreach (var cellBlock in row)
                {
                    var cell = cellBlock as TableCell;
                    if (cell == null || index >= columns) continue;

                    double width;
                    if (HasImage(cell)) width = available;
                    else width = MeasureTwips(CellText(cell)) + DocxBuilder.CellMargin * 2 + 60;   // 60: 罫線と端数の余裕

                    natural[index] = Math.Max(natural[index], width);
                    index++;
                }
            }

            var widths = new int[columns];
            var total = 0.0;
            foreach (var width in natural) total += width;

            if (total <= available)
            {
                for (var c = 0; c < columns; c++) widths[c] = (int)Math.Ceiling(natural[c]);
                return widths;
            }

            var weights = new double[columns];
            var sum = 0.0;
            for (var c = 0; c < columns; c++)
            {
                weights[c] = Math.Min(natural[c], available * 0.6);
                sum += weights[c];
            }

            var used = 0;
            for (var c = 0; c < columns; c++)
            {
                widths[c] = Math.Max(minimum, (int)Math.Floor(available * weights[c] / sum));
                used += widths[c];
            }
            widths[columns - 1] += Math.Max(0, available - used);   // 端数は最後の列へ

            return widths;
        }

        /// <summary>セルの文字（装飾を外したもの）。</summary>
        private static string CellText(TableCell cell)
        {
            var builder = new StringBuilder();
            foreach (var block in cell)
            {
                var leaf = block as LeafBlock;
                if (leaf == null || leaf.Inline == null) continue;

                if (builder.Length > 0) builder.Append(' ');
                builder.Append(PlainText(leaf.Inline));
            }
            return builder.ToString();
        }

        private static bool HasImage(TableCell cell)
        {
            foreach (var block in cell)
            {
                var leaf = block as LeafBlock;
                if (leaf == null || leaf.Inline == null) continue;

                foreach (var link in leaf.Inline.Descendants<LinkInline>())
                {
                    if (link.IsImage) return true;
                }
            }
            return false;
        }

        /// <summary>本文と同じフォント・大きさで書いたときの幅（twip）。1pt = 20 twip、1 DIP = 15 twip。</summary>
        private static double MeasureTwips(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var formatted = new System.Windows.Media.FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"),
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("BIZ UDPゴシック"),
                10.5 * 96 / 72,                                       // 10.5pt を DIP に
                System.Windows.Media.Brushes.Black, 1.0);

            return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) * 15;
        }

        private static string AlignOf(Table table, int column)
        {
            if (column >= table.ColumnDefinitions.Count) return null;

            var align = table.ColumnDefinitions[column].Alignment;
            if (align == TableColumnAlign.Center) return "center";
            if (align == TableColumnAlign.Right) return "right";

            return null;
        }

        private void WriteCode(CodeBlock code, Context context)
        {
            var format = Format(context, "SourceCode");
            var lines = code.Lines;

            // 1行を1段落にする。空行も段落として残し、コードの見た目を崩さない。
            for (var i = 0; i < lines.Count; i++)
            {
                var pieces = new List<Piece>();
                var text = lines.Lines[i].Slice.ToString();

                if (text.Length > 0)
                {
                    var piece = new Piece();
                    piece.Kind = PieceKind.Text;
                    piece.Text = text;
                    pieces.Add(piece);
                }

                _docx.Paragraph(format, pieces);
            }
        }

        /// <summary>脚注は文書の末尾にまとめて置くので、直前の見出しの深さを引き継がない。</summary>
        private void WriteFootnotes(FootnoteGroup group, Context context)
        {
            _depth = 0;

            var rule = Format(context, null);
            rule.Rule = true;
            _docx.Paragraph(rule, new Piece[0]);

            foreach (var block in group)
            {
                var note = block as Footnote;
                if (note == null) continue;

                var first = true;

                foreach (var child in note)
                {
                    var paragraph = child as ParagraphBlock;

                    if (first && paragraph != null)
                    {
                        var number = note.Order.ToString(CultureInfo.InvariantCulture) + ". ";
                        WriteInlines(paragraph.Inline, Format(context, null), context, number);
                    }
                    else
                    {
                        WriteBlock(child, context);
                    }

                    first = false;
                }
            }
        }

        // =====================================================================
        // 保存
        // =====================================================================

        /// <summary>
        /// 一時ファイルに書いてから置き換える。
        /// Word で開いたままならコピーの前に失敗し、前の docx はそのまま残る。
        /// コピーの途中で失敗すると前の docx も壊れるが、普通の上書き保存と同じとして受け入れている（SPEC 13章）。
        /// </summary>
        private void SaveAtomically(string path)
        {
            var temp = path + ".exporting";

            try
            {
                _docx.Save(temp);
                File.Copy(temp, path, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // =====================================================================
        // 小物
        // =====================================================================

        private static string PageKey(PageDocument document, string pageId)
        {
            return FullPath(document.FilePath) + "#" + (pageId ?? string.Empty);
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static bool IsWeb(string url)
        {
            return url.IndexOf("://", StringComparison.Ordinal) >= 0
                || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMarkdown(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// リンク先のパス。MarkdownView.ResolvePath と同じ探し方をする。
        /// # より後ろは fragment に返す（別ファイルのサブページを指す場合に使う）。
        /// </summary>
        private static string Resolve(string documentPath, string target, out string fragment)
        {
            fragment = string.Empty;
            if (string.IsNullOrEmpty(documentPath)) return null;

            var cleaned = target;

            var anchor = cleaned.IndexOf('#');
            if (anchor >= 0)
            {
                fragment = cleaned.Substring(anchor + 1);
                cleaned = cleaned.Substring(0, anchor);
            }

            if (cleaned.Length == 0) return null;

            string decoded = null;
            try
            {
                decoded = Attachments.Combine(documentPath, Uri.UnescapeDataString(cleaned));
            }
            catch (ArgumentException)
            {
            }

            if (decoded != null && File.Exists(decoded)) return decoded;

            var raw = Attachments.Combine(documentPath, cleaned);
            if (raw != null && File.Exists(raw)) return raw;

            return decoded ?? raw;
        }

        /// <summary>リンクの表示名を1行の文字列にする。</summary>
        private static string PlainText(ContainerInline container)
        {
            var builder = new StringBuilder();
            Collect(container, builder);

            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        private static void Collect(ContainerInline container, StringBuilder builder)
        {
            for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
            {
                var literal = inline as LiteralInline;
                if (literal != null)
                {
                    builder.Append(literal.Content.ToString());
                    continue;
                }

                var code = inline as CodeInline;
                if (code != null)
                {
                    builder.Append(code.Content.ToString());
                    continue;
                }

                var entity = inline as HtmlEntityInline;
                if (entity != null)
                {
                    builder.Append(entity.Transcoded.ToString());
                    continue;
                }

                if (inline is LineBreakInline)
                {
                    builder.Append(' ');
                    continue;
                }

                var child = inline as ContainerInline;
                if (child != null) Collect(child, builder);
            }
        }
    }
}
