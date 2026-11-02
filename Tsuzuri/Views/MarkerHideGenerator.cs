using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// カーソルのある行以外で、Markdown の記号を画面から消す（SPEC 8章）。
    /// あわせて、見出しの深さに応じて行頭を字下げして見せる（SPEC 8.4）。
    ///
    /// 消すのもずらすのも描画層だけで、文書そのものには一切触れない。
    /// ここがどう間違ってもファイルの中身は壊れない。
    ///
    /// 字下げを別の生成器にしない理由: AvalonEdit は文字数0の要素を作ったあと、
    /// 同じ位置から始まる要素を探さない。字下げを先に置くと、行頭の ## や - を
    /// 消す要素が作られなくなる。そこで、行頭の記号と字下げを1つの要素にまとめる。
    /// </summary>
    public sealed class MarkerHideGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownView _owner;

        /// <summary>この行の字下げをもう作ったか。文字数0の要素は、止めないと同じ位置で作り続ける。</summary>
        private bool _indentDone;

        public MarkerHideGenerator(MarkdownView owner)
        {
            _owner = owner;
        }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _indentDone = false;
        }

        /// <summary>見出しの深さぶんの字下げの幅。</summary>
        private double IndentOf(DocumentLine line)
        {
            var info = _owner.GetLineInfo(line.LineNumber);
            return info == null || info.Depth <= 0 ? 0.0 : info.Depth * Theme.IndentStep;
        }

        /// <summary>
        /// この行から始まる表を、表の部品にして描くか（SPEC 8章）。表の1行目で、カーソルが表の外にあるときだけ。
        /// 描くなら、表の最後の行を返す。
        /// </summary>
        private bool RendersTable(DocumentLine line, out int last)
        {
            int first;
            last = -1;

            if (!_owner.TryGetTableRange(line.LineNumber, out first, out last)) return false;

            // 描く前に2行目以降を畳んである表だけ（MarkdownView.UpdateTableFolds）。畳んでいない行を飛ばすと AvalonEdit が止める。
            return first == line.LineNumber && _owner.IsTableFolded(first, last);
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;

            // 表は、字下げより先に、行頭で表の範囲をまとめて受け持つ。
            int tableLast;
            if (!_indentDone && startOffset <= line.Offset && RendersTable(line, out tableLast)) return line.Offset;

            if (!_indentDone && startOffset <= line.Offset && IndentOf(line) > 0) return line.Offset;

            var ranges = _owner.GetHiddenRanges(line.LineNumber);

            if (ranges != null)
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    if (!Fits(ranges[i], line.Length)) continue;

                    var offset = line.Offset + ranges[i].Start;
                    if (offset >= startOffset) return offset;
                }
            }

            // 画像は行末に置く。消す範囲は行末より手前から始まるので、ぶつからない。
            if (line.EndOffset >= startOffset && _owner.GetLineImages(line.LineNumber) != null)
                return line.EndOffset;

            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;

            int tableLast;
            if (!_indentDone && offset == line.Offset && RendersTable(line, out tableLast))
            {
                var table = CreateTable(line, tableLast);
                if (table != null)
                {
                    _indentDone = true;
                    return table;
                }
            }

            if (!_indentDone && offset == line.Offset)
            {
                var indent = IndentOf(line);
                if (indent > 0)
                {
                    _indentDone = true;
                    return CreateIndent(line, indent);
                }
            }

            if (offset == line.EndOffset)
            {
                var images = _owner.GetLineImages(line.LineNumber);
                return images != null ? CreateImages(images) : null;
            }

            var ranges = _owner.GetHiddenRanges(line.LineNumber);
            if (ranges == null) return null;

            for (var i = 0; i < ranges.Count; i++)
            {
                if (line.Offset + ranges[i].Start != offset) continue;

                var range = ranges[i];
                if (!Fits(range, line.Length)) return null;

                // 画像の題は、薄い字で描く。
                if (range.Caption && range.Replacement.Length > 0) return new CaptionElement(range.Replacement, range.Length);

                // 置き換える文字があるものは描く（- を • にするなど）。
                return range.Replacement.Length > 0
                    ? (VisualLineElement)new FormattedTextElement(range.Replacement, range.Length)
                    : new HiddenElement(range.Length);
            }

            return null;
        }

        /// <summary>
        /// 行の画像を、リンクの下に縦に並べる。文書上は長さゼロで、本文には触れない。
        ///
        /// 大きさは画素数を画面の拡大率で割ったもの。スクリーンショットが撮ったときと
        /// 同じ大きさに見える。本文の幅を超えるものだけ縦横比を保って縮め、広げはしない。
        ///
        /// 枠は本文の幅いっぱいに取る。そうするとリンクの横には収まらず、
        /// 折り返しで必ず次の段に送られる。
        ///
        /// 画像は文字のベースラインに乗るので、そのままだと下に文字の下がりぶんの
        /// 隙間が残る。ベースラインを下がりぶん上げて、段の下端まで画像を届かせる。
        /// </summary>
        private VisualLineElement CreateImages(IList<LineImage> images)
        {
            var textView = CurrentContext.TextView;
            var dpi = VisualTreeHelper.GetDpi(textView);

            // 幅ちょうどにすると折り返しの計算で行末からはみ出すことがあるので、少し残す。
            // 画像の段も字下げの位置から始まるので、その分も引く。
            var room = textView.ActualWidth - 8 - IndentOf(CurrentContext.VisualLine.FirstDocumentLine);

            var panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;

            var widest = 0.0;
            var total = 0.0;
            foreach (var entry in images)
            {
                var bitmap = entry.Image;
                var width = bitmap.PixelWidth / dpi.DpiScaleX;
                var height = bitmap.PixelHeight / dpi.DpiScaleY;

                if (room > 0 && width > room)
                {
                    height = height * room / width;
                    width = room;
                }

                var image = new Image();
                image.Source = bitmap;
                image.Stretch = Stretch.Fill;
                image.Width = Math.Max(1, width);
                image.Height = Math.Max(1, height);
                image.HorizontalAlignment = HorizontalAlignment.Left;
                image.Margin = new Thickness(0, 4, 0, 0);
                image.Tag = entry.Link;               // クリックでリンク先を開くため
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

                panel.Children.Add(image);
                widest = Math.Max(widest, width);
                total += image.Height + image.Margin.Top;
            }

            panel.Width = room > 0 ? room : widest;

            var properties = CurrentContext.GlobalTextRunProperties;
            var family = properties.Typeface.FontFamily;
            var descent = (family.LineSpacing - family.Baseline) * properties.FontRenderingEmSize;
            TextBlock.SetBaselineOffset(panel, Math.Max(0, total - descent));

            return new ImageElement(panel);
        }

        /// <summary>
        /// 表の行（first から last まで）を、1つの表の部品にする。文書上は表の行の範囲をまるごと受け持つ。
        /// 字下げは部品の左の余白で付ける。読めなければ null（生のまま見せる）。
        /// </summary>
        private VisualLineElement CreateTable(DocumentLine first, int lastNumber)
        {
            var document = CurrentContext.Document;
            var last = document.GetLineByNumber(lastNumber);
            var length = last.EndOffset - first.Offset;
            var source = document.GetText(first.Offset, length);

            var indent = IndentOf(first);
            var room = CurrentContext.TextView.ActualWidth - 8 - indent;
            var firstNumber = first.LineNumber;

            var table = TableElement.Build(source, room, row => _owner.EditTable(firstNumber));
            if (table == null) return null;

            table.Margin = new Thickness(indent, table.Margin.Top, 0, table.Margin.Bottom);
            return TableElement.CreateElement(table, length, CurrentContext);
        }

        /// <summary>
        /// 行頭の字下げ。行頭に消す記号（## や -）があれば、それも同じ要素で受け持つ。
        /// </summary>
        private VisualLineElement CreateIndent(DocumentLine line, double indent)
        {
            var ranges = _owner.GetHiddenRanges(line.LineNumber);

            if (ranges != null)
            {
                foreach (var range in ranges)
                {
                    if (range.Start != 0 || !Fits(range, line.Length)) continue;

                    // 画像の題は字下げに含めず、題の要素で描く（2026-11-02）。字下げの要素は空白として扱われるので、
                    // 題まで含めると、題の後で折り返す画像の段が字下げの位置に揃わず、左端から出ていた。
                    if (range.Caption) break;

                    var text = range.Replacement.Length > 0 ? range.Replacement : null;
                    return new IndentElement(indent, range.Length, text);
                }
            }

            return new IndentElement(indent, 0, null);
        }

        /// <summary>
        /// 行の中に収まっているか。行区切りを飲み込む要素を作ると
        /// AvalonEdit が例外を投げてアプリごと落ちるため、必ず確かめる。
        /// </summary>
        private static bool Fits(HiddenRange range, int lineLength)
        {
            return range.Start >= 0
                && range.Length > 0
                && range.Start + range.Length <= lineLength;
        }

        /// <summary>
        /// 画像の枠。文書上の長さはゼロ。
        ///
        /// カーソルの止まる位置を持たせない。持たせると、同じ文書位置に
        /// 「画像の前」と「画像の後」の2か所ができ、→ キーが空振りする。
        /// </summary>
        private sealed class ImageElement : InlineObjectElement
        {
            public ImageElement(UIElement element) : base(0, element)
            {
            }

            public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
            {
                return -1;
            }
        }

        /// <summary>
        /// 行頭の字下げ。見出しの深さぶんの幅を空け、行頭の記号を置き換える文字
        /// （- を • にするなど）があれば、その後ろに描く。
        /// </summary>
        private sealed class IndentElement : VisualLineElement
        {
            private readonly double _indent;
            private readonly string _text;

            public IndentElement(double indent, int documentLength, string text) : base(1, documentLength)
            {
                _indent = indent;
                _text = text;
            }

            public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            {
                return new IndentRun(_indent, _text, TextRunProperties, context.TextView);
            }

            /// <summary>
            /// 空白として扱わせる。折り返した2行目以降が1行目と同じ位置から始まる
            /// （InheritWordWrapIndentation は行頭の空白の幅に揃える）。
            /// </summary>
            public override bool IsWhitespace(int visualColumn)
            {
                return true;
            }

            /// <summary>
            /// 文字数0の字下げにはカーソルを止めない。止めると同じ文書位置に
            /// 「字下げの前」と「後」の2か所ができ、→ キーが空振りする。
            /// </summary>
            public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
            {
                return DocumentLength == 0 ? -1 : base.GetNextCaretPosition(visualColumn, direction, mode);
            }
        }

        /// <summary>字下げの描画。幅を空け、置き換える文字があればその後ろに描く。</summary>
        private sealed class IndentRun : TextEmbeddedObject
        {
            private readonly double _indent;
            private readonly TextRunProperties _properties;

            /// <summary>描く文字。無ければ null。</summary>
            private readonly FormattedText _text;

            /// <summary>行の高さとベースラインを決めるための物差し。</summary>
            private readonly FormattedText _measure;

            public IndentRun(double indent, string text, TextRunProperties properties, TextView view)
            {
                _indent = indent;
                _properties = properties;

                var pixelsPerDip = VisualTreeHelper.GetDpi(view).PixelsPerDip;

                _text = string.IsNullOrEmpty(text) ? null : Format(text, properties, pixelsPerDip);
                _measure = _text ?? Format(" ", properties, pixelsPerDip);
            }

            private static FormattedText Format(string text, TextRunProperties properties, double pixelsPerDip)
            {
                return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    properties.Typeface, properties.FontRenderingEmSize, properties.ForegroundBrush, pixelsPerDip);
            }

            private double Width
            {
                get { return _indent + (_text == null ? 0.0 : _text.WidthIncludingTrailingWhitespace); }
            }

            // 字下げと、その後ろの本文の間では折り返さない。
            public override LineBreakCondition BreakBefore
            {
                get { return LineBreakCondition.BreakRestrained; }
            }

            public override LineBreakCondition BreakAfter
            {
                get { return LineBreakCondition.BreakRestrained; }
            }

            public override bool HasFixedSize
            {
                get { return true; }
            }

            public override CharacterBufferReference CharacterBufferReference
            {
                get { return new CharacterBufferReference(); }
            }

            public override int Length
            {
                get { return 1; }
            }

            public override TextRunProperties Properties
            {
                get { return _properties; }
            }

            public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)
            {
                return new TextEmbeddedObjectMetrics(Width, _measure.Height, _measure.Baseline);
            }

            public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways)
            {
                return new Rect(0, 0, Width, _measure.Height);
            }

            /// <summary>origin はベースラインの位置で渡される。</summary>
            public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways)
            {
                if (_text == null) return;

                drawingContext.DrawText(_text, new Point(origin.X + _indent, origin.Y - _text.Baseline));
            }
        }

        /// <summary>
        /// 画像の題（2026-10-27）。リンクの文字列の代わりに名前を描く。本文と同じ大きさ（クリックして生の文字が出ても段の高さが変わらない）で、
        /// 色は薄く、下線は付けない（押せるリンクには見せない）。色付けの処理がリンクの色を付けていても、描くときに上書きする。
        /// </summary>
        private sealed class CaptionElement : FormattedTextElement
        {
            public CaptionElement(string text, int documentLength) : base(text, documentLength)
            {
            }

            public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            {
                TextRunProperties.SetForegroundBrush(Theme.ImageCaption);
                TextRunProperties.SetTextDecorations(null);
                return base.CreateTextRun(startVisualColumn, context);
            }
        }

        /// <summary>
        /// 文書上は documentLength 文字ぶん場所を取るが、何も描かない要素。
        ///
        /// AvalonEdit の要素は表示幅を完全にゼロにできない（最低1カラム）ので、
        /// WPF の TextHidden を使って、1カラムぶんを「描かない run」で埋める。
        /// </summary>
        private sealed class HiddenElement : VisualLineElement
        {
            public HiddenElement(int documentLength) : base(1, documentLength)
            {
            }

            public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            {
                return new TextHidden(1);
            }
        }
    }
}
