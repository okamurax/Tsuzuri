using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 水平線（`---`）を本当の線として描く（SPEC 8章）。
    ///
    /// AvalonEdit は行に罫線を引けないので、背景の層に自分で描く。
    /// `---` の文字そのものは、カーソルの無い行では画面から消している。
    /// </summary>
    public sealed class RuleRenderer : IBackgroundRenderer
    {
        private static readonly Pen Stroke = CreatePen();

        private readonly MarkdownView _owner;

        public RuleRenderer(MarkdownView owner)
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

            var width = textView.ActualWidth;
            if (width <= 0) return;

            foreach (var visualLine in textView.VisualLines)
            {
                var info = _owner.GetLineInfo(visualLine.FirstDocumentLine.LineNumber);
                if (info == null || info.Kind != LineKind.Rule) continue;

                var top = visualLine.VisualTop - textView.VerticalOffset;

                // 0.5 ずらして 1px の線がぼやけないようにする。
                var y = Math.Round(top + visualLine.Height / 2.0) + 0.5;

                drawingContext.DrawLine(Stroke, new Point(0, y), new Point(width, y));
            }
        }

        private static Pen CreatePen()
        {
            var pen = new Pen(Theme.RuleStroke, 1.0);
            pen.Freeze();
            return pen;
        }
    }
}
