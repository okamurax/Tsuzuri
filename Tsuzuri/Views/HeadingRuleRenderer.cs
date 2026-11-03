using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 見出しの下に線を引く（2026-11-03。SPEC 8章）。見出し1は薄いグレーの線、見出し2は同じ色の破線。見出し3以下は引かない。
    ///
    /// 線は見出しの文字の始まりから右端まで。折り返した見出しは最後の段の下に引く。
    /// 下線式（=== / ---）の見出しは下線の行をそのまま見せているので、引かない（# の見出しだけ）。
    /// </summary>
    public sealed class HeadingRuleRenderer : IBackgroundRenderer
    {
        private static readonly Pen Solid = CreatePen(false);
        private static readonly Pen Dashed = CreatePen(true);

        /// <summary>線の右端を、描く幅の右端からこれだけ空ける（px）。</summary>
        private const double RightMargin = 8.0;

        private readonly MarkdownView _owner;

        public HeadingRuleRenderer(MarkdownView owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer
        {
            get { return KnownLayer.Background; }
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (textView == null || !textView.VisualLinesValid) return;

            var right = textView.ActualWidth - RightMargin;
            if (right <= 0) return;

            foreach (var visualLine in textView.VisualLines)
            {
                var line = visualLine.FirstDocumentLine;
                var info = _owner.GetLineInfo(line.LineNumber);
                if (info == null || info.Kind != LineKind.Heading || info.ContinuesHeading) continue;
                if (info.Level != 1 && info.Level != 2) continue;

                // 次の行が下線式の見出しの続き・下線なら、# の見出しではない。
                var next = line.LineNumber < _owner.Document.LineCount ? _owner.GetLineInfo(line.LineNumber + 1) : null;
                if (next != null && (next.Kind == LineKind.Setext || next.ContinuesHeading)) continue;

                // 文字の始まり（隠した # と空白の後ろ）。見出しが空なら行の始まり。
                var start = Math.Min(info.Marker.Length, line.Length);
                var column = visualLine.GetVisualColumn(start);
                var first = visualLine.GetTextLine(column);
                var x = visualLine.GetTextLineVisualXPosition(first, column) - textView.HorizontalOffset;
                if (x >= right) continue;

                var last = visualLine.TextLines[visualLine.TextLines.Count - 1];
                var bottom = visualLine.GetTextLineVisualYPosition(last, VisualYPosition.LineBottom) - textView.VerticalOffset;

                // 0.5 ずらして 1px の線がぼやけないようにする。
                var y = Math.Round(bottom) - 0.5;
                drawingContext.DrawLine(info.Level == 1 ? Solid : Dashed, new Point(Math.Round(x), y), new Point(right, y));
            }
        }

        private static Pen CreatePen(bool dashed)
        {
            var pen = new Pen(Theme.HeadingRule, 1.0);
            if (dashed) pen.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
            pen.Freeze();
            return pen;
        }
    }
}
