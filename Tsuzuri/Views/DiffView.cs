using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// ファイラーの git の印を押したときの差分（2026-11-24。利用者の考え。SPEC 4章）。ランチャーの下半分に出す（MainWindow.Overlays.cs）。
    /// git diff の形の文をそのまま出し、足した行は緑、消した行は赤の地を行の幅いっぱいに敷く。読むだけで、直せない（選んで写すことはできる）。
    /// </summary>
    public sealed class DiffView : TextEditor
    {
        public DiffView()
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            WordWrap = false;
            FontFamily = Theme.CodeFont;
            FontSize = 13;
            Foreground = Theme.Text;
            Background = Brushes.Transparent;
            Padding = new Thickness(8, 4, 8, 4);
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Options.EnableHyperlinks = false;
            Options.EnableEmailHyperlinks = false;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextArea.TextView.BackgroundRenderers.Add(new LineBackground(this));
            TextArea.TextView.LineTransformers.Add(new LineColor());
        }

        /// <summary>出す文を入れ替え、先頭へ戻す。</summary>
        public void Show(string text)
        {
            Document.Text = text ?? string.Empty;
            ScrollToHome();
        }

        private enum Kind
        {
            Plain,
            Added,
            Removed,
            Hunk,
            Header,
        }

        private static Kind KindOf(string line)
        {
            if (line.Length == 0) return Kind.Plain;
            if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal)) return Kind.Header;
            if (line[0] == '+') return Kind.Added;
            if (line[0] == '-') return Kind.Removed;
            if (line.StartsWith("@@", StringComparison.Ordinal)) return Kind.Hunk;
            if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal)
                || line.StartsWith("similarity ", StringComparison.Ordinal) || line.StartsWith("rename ", StringComparison.Ordinal)
                || line.StartsWith("old mode", StringComparison.Ordinal) || line.StartsWith("new mode", StringComparison.Ordinal)
                || line.StartsWith("Binary files", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal)
                || line.StartsWith("? ", StringComparison.Ordinal) || line.StartsWith("\\ ", StringComparison.Ordinal))
                return Kind.Header;
            return Kind.Plain;
        }

        private static readonly Brush AddedBack = Freeze(Color.FromArgb(0x40, 0x3F, 0xB9, 0x50));
        private static readonly Brush RemovedBack = Freeze(Color.FromArgb(0x40, 0xF8, 0x51, 0x49));

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>足した行・消した行の地を、行の幅いっぱいに敷く。</summary>
        private sealed class LineBackground : IBackgroundRenderer
        {
            private readonly DiffView _owner;

            public LineBackground(DiffView owner)
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
                foreach (var visual in textView.VisualLines)
                {
                    var line = visual.FirstDocumentLine;
                    var kind = KindOf(_owner.Document.GetText(line.Offset, Math.Min(line.Length, 16)));
                    var brush = kind == Kind.Added ? AddedBack : kind == Kind.Removed ? RemovedBack : null;
                    if (brush == null) continue;
                    var top = visual.VisualTop - textView.VerticalOffset;
                    drawingContext.DrawRectangle(brush, null, new Rect(0, top, Math.Max(textView.ActualWidth, 1), visual.Height));
                }
            }
        }

        /// <summary>区切り（@@）は青、見出しの行（diff・index・---・+++ など）は薄い字にする。</summary>
        private sealed class LineColor : DocumentColorizingTransformer
        {
            protected override void ColorizeLine(DocumentLine line)
            {
                if (line.Length == 0) return;
                var kind = KindOf(CurrentContext.Document.GetText(line.Offset, Math.Min(line.Length, 16)));
                Brush brush = kind == Kind.Hunk ? Theme.Link : kind == Kind.Header ? Theme.Muted : null;
                if (brush == null) return;
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
