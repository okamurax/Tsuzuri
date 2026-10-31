using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tsuzuri.Markdown;

// WPF（System.Windows.Documents）にも同じ名前があるので、Markdig のほうを指すと決めておく。
using Table = Markdig.Extensions.Tables.Table;
using TableCell = Markdig.Extensions.Tables.TableCell;
using TableRow = Markdig.Extensions.Tables.TableRow;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 表を、罫線の付いた表として描く（SPEC 8.4.1。2026-10-16）。
    ///
    /// カーソルが表の外にあるときだけ、表の行をまとめて1つの部品に置き換える（MarkerHideGenerator）。
    /// カーソルが表の中に入ったら（行番号を押すなど）、表全体を生の Markdown に戻して打てるようにする。
    /// 表を押すと、表エディタ（TableEditor）で編集する（MarkdownView.EditTable。SPEC 8.4.2）。
    /// 描くのは表示だけで、文書には一切触れない。ここがどう間違ってもファイルの中身は壊れない。
    ///
    /// 解析は Markdig に任せる（セルの区切り・寄せ・セルの中の太字やコード）。読めなければ null を返し、生のまま見せる。
    /// （同じ日に、セルの上でそのまま打てる形も試したが、エディタの中に入力欄を入れて作り直す作りが不安定で、やめた）
    /// </summary>
    internal static class TableElement
    {
        private const double CellPaddingX = 10;
        private const double CellPaddingY = 5;

        /// <summary>表の部品の枠に付ける印（Tag）。本文側のマウスの処理（リンクを開く等）が、表の上では動かないようにするため。</summary>
        public const string FrameTag = "MarkdownTable";

        /// <summary>表として読めるか。読めない表は畳まずに生のまま見せる（畳んだのに部品を作れないと、行が見えなくなる）。</summary>
        public static bool CanBuild(string source)
        {
            return Parse(source) != null;
        }

        private static Table Parse(string source)
        {
            try
            {
                var table = Markdig.Markdown.Parse(source, MarkdownParser.Pipeline).OfType<Table>().FirstOrDefault();
                return table != null && table.OfType<TableRow>().Any() ? table : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 表の部品を作る。source は表の行をつないだもの。room は使える幅。
        /// 行を押すと onRowClick に、表の中で何行目（0 始まり。区切りの行も数える）かを渡す。読めなければ null。
        /// </summary>
        public static FrameworkElement Build(string source, double room, Action<int> onRowClick)
        {
            var table = Parse(source);
            if (table == null) return null;

            var rows = table.OfType<TableRow>().ToList();
            var columns = rows.Max(row => row.Count);
            if (columns == 0) return null;

            var grid = new Grid();
            grid.SnapsToDevicePixels = true;

            var widest = new double[columns];

            for (var r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (var c = 0; c < columns; c++)
                {
                    var cell = c < rows[r].Count ? rows[r][c] as TableCell : null;
                    var text = CreateCellText(cell, rows[r].IsHeader, AlignOf(table, c));

                    // 列の幅 = 中身の幅（端数は切り上げ）＋左右の余白＋罫線（1列目は左右で 2px、ほかは右の 1px）＋余裕 2px。
                    // 余裕が無いと、並べるときの端数のずれで最後の1文字が折り返す。
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var rules = c == 0 ? 2 : 1;
                    widest[c] = Math.Max(widest[c], Math.Ceiling(text.DesiredSize.Width) + CellPaddingX * 2 + rules + 2);

                    var border = new Border();
                    border.Child = text;
                    border.Padding = new Thickness(CellPaddingX, CellPaddingY, CellPaddingX, CellPaddingY);
                    border.BorderBrush = Theme.Divider;
                    border.BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1);
                    // 見出し行も、ほかの行と同じ地と字の色（2026-10-18。前は見出し行だけコードの地の色にしていた）。見出し行は字をやや太くするだけ。
                    border.Background = Brushes.Transparent;   // 地が無いと、字の無い所が押せない

                    var line = rows[r].Line;   // 表の中で何行目か（区切りの行も数える）
                    border.MouseLeftButtonDown += (sender, e) =>
                    {
                        e.Handled = true;
                        onRowClick(line);
                    };

                    Grid.SetRow(border, r);
                    Grid.SetColumn(border, c);
                    grid.Children.Add(border);
                }
            }

            // 中身が収まるなら、列は中身の幅（表は左に寄せる）。収まらなければ、本文の幅いっぱいに広げ、
            // 中身の量に合わせて割り振る（短い列は狭く、長い列は広く。セルの中で折り返す）。
            var total = widest.Sum();
            if (room <= 0 || total <= room)
            {
                foreach (var width in widest)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            }
            else
            {
                foreach (var width in widest)
                {
                    var weight = Math.Max(60, Math.Min(width, room * 0.6));
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
                }
                grid.Width = room;
            }

            var frame = new Border();
            frame.Tag = FrameTag;
            frame.Child = grid;
            frame.HorizontalAlignment = HorizontalAlignment.Left;
            frame.Margin = new Thickness(0, 6, 0, 6);
            frame.Cursor = Cursors.IBeam;
            frame.ToolTip = "押すと、表エディタで編集する。Markdown のまま直すときは、行番号を押す";

            return frame;
        }

        /// <summary>表の部品を、行の中の要素にする。文書上は表の行の範囲をまるごと受け持つ。</summary>
        public static VisualLineElement CreateElement(FrameworkElement table, int documentLength, ITextRunConstructionContext context)
        {
            table.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var height = table.DesiredSize.Height;

            // 部品は文字のベースラインに乗るので、そのままだと下に文字の下がりぶんの隙間が残る。画像と同じく、下がりぶん上げる。
            var properties = context.GlobalTextRunProperties;
            var family = properties.Typeface.FontFamily;
            var descent = (family.LineSpacing - family.Baseline) * properties.FontRenderingEmSize;
            TextBlock.SetBaselineOffset(table, Math.Max(0, height - descent));

            return new Element(table, documentLength);
        }

        private static TextBlock CreateCellText(TableCell cell, bool header, TableColumnAlign? align)
        {
            var text = new TextBlock();
            text.TextWrapping = TextWrapping.Wrap;
            text.FontFamily = Theme.BodyFont;
            text.FontSize = Theme.BodySize * 0.9;
            text.Foreground = Theme.Text;
            if (header) text.FontWeight = FontWeights.SemiBold;

            if (align == TableColumnAlign.Center) text.TextAlignment = TextAlignment.Center;
            else if (align == TableColumnAlign.Right) text.TextAlignment = TextAlignment.Right;

            if (cell == null) return text;

            foreach (var block in cell)
            {
                var paragraph = block as LeafBlock;
                if (paragraph == null || paragraph.Inline == null) continue;

                foreach (var inline in paragraph.Inline)
                    AddInline(text.Inlines, inline, FontWeights.Normal, FontStyles.Normal, false);
            }

            return text;
        }

        /// <summary>セルの中の装飾。太字・斜体・取り消し線・コード・リンク（名前だけ）・改行。</summary>
        private static void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline,
            FontWeight weight, FontStyle style, bool strike)
        {
            var literal = inline as LiteralInline;
            if (literal != null)
            {
                target.Add(Styled(new Run(literal.Content.ToString()), weight, style, strike));
                return;
            }

            var code = inline as CodeInline;
            if (code != null)
            {
                var run = new Run(code.Content);
                run.FontFamily = Theme.CodeFont;
                run.Foreground = Theme.CodeFore;
                run.Background = Theme.CodeBack;
                target.Add(run);
                return;
            }

            if (inline is LineBreakInline)
            {
                target.Add(new LineBreak());
                return;
            }

            var emphasis = inline as EmphasisInline;
            if (emphasis != null)
            {
                var nextWeight = weight;
                var nextStyle = style;
                var nextStrike = strike;

                if (emphasis.DelimiterChar == '~') nextStrike = true;
                else if (emphasis.DelimiterCount >= 2) nextWeight = FontWeights.Bold;
                else nextStyle = FontStyles.Italic;

                foreach (var child in emphasis)
                    AddInline(target, child, nextWeight, nextStyle, nextStrike);
                return;
            }

            var link = inline as LinkInline;
            if (link != null)
            {
                var span = new Span();
                span.Foreground = Theme.Link;
                foreach (var child in link)
                    AddInline(span.Inlines, child, weight, style, strike);
                target.Add(span);
                return;
            }

            var container = inline as ContainerInline;
            if (container != null)
            {
                foreach (var child in container)
                    AddInline(target, child, weight, style, strike);
                return;
            }

            // そのほかは、書いてあるとおりの文字で出す。<br> はセルの中の改行にする。
            var html = inline as HtmlInline;
            if (html != null)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(html.Tag, @"^<br\s*/?>$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    target.Add(new LineBreak());
                else
                    target.Add(Styled(new Run(html.Tag), weight, style, strike));
                return;
            }

            var autolink = inline as AutolinkInline;
            if (autolink != null)
            {
                var run = Styled(new Run(autolink.Url), weight, style, strike);
                run.Foreground = Theme.Link;
                target.Add(run);
                return;
            }

            var entity = inline as HtmlEntityInline;
            if (entity != null) target.Add(Styled(new Run(entity.Transcoded.ToString()), weight, style, strike));
        }

        private static Run Styled(Run run, FontWeight weight, FontStyle style, bool strike)
        {
            run.FontWeight = weight;
            run.FontStyle = style;
            if (strike) run.TextDecorations = TextDecorations.Strikethrough;
            return run;
        }

        private static TableColumnAlign? AlignOf(Table table, int column)
        {
            return column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
        }

        /// <summary>
        /// 表の部品。カーソルの止まる位置を持たせない（画像と同じ）。
        /// 表の中へカーソルが入ると、表は生の Markdown に戻るので、中で止まる必要が無い。
        /// </summary>
        private sealed class Element : InlineObjectElement
        {
            public Element(UIElement element, int documentLength) : base(documentLength, element)
            {
            }

            public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction,
                ICSharpCode.AvalonEdit.Document.CaretPositioningMode mode)
            {
                return -1;
            }
        }
    }
}
