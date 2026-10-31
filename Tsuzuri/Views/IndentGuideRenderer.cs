using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 見出しの深さを示す縦線を、字下げの中に描く（SPEC 8.4）。
    ///
    /// 字下げそのものは MarkerHideGenerator が行頭に幅を空けて作る。ここはその空いた所に
    /// 段の数だけ線を引くだけ。行の高さいっぱいに引くので、折り返した行でも線が切れない。
    /// </summary>
    public sealed class IndentGuideRenderer : IBackgroundRenderer
    {
        /// <summary>各段の左端から線までの距離。本文との間を空ける。</summary>
        private const double Offset = 4.0;

        private readonly MarkdownView _owner;

        public IndentGuideRenderer(MarkdownView owner)
        {
            _owner = owner;
        }

        public KnownLayer Layer
        {
            get { return KnownLayer.Background; }
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;

            foreach (var visualLine in textView.VisualLines)
            {
                var info = _owner.GetLineInfo(visualLine.FirstDocumentLine.LineNumber);
                if (info == null || info.Depth <= 0) continue;

                var top = visualLine.VisualTop - textView.VerticalOffset;

                for (var depth = 0; depth < info.Depth; depth++)
                {
                    var x = Math.Round(depth * Theme.IndentStep + Offset) - textView.HorizontalOffset;

                    drawingContext.DrawRectangle(Theme.IndentGuide, null,
                        new Rect(x, top, 1.0, visualLine.Height));
                }
            }
        }
    }
}
