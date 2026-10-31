using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// コードブロック（``` で囲った所）の中身を一度にコピーするボタン（SPEC 8章。2026-10-29）。
    ///
    /// 開きの ``` の行の右端に「コピー」を置く。本文の流れには入れず、文字の上の層に重ねる（行の組み方を変えないため）。
    /// 画面に出ている行が変わるたび（スクロール・打った・幅が変わった）に置き直す。
    /// コピーするのは、開きと閉じの ``` の間の行（``` の行は含めない）。ファイルの文字そのまま。
    /// </summary>
    public sealed class CodeCopyLayer : Canvas
    {
        private readonly MarkdownView _owner;
        private readonly TextView _textView;

        public CodeCopyLayer(MarkdownView owner, TextView textView)
        {
            _owner = owner;
            _textView = textView;
            ClipToBounds = true;
            _textView.VisualLinesChanged += delegate { Place(); };
        }

        private void Place()
        {
            Children.Clear();
            if (!_textView.VisualLinesValid || _textView.VisualLines.Count == 0) return;

            var document = _textView.Document;
            if (document == null) return;

            var lastVisible = _textView.VisualLines[_textView.VisualLines.Count - 1].LastDocumentLine.LineNumber;

            // 開きか閉じかは、先頭から ``` の行を数えて決める（中身が空のブロックや、続けて置いたブロックでも取り違えない）。
            var openings = new HashSet<int>();
            var open = false;
            for (var number = 1; number <= lastVisible; number++)
            {
                var info = _owner.GetLineInfo(number);
                if (info == null || info.Kind != LineKind.Fence)
                {
                    if (info == null || info.Kind != LineKind.Code) open = false;   // ブロックの外に出た（閉じの無いブロックの終わり等）
                    continue;
                }

                if (!open) openings.Add(number);
                open = !open;
            }

            foreach (var line in _textView.VisualLines)
            {
                var number = line.FirstDocumentLine.LineNumber;
                if (!openings.Contains(number)) continue;

                var button = CreateButton(number);
                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var top = line.VisualTop - _textView.VerticalOffset + Math.Max(0, (line.Height - button.DesiredSize.Height) / 2);
                SetLeft(button, Math.Max(0, _textView.ActualWidth - button.DesiredSize.Width - 12));
                SetTop(button, top);
                Children.Add(button);
            }
        }

        private Button CreateButton(int fenceLine)
        {
            var button = new Button
            {
                Content = "コピー",
                ToolTip = "このコードブロックの中身をコピーする",
                FontSize = 11,
                Padding = new Thickness(8, 1, 8, 1),
                Focusable = false,
                Cursor = Cursors.Arrow,
                Foreground = Theme.Muted,
            };
            var style = Application.Current != null ? Application.Current.TryFindResource("ToolButton") as Style : null;
            if (style != null) button.Style = style;

            button.Click += delegate
            {
                if (!Copy(fenceLine)) return;

                button.Content = "コピーしました";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += delegate
                {
                    timer.Stop();
                    button.Content = "コピー";
                };
                timer.Start();
            };
            return button;
        }

        /// <summary>開きの ``` の次の行から、閉じの ``` の手前（無ければブロックの終わり）までをクリップボードに入れる。</summary>
        private bool Copy(int fenceLine)
        {
            var document = _textView.Document;
            if (document == null) return false;

            var lines = new List<string>();
            for (var number = fenceLine + 1; number <= document.LineCount; number++)
            {
                var info = _owner.GetLineInfo(number);
                if (info == null || info.Kind != LineKind.Code) break;   // 閉じの ``` か、ブロックの外

                var line = document.GetLineByNumber(number);
                lines.Add(document.GetText(line.Offset, line.Length));
            }

            try
            {
                Clipboard.SetText(string.Join("\r\n", lines));
                return true;
            }
            catch (Exception)
            {
                // ほかのアプリがクリップボードを掴んでいる等。
                return false;
            }
        }
    }
}
