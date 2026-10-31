using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// コードブロックの地を、ブロック全体（``` の行から閉じの ``` の行まで、右端まで）に塗る（SPEC 8章。2026-10-29）。
    ///
    /// 前は字の背景だけを塗っていて、字の無い所（行の右側・空行）は地のままだった。
    /// 続いているコードの行をまとめて、角を丸めた1つの四角にする。左端はその行の字下げ（見出しの深さ）にそろえる。
    /// </summary>
    public sealed class CodeBlockRenderer : IBackgroundRenderer
    {
        private const double Radius = 4.0;
        private const double PadLeft = 6.0;
        private const double PadRight = 8.0;

        private readonly MarkdownView _owner;

        public CodeBlockRenderer(MarkdownView owner)
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

            var right = textView.ActualWidth - PadRight;
            if (right <= 0) return;

            double top = 0, bottom = 0, left = 0;
            var inBlock = false;

            foreach (var visualLine in textView.VisualLines)
            {
                var info = _owner.GetLineInfo(visualLine.FirstDocumentLine.LineNumber);
                var code = info != null && (info.Kind == LineKind.Fence || info.Kind == LineKind.Code);

                if (!code)
                {
                    if (inBlock) Fill(drawingContext, left, top, right, bottom);
                    inBlock = false;
                    continue;
                }

                var lineTop = visualLine.VisualTop - textView.VerticalOffset;
                if (!inBlock)
                {
                    inBlock = true;
                    top = lineTop;
                    left = Math.Max(0, (info.Depth > 0 ? info.Depth * Theme.IndentStep : 0) - PadLeft - textView.HorizontalOffset);
                }
                bottom = lineTop + visualLine.Height;
            }

            if (inBlock) Fill(drawingContext, left, top, right, bottom);
        }

        private static void Fill(DrawingContext drawingContext, double left, double top, double right, double bottom)
        {
            if (right <= left || bottom <= top) return;
            drawingContext.DrawRoundedRectangle(Theme.CodeBack, null, new Rect(left, top, right - left, bottom - top), Radius, Radius);
        }
    }
}
