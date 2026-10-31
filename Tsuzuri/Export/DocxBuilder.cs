using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Xml;

namespace Tsuzuri.Export
{
    /// <summary>書き出す用紙（SPEC 13章）。</summary>
    public enum Paper
    {
        A4,
        A3,
    }

    /// <summary>
    /// 書き出す用紙・向き・余白（SPEC 13章。2026-10-14 に選べるようにした。前は A4 縦・余白 20mm に決め打ち）。
    /// 画像を縮める上限は、ここから決まる本文の幅。
    /// </summary>
    public sealed class PageSetup
    {
        public Paper Paper = Paper.A4;
        public bool Landscape;

        /// <summary>余白（上下左右とも同じ）。</summary>
        public int MarginMm = 20;

        // Word が使う値（twip）。A4 は 210×297mm、A3 は 297×420mm。
        private int ShortSide
        {
            get { return Paper == Paper.A3 ? 16838 : 11906; }
        }

        private int LongSide
        {
            get { return Paper == Paper.A3 ? 23811 : 16838; }
        }

        public int WidthTwips
        {
            get { return Landscape ? LongSide : ShortSide; }
        }

        public int HeightTwips
        {
            get { return Landscape ? ShortSide : LongSide; }
        }

        public int MarginTwips
        {
            get { return (int)Math.Round(MarginMm * 1440 / 25.4); }
        }

        /// <summary>用紙の幅から左右の余白を引いた本文の幅。</summary>
        public int TextWidthTwips
        {
            get { return WidthTwips - MarginTwips * 2; }
        }

        /// <summary>用紙の高さから上下の余白を引いた本文の高さ。</summary>
        public int TextHeightTwips
        {
            get { return HeightTwips - MarginTwips * 2; }
        }
    }

    [Flags]
    internal enum RunStyle
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Strike = 4,
        Code = 8,
        Superscript = 16,
        Subscript = 32,
        Highlight = 64,
        Underline = 128,
        Link = 256,
    }

    internal enum PieceKind
    {
        Text,
        Break,
        LinkStart,
        LinkEnd,
    }

    /// <summary>段落の中身の1片。文字・改行・リンクの始まりと終わり。</summary>
    internal sealed class Piece
    {
        public PieceKind Kind;
        public string Text;
        public RunStyle Style;
        public string RelationshipId;
    }

    internal sealed class ParagraphFormat
    {
        /// <summary>styles.xml の styleId。null なら標準。</summary>
        public string Style;

        /// <summary>0 なら箇条書きにしない。</summary>
        public int NumberingId;
        public int Level;

        /// <summary>左の字下げ（twip）。0 なら書かず、スタイルに任せる。</summary>
        public int Indent;

        /// <summary>1行目だけ左へぶら下げる幅（twip）。箇条書きの記号の置き場所。</summary>
        public int Hanging;

        /// <summary>"center" / "right"。null なら左。</summary>
        public string Align;

        /// <summary>区切り線（下罫線だけの段落）。</summary>
        public bool Rule;

        public ParagraphFormat Clone()
        {
            return (ParagraphFormat)MemberwiseClone();
        }
    }

    /// <summary>
    /// docx を組み立てる。WordprocessingML を XmlWriter で直接書き、
    /// WPF 標準の System.IO.Packaging で包む。
    ///
    /// Open XML SDK を使わない理由: 配布を exe と最小限の DLL に保つため（SPEC 2章）。
    /// 書くのは段落・見出し・箇条書き・表・画像・リンクだけなので、手書きで足りる。
    /// </summary>
    internal sealed class DocxBuilder
    {
        private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

        private const string RelationshipBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        private const string ContentTypeBase = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

        public const long EmuPerTwip = 635;
        public const long EmuPerDip = 9525;

        /// <summary>画像の枠線。0.5pt（1pt = 12700 EMU）の薄い灰色。</summary>
        private const long ImageBorderWidthEmu = 6350;
        private const string ImageBorderColor = "BFBFBF";

        /// <summary>箇条書き1段ぶん・引用の字下げ（twip）。全角2文字ほど。記号や番号をぶら下げる幅が要る。</summary>
        public const int IndentStep = 420;

        /// <summary>見出しの深さ1段ぶんの字下げ（twip）。全角1文字（10.5pt）。2026-10-15 に2文字から詰めた。</summary>
        public const int DepthStep = 210;

        /// <summary>表のセルの左右の内余白（twip）。TableNormal と揃える。</summary>
        /// <summary>セルの左右の余白（twip）。</summary>
        public const int CellMargin = 108;

        /// <summary>見出しの深さとして使う段の数。見出し 1〜6 に合わせる。</summary>
        public const int MaxDepth = 6;

        /// <summary>記号の箇条書きは全部これを使う。番号付きは段落ごとに振り直すので別に作る。</summary>
        public const int BulletNumbering = 1;

        private sealed class Media
        {
            public string Id;
            public string Name;
            public string ContentType;
            public byte[] Data;
        }

        private sealed class OrderedList
        {
            public int Id;
            public int Level;
            public int Start;
        }

        private readonly MemoryStream _stream = new MemoryStream();
        private readonly XmlWriter _xml;

        private readonly List<Media> _media = new List<Media>();
        private readonly List<KeyValuePair<string, Uri>> _hyperlinks = new List<KeyValuePair<string, Uri>>();
        private readonly List<OrderedList> _ordered = new List<OrderedList>();

        private int _nextNumbering = BulletNumbering + 1;
        private int _nextDrawing = 1;
        private int[] _columnWidths = new int[0];
        private int _cell;
        private int _tableIndent;
        private int _paragraphsInCell;
        private bool _lastWasTable;

        private readonly PageSetup _page;

        /// <summary>本文の幅。画像を縮める上限。</summary>
        public long TextWidthEmu
        {
            get { return _page.TextWidthTwips * EmuPerTwip; }
        }

        /// <summary>本文の高さ。画像を縮める上限（2026-10-18。前は幅だけ見ていて、縦に長い画像が用紙からはみ出した）。</summary>
        public long TextHeightEmu
        {
            get { return _page.TextHeightTwips * EmuPerTwip; }
        }

        public DocxBuilder(PageSetup page)
        {
            _page = page;

            var settings = new XmlWriterSettings();
            settings.Encoding = new UTF8Encoding(false);
            settings.CloseOutput = false;

            _xml = XmlWriter.Create(_stream, settings);
            _xml.WriteStartDocument(true);
            _xml.WriteStartElement("w", "document", W);
            _xml.WriteAttributeString("xmlns", "r", null, R);
            _xml.WriteAttributeString("xmlns", "wp", null, WP);
            _xml.WriteAttributeString("xmlns", "a", null, A);
            _xml.WriteAttributeString("xmlns", "pic", null, PIC);
            Start("body");
        }

        // =====================================================================
        // 段落
        // =====================================================================

        public void Paragraph(ParagraphFormat format, IList<Piece> pieces)
        {
            Start("p");
            WriteParagraphProperties(format);

            var linkOpen = false;

            foreach (var piece in pieces)
            {
                switch (piece.Kind)
                {
                    case PieceKind.LinkStart:
                        if (linkOpen || piece.RelationshipId == null) break;
                        Start("hyperlink");
                        _xml.WriteAttributeString("r", "id", R, piece.RelationshipId);
                        linkOpen = true;
                        break;

                    case PieceKind.LinkEnd:
                        if (!linkOpen) break;
                        End();
                        linkOpen = false;
                        break;

                    case PieceKind.Break:
                        Start("r");
                        Empty("br");
                        End();
                        break;

                    default:
                        WriteRun(piece.Text, piece.Style);
                        break;
                }
            }

            if (linkOpen) End();

            End();
            Wrote();
        }

        /// <summary>画像だけの段落。大きさは EMU で渡す。</summary>
        public void Image(ParagraphFormat format, byte[] data, string extension, long width, long height, string description)
        {
            var number = _media.Count + 1;
            var media = new Media();
            media.Id = "rIdImage" + number;
            media.Name = "image" + number + "." + extension;
            media.ContentType = ImageContentType(extension);
            media.Data = data;
            _media.Add(media);

            var drawing = _nextDrawing++;
            var cx = Math.Max(1, width).ToString(CultureInfo.InvariantCulture);
            var cy = Math.Max(1, height).ToString(CultureInfo.InvariantCulture);

            Start("p");
            WriteParagraphProperties(format);
            Start("r");
            Start("drawing");

            _xml.WriteStartElement("wp", "inline", WP);
            _xml.WriteAttributeString("distT", "0");
            _xml.WriteAttributeString("distB", "0");
            _xml.WriteAttributeString("distL", "0");
            _xml.WriteAttributeString("distR", "0");

            _xml.WriteStartElement("wp", "extent", WP);
            _xml.WriteAttributeString("cx", cx);
            _xml.WriteAttributeString("cy", cy);
            _xml.WriteEndElement();

            // 枠線は画像の外側に半分はみ出す。その分を描く範囲に足しておく（Word が枠線を付けたときと同じ）。
            var outside = (ImageBorderWidthEmu / 2).ToString(CultureInfo.InvariantCulture);
            _xml.WriteStartElement("wp", "effectExtent", WP);
            _xml.WriteAttributeString("l", outside);
            _xml.WriteAttributeString("t", outside);
            _xml.WriteAttributeString("r", outside);
            _xml.WriteAttributeString("b", outside);
            _xml.WriteEndElement();

            // descr は Word の「代替テキスト」になる。
            _xml.WriteStartElement("wp", "docPr", WP);
            _xml.WriteAttributeString("id", drawing.ToString(CultureInfo.InvariantCulture));
            _xml.WriteAttributeString("name", "図 " + drawing);
            _xml.WriteAttributeString("descr", Clean(description ?? string.Empty));
            _xml.WriteEndElement();

            _xml.WriteStartElement("wp", "cNvGraphicFramePr", WP);
            _xml.WriteStartElement("a", "graphicFrameLocks", A);
            _xml.WriteAttributeString("noChangeAspect", "1");
            _xml.WriteEndElement();
            _xml.WriteEndElement();

            _xml.WriteStartElement("a", "graphic", A);
            _xml.WriteStartElement("a", "graphicData", A);
            _xml.WriteAttributeString("uri", PIC);
            _xml.WriteStartElement("pic", "pic", PIC);

            _xml.WriteStartElement("pic", "nvPicPr", PIC);
            _xml.WriteStartElement("pic", "cNvPr", PIC);
            _xml.WriteAttributeString("id", drawing.ToString(CultureInfo.InvariantCulture));
            _xml.WriteAttributeString("name", media.Name);
            _xml.WriteEndElement();
            _xml.WriteStartElement("pic", "cNvPicPr", PIC);
            _xml.WriteEndElement();
            _xml.WriteEndElement();

            _xml.WriteStartElement("pic", "blipFill", PIC);
            _xml.WriteStartElement("a", "blip", A);
            _xml.WriteAttributeString("r", "embed", R, media.Id);
            _xml.WriteEndElement();
            _xml.WriteStartElement("a", "stretch", A);
            _xml.WriteStartElement("a", "fillRect", A);
            _xml.WriteEndElement();
            _xml.WriteEndElement();
            _xml.WriteEndElement();

            _xml.WriteStartElement("pic", "spPr", PIC);
            _xml.WriteStartElement("a", "xfrm", A);
            _xml.WriteStartElement("a", "off", A);
            _xml.WriteAttributeString("x", "0");
            _xml.WriteAttributeString("y", "0");
            _xml.WriteEndElement();
            _xml.WriteStartElement("a", "ext", A);
            _xml.WriteAttributeString("cx", cx);
            _xml.WriteAttributeString("cy", cy);
            _xml.WriteEndElement();
            _xml.WriteEndElement();
            _xml.WriteStartElement("a", "prstGeom", A);
            _xml.WriteAttributeString("prst", "rect");
            _xml.WriteStartElement("a", "avLst", A);
            _xml.WriteEndElement();
            _xml.WriteEndElement();

            // 枠線（SPEC 13章。2026-10-14）。白い背景のスクリーンショットでも、どこまでが画像か分かるように。
            // Word の「図の枠線」なので、要らなければ Word で消せる。
            _xml.WriteStartElement("a", "ln", A);
            _xml.WriteAttributeString("w", ImageBorderWidthEmu.ToString(CultureInfo.InvariantCulture));
            _xml.WriteStartElement("a", "solidFill", A);
            _xml.WriteStartElement("a", "srgbClr", A);
            _xml.WriteAttributeString("val", ImageBorderColor);
            _xml.WriteEndElement();
            _xml.WriteEndElement();
            _xml.WriteEndElement();
            _xml.WriteEndElement();   // pic:spPr

            _xml.WriteEndElement();   // pic:pic
            _xml.WriteEndElement();   // a:graphicData
            _xml.WriteEndElement();   // a:graphic
            _xml.WriteEndElement();   // wp:inline

            End();   // drawing
            End();   // r
            End();   // p
            Wrote();
        }

        /// <summary>そのまま埋め込める形式か。それ以外は PNG に変換してから渡す。</summary>
        public static bool CanEmbed(string extension)
        {
            return ImageContentType(extension) != null;
        }

        private static string ImageContentType(string extension)
        {
            switch (extension)
            {
                case "png": return "image/png";
                case "jpg":
                case "jpeg": return "image/jpeg";
                case "gif": return "image/gif";
                case "bmp": return "image/bmp";
                default: return null;
            }
        }

        /// <summary>外部へのリンクを登録する。URL として読めなければ null（文字だけ残す）。</summary>
        public string AddHyperlink(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return null;

            var id = "rIdLink" + (_hyperlinks.Count + 1);
            _hyperlinks.Add(new KeyValuePair<string, Uri>(id, uri));

            return id;
        }

        /// <summary>
        /// 番号付きの箇条書きを1つ始める。
        /// 同じ番号定義を使い回すと、別のリストでも番号が続いてしまうので、リストごとに作る。
        /// </summary>
        public int AddOrderedList(int level, int start)
        {
            var list = new OrderedList();
            list.Id = _nextNumbering++;
            list.Level = level;
            list.Start = start;
            _ordered.Add(list);

            return list.Id;
        }

        private void WriteParagraphProperties(ParagraphFormat format)
        {
            if (format == null) return;

            var any = format.Style != null || format.NumberingId > 0 || format.Rule
                || format.Indent > 0 || format.Align != null;
            if (!any) return;

            // 子要素の並びはスキーマで決まっている（pStyle, numPr, pBdr, ind, jc）。
            Start("pPr");

            if (format.Style != null) Val("pStyle", format.Style);

            if (format.NumberingId > 0)
            {
                Start("numPr");
                Val("ilvl", format.Level.ToString(CultureInfo.InvariantCulture));
                Val("numId", format.NumberingId.ToString(CultureInfo.InvariantCulture));
                End();
            }

            if (format.Rule)
            {
                Start("pBdr");
                Start("bottom");
                Attr("val", "single");
                Attr("sz", "6");
                Attr("space", "1");
                Attr("color", "A6A6A6");
                End();
                End();
            }

            if (format.Indent > 0)
            {
                Start("ind");
                Attr("left", format.Indent.ToString(CultureInfo.InvariantCulture));
                if (format.Hanging > 0) Attr("hanging", format.Hanging.ToString(CultureInfo.InvariantCulture));
                End();
            }

            if (format.Align != null) Val("jc", format.Align);

            End();
        }

        private void WriteRun(string text, RunStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;

            Start("r");

            if (style != RunStyle.None)
            {
                // 子要素の並びはスキーマで決まっている。
                Start("rPr");

                if ((style & RunStyle.Link) != 0) Val("rStyle", "Hyperlink");

                if ((style & RunStyle.Code) != 0)
                {
                    Start("rFonts");
                    Attr("ascii", "Consolas");
                    Attr("hAnsi", "Consolas");
                    End();
                }

                if ((style & RunStyle.Bold) != 0) Empty("b");
                if ((style & RunStyle.Italic) != 0) Empty("i");
                if ((style & RunStyle.Strike) != 0) Empty("strike");
                if ((style & RunStyle.Highlight) != 0) Val("highlight", "yellow");
                if ((style & RunStyle.Underline) != 0) Val("u", "single");

                if ((style & RunStyle.Code) != 0)
                {
                    Start("shd");
                    Attr("val", "clear");
                    Attr("color", "auto");
                    Attr("fill", "F2F2F2");
                    End();
                }

                if ((style & RunStyle.Superscript) != 0) Val("vertAlign", "superscript");
                else if ((style & RunStyle.Subscript) != 0) Val("vertAlign", "subscript");

                End();
            }

            // タブと改行は w:t の中に書けないので、それぞれ専用の要素にする。
            var chunk = new StringBuilder();

            foreach (var c in Clean(text))
            {
                if (c == '\r') continue;

                if (c == '\t' || c == '\n')
                {
                    WriteText(chunk);
                    Empty(c == '\t' ? "tab" : "br");
                    continue;
                }

                chunk.Append(c);
            }

            WriteText(chunk);

            End();
        }

        private void WriteText(StringBuilder chunk)
        {
            if (chunk.Length == 0) return;

            Start("t");
            _xml.WriteAttributeString("xml", "space", XmlNamespace, "preserve");
            _xml.WriteString(chunk.ToString());
            End();

            chunk.Clear();
        }

        // =====================================================================
        // 表
        // =====================================================================

        /// <summary>その幅のセルに置く画像の幅の上限（EMU）。</summary>
        public static long CellContentWidthEmu(int cellWidth)
        {
            return Math.Max(1, cellWidth - CellMargin * 2) * EmuPerTwip;
        }

        /// <summary>今書いているセルの幅（twip）。</summary>
        private int CurrentCellWidth
        {
            get { return _columnWidths[Math.Min(_cell, _columnWidths.Length - 1)]; }
        }

        /// <param name="columnWidths">列ごとの幅（twip）。中身に合わせて決めたもの（DocxExporter.ColumnWidths）。</param>
        /// <param name="indent">表の左端の字下げ（twip）。見出しの深さに揃える。</param>
        public void BeginTable(int[] columnWidths, int indent)
        {
            // 表が続くと Word は1つの表にくっつけてしまうので、空の段落で離す。
            if (_lastWasTable) Paragraph(null, new Piece[0]);

            _columnWidths = columnWidths;
            _tableIndent = Math.Max(0, indent);

            var total = 0;
            foreach (var width in columnWidths) total += width;

            Start("tbl");

            // 子要素の並びはスキーマで決まっている（tblStyle, tblW, tblInd, tblLook）。
            // 表の幅は列の幅の合計（2026-10-16。前は本文の幅いっぱい）。短い表は狭く、左に寄る。
            Start("tblPr");
            Val("tblStyle", "TableGrid");
            Start("tblW");
            Attr("w", total.ToString(CultureInfo.InvariantCulture));
            Attr("type", "dxa");
            End();
            if (_tableIndent > 0)
            {
                Start("tblInd");
                Attr("w", _tableIndent.ToString(CultureInfo.InvariantCulture));
                Attr("type", "dxa");
                End();
            }
            Start("tblLook");
            Attr("val", "04A0");
            End();
            End();

            Start("tblGrid");
            foreach (var width in _columnWidths)
            {
                Start("gridCol");
                Attr("w", width.ToString(CultureInfo.InvariantCulture));
                End();
            }
            End();
        }

        public void BeginRow(bool header)
        {
            _cell = 0;
            Start("tr");

            if (header)
            {
                // ページをまたいだときに見出し行を繰り返す。
                Start("trPr");
                Empty("tblHeader");
                End();
            }
        }

        public void BeginCell(bool header)
        {
            Start("tc");

            Start("tcPr");
            Start("tcW");
            Attr("w", CurrentCellWidth.ToString(CultureInfo.InvariantCulture));
            Attr("type", "dxa");
            End();
            _cell++;

            // 見出し行も網掛けしない（2026-10-16。表は細い線だけのすっきりした形にする）。

            End();

            _paragraphsInCell = 0;
        }

        public void EndCell()
        {
            // セルは段落を1つ以上持たないと Word が開けない。
            if (_paragraphsInCell == 0)
            {
                Start("p");
                End();
            }

            End();
        }

        public void EndRow()
        {
            End();
        }

        public void EndTable()
        {
            End();
            _lastWasTable = true;
        }

        private void Wrote()
        {
            _paragraphsInCell++;
            _lastWasTable = false;
        }

        // =====================================================================
        // 保存
        // =====================================================================

        public void Save(string path)
        {
            if (_lastWasTable) Paragraph(null, new Piece[0]);

            Start("sectPr");
            Start("pgSz");
            Attr("w", _page.WidthTwips.ToString(CultureInfo.InvariantCulture));
            Attr("h", _page.HeightTwips.ToString(CultureInfo.InvariantCulture));
            if (_page.Landscape) Attr("orient", "landscape");
            End();
            Start("pgMar");
            var margin = _page.MarginTwips.ToString(CultureInfo.InvariantCulture);
            Attr("top", margin);
            Attr("right", margin);
            Attr("bottom", margin);
            Attr("left", margin);
            Attr("header", "851");
            Attr("footer", "992");
            Attr("gutter", "0");
            End();
            End();

            End();   // body
            End();   // document
            _xml.WriteEndDocument();
            _xml.Flush();

            using (var package = Package.Open(path, FileMode.Create, FileAccess.ReadWrite))
            {
                var main = AddPart(package, "/word/document.xml", ContentTypeBase + "document.main+xml",
                    _stream.ToArray());
                package.CreateRelationship(main.Uri, TargetMode.Internal, RelationshipBase + "officeDocument", "rId1");

                AddPart(package, "/word/styles.xml", ContentTypeBase + "styles+xml", Utf8(StylesXml()));
                main.CreateRelationship(new Uri("styles.xml", UriKind.Relative), TargetMode.Internal,
                    RelationshipBase + "styles", "rIdStyles");

                AddPart(package, "/word/numbering.xml", ContentTypeBase + "numbering+xml", Utf8(NumberingXml()));
                main.CreateRelationship(new Uri("numbering.xml", UriKind.Relative), TargetMode.Internal,
                    RelationshipBase + "numbering", "rIdNumbering");

                AddPart(package, "/word/settings.xml", ContentTypeBase + "settings+xml", Utf8(SettingsXml()));
                main.CreateRelationship(new Uri("settings.xml", UriKind.Relative), TargetMode.Internal,
                    RelationshipBase + "settings", "rIdSettings");

                foreach (var media in _media)
                {
                    AddPart(package, "/word/media/" + media.Name, media.ContentType, media.Data);
                    main.CreateRelationship(new Uri("media/" + media.Name, UriKind.Relative), TargetMode.Internal,
                        RelationshipBase + "image", media.Id);
                }

                foreach (var link in _hyperlinks)
                {
                    main.CreateRelationship(link.Value, TargetMode.External, RelationshipBase + "hyperlink", link.Key);
                }
            }
        }

        private static PackagePart AddPart(Package package, string path, string contentType, byte[] data)
        {
            var uri = PackUriHelper.CreatePartUri(new Uri(path, UriKind.Relative));
            var part = package.CreatePart(uri, contentType, CompressionOption.Normal);

            using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
            {
                stream.Write(data, 0, data.Length);
            }

            return part;
        }

        private static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }

        // =====================================================================
        // 付属の定義
        // =====================================================================

        /// <summary>
        /// 見出しは Word 組み込みの「見出し 1〜6」と同じ名前にする。
        /// こうしておくと Word の目次・ナビゲーション・PDF のしおりがそのまま使える。
        /// </summary>
        private static string StylesXml()
        {
            var b = new StringBuilder();

            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            b.Append("<w:styles xmlns:w=\"").Append(W).Append("\">");

            b.Append("<w:docDefaults>");
            b.Append("<w:rPrDefault><w:rPr>");
            // 和文・欧文とも BIZ UDPゴシック（2026-10-15。前は游ゴシック。細くてかすれて見えるため）。
            b.Append("<w:rFonts w:ascii=\"BIZ UDPゴシック\" w:eastAsia=\"BIZ UDPゴシック\" w:hAnsi=\"BIZ UDPゴシック\" w:cs=\"BIZ UDPゴシック\"/>");
            // 字の色はほぼ黒（2026-10-15）。画面ではやわらかく、印刷でもほとんど差が出ない。Word の「黒、テキスト 1、白 + 基本色 15%」。
            // rPr の子要素の並びはスキーマで決まっている（color は rFonts の後、kern の前）。
            b.Append("<w:color w:val=\"262626\"/>");
            b.Append("<w:kern w:val=\"2\"/><w:sz w:val=\"21\"/><w:szCs w:val=\"21\"/>");
            b.Append("<w:lang w:val=\"en-US\" w:eastAsia=\"ja-JP\" w:bidi=\"ar-SA\"/>");
            b.Append("</w:rPr></w:rPrDefault>");
            // 段落の前後の間隔は 0 行。行間は 1.25 行のまま。
            b.Append("<w:pPrDefault><w:pPr><w:spacing w:before=\"0\" w:beforeLines=\"0\" w:after=\"0\" w:afterLines=\"0\" ");
            b.Append("w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>");
            b.Append("</w:docDefaults>");

            b.Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\">");
            b.Append("<w:name w:val=\"Normal\"/><w:qFormat/></w:style>");

            // 見出しの大きさ（半ポイント）。13・12・11.5・11・11・11pt、本文は 10.5pt。
            // 太字をやめたので大きさの差は小さく、なめらかにする（2026-10-15。前は 16・14・12・11・10.5・10.5pt で、見出し1と本文の差が大きかった）。
            int[] sizes = { 26, 24, 23, 22, 22, 22 };

            for (var i = 0; i < 6; i++)
            {
                var level = i + 1;

                b.Append("<w:style w:type=\"paragraph\" w:styleId=\"Heading").Append(level).Append("\">");
                b.Append("<w:name w:val=\"heading ").Append(level).Append("\"/>");
                b.Append("<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/>");
                b.Append("<w:uiPriority w:val=\"9\"/><w:qFormat/>");
                // keepNext / keepLines は付けない。編集記号の ▪ が見出しに並ぶうえ、見出しの次が
                // 大きな画像などだと見出しごと次のページへ送られ、手前に大きな空白が残るため。
                b.Append("<w:pPr>");
                b.Append("<w:spacing w:before=\"0\" w:beforeLines=\"0\" w:after=\"0\" w:afterLines=\"0\"/>");
                if (i > 0) b.Append("<w:ind w:left=\"").Append(DepthStep * i).Append("\"/>");
                b.Append("<w:outlineLvl w:val=\"").Append(i).Append("\"/></w:pPr>");
                // 見出しは太字にしない（2026-10-15）。太字は本文の **強調** だけ。
                b.Append("<w:rPr><w:b w:val=\"0\"/><w:bCs w:val=\"0\"/>");
                b.Append("<w:sz w:val=\"").Append(sizes[i]).Append("\"/><w:szCs w:val=\"").Append(sizes[i]).Append("\"/>");
                b.Append("</w:rPr></w:style>");
            }

            // 本文 1〜6: 見出し n の配下の本文。見出しより一段深く字下げする（エディタと同じ見た目）。
            for (var depth = 1; depth <= MaxDepth; depth++)
            {
                b.Append("<w:style w:type=\"paragraph\" w:customStyle=\"1\" w:styleId=\"Body").Append(depth).Append("\">");
                b.Append("<w:name w:val=\"本文 ").Append(depth).Append("\"/>");
                b.Append("<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Body").Append(depth).Append("\"/><w:qFormat/>");
                b.Append("<w:pPr><w:ind w:left=\"").Append(DepthStep * depth).Append("\"/></w:pPr>");
                b.Append("</w:style>");
            }

            b.Append("<w:style w:type=\"paragraph\" w:styleId=\"Quote\">");
            b.Append("<w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"29\"/><w:qFormat/>");
            b.Append("<w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"BFBFBF\"/></w:pBdr>");
            b.Append("<w:ind w:left=\"").Append(IndentStep).Append("\"/></w:pPr>");
            b.Append("<w:rPr><w:color w:val=\"595959\"/></w:rPr></w:style>");

            b.Append("<w:style w:type=\"paragraph\" w:customStyle=\"1\" w:styleId=\"SourceCode\">");
            b.Append("<w:name w:val=\"Source Code\"/><w:basedOn w:val=\"Normal\"/>");
            b.Append("<w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/>");
            b.Append("<w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>");
            b.Append("<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/><w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/></w:rPr>");
            b.Append("</w:style>");

            b.Append("<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\">");
            b.Append("<w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/>");
            b.Append("</w:style>");

            b.Append("<w:style w:type=\"character\" w:styleId=\"Hyperlink\">");
            b.Append("<w:name w:val=\"Hyperlink\"/><w:basedOn w:val=\"DefaultParagraphFont\"/>");
            b.Append("<w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/>");
            b.Append("<w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>");

            b.Append("<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\">");
            b.Append("<w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>");
            b.Append("<w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar>");
            b.Append("<w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"").Append(CellMargin).Append("\" w:type=\"dxa\"/>");
            b.Append("<w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"").Append(CellMargin).Append("\" w:type=\"dxa\"/>");
            b.Append("</w:tblCellMar></w:tblPr></w:style>");

            b.Append("<w:style w:type=\"table\" w:styleId=\"TableGrid\">");
            b.Append("<w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:uiPriority w:val=\"39\"/>");
            b.Append("<w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>");
            b.Append("<w:tblPr><w:tblBorders>");
            foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            {
                // 細い線（0.5pt）を、字と同じほぼ黒の灰色で（2026-10-16。前は薄い灰色 #A6A6A6）。
                b.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"262626\"/>");
            }
            b.Append("</w:tblBorders></w:tblPr></w:style>");

            b.Append("</w:styles>");

            return b.ToString();
        }

        private string NumberingXml()
        {
            var b = new StringBuilder();

            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            b.Append("<w:numbering xmlns:w=\"").Append(W).Append("\">");

            AbstractNumbering(b, 0, true);
            AbstractNumbering(b, 1, false);

            b.Append("<w:num w:numId=\"").Append(BulletNumbering).Append("\"><w:abstractNumId w:val=\"0\"/></w:num>");

            foreach (var list in _ordered)
            {
                b.Append("<w:num w:numId=\"").Append(list.Id).Append("\"><w:abstractNumId w:val=\"1\"/>");
                b.Append("<w:lvlOverride w:ilvl=\"").Append(list.Level).Append("\">");
                b.Append("<w:startOverride w:val=\"").Append(list.Start).Append("\"/></w:lvlOverride></w:num>");
            }

            b.Append("</w:numbering>");

            return b.ToString();
        }

        private static void AbstractNumbering(StringBuilder b, int id, bool bullet)
        {
            string[] bullets = { "•", "◦", "▪" };

            b.Append("<w:abstractNum w:abstractNumId=\"").Append(id).Append("\">");
            b.Append("<w:multiLevelType w:val=\"hybridMultilevel\"/>");

            for (var level = 0; level < 9; level++)
            {
                var text = bullet ? bullets[level % bullets.Length] : "%" + (level + 1) + ".";

                b.Append("<w:lvl w:ilvl=\"").Append(level).Append("\">");
                b.Append("<w:start w:val=\"1\"/>");
                b.Append("<w:numFmt w:val=\"").Append(bullet ? "bullet" : "decimal").Append("\"/>");
                b.Append("<w:lvlText w:val=\"").Append(text).Append("\"/>");
                b.Append("<w:lvlJc w:val=\"left\"/>");
                b.Append("<w:pPr><w:ind w:left=\"").Append(IndentStep * (level + 1));
                b.Append("\" w:hanging=\"").Append(IndentStep).Append("\"/></w:pPr>");
                b.Append("</w:lvl>");
            }

            b.Append("</w:abstractNum>");
        }

        /// <summary>
        /// view: Web レイアウトで開かせる。ページの区切りも余白もなく、ウィンドウの幅いっぱいに折り返す。
        /// 表示モードはファイルに残る情報なので、受け取った人も同じ表示で開く。印刷は普通に印刷レイアウトで出る。
        /// Word の決まりで settings の先頭近く（defaultTabStop より前）に置く。
        ///
        /// doNotAutoCompressPictures: Word が保存時や PDF 化で画像を 220ppi に落とすのを止める。
        /// スクリーンショットの細かい文字がにじむため。
        /// </summary>
        private static string SettingsXml()
        {
            var b = new StringBuilder();

            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            b.Append("<w:settings xmlns:w=\"").Append(W).Append("\">");
            b.Append("<w:view w:val=\"web\"/>");
            b.Append("<w:defaultTabStop w:val=\"840\"/>");
            b.Append("<w:characterSpacingControl w:val=\"doNotCompress\"/>");
            b.Append("<w:compat><w:compatSetting w:name=\"compatibilityMode\" ");
            b.Append("w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>");
            b.Append("<w:doNotAutoCompressPictures/>");
            b.Append("</w:settings>");

            return b.ToString();
        }

        // =====================================================================
        // 小物
        // =====================================================================

        private void Start(string name)
        {
            _xml.WriteStartElement("w", name, W);
        }

        private void End()
        {
            _xml.WriteEndElement();
        }

        private void Empty(string name)
        {
            Start(name);
            End();
        }

        private void Attr(string name, string value)
        {
            _xml.WriteAttributeString("w", name, W, value);
        }

        private void Val(string name, string value)
        {
            Start(name);
            Attr("val", value);
            End();
        }

        /// <summary>XML に書けない制御文字を落とす。そのまま渡すと XmlWriter が例外を出す。</summary>
        private static string Clean(string text)
        {
            var builder = new StringBuilder(text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        builder.Append(c).Append(text[i + 1]);
                        i++;
                    }

                    continue;
                }

                if (char.IsLowSurrogate(c)) continue;
                if (XmlConvert.IsXmlChar(c)) builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
