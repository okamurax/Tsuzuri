using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiLink.Agents;
using AiLink.Office;

namespace AiLink.Views
{
    /// <summary>
    /// ランチャーのファイラーの下の段の、開いている Excel の一覧（2026-11-24。利用者の考え。SPEC 4章・19章）。
    /// ブックを並べ、その下に字下げしてシートを並べる。シートを押すと選び（選んだ色の地）、もう一度押すと外す。いくつでも選べる。初めは何も選んでいない。
    /// 選んだブックとシート（名前だけ。中身は渡さない）が AI の欄の Claude に渡り、AI への「このシート」になる（MainWindow.Excel.cs）。Excel の側は何も切り替えない。
    /// Excel でどのブック・シートが前に出ているかは使わない（手で作業しているシートが、AI に扱ってほしいシートとは限らないため。利用者の判断）。
    /// 保存していない変更があるブックは、名前の右に「※」（2026-11-25。利用者の判断。Claude の分も、手で直した分も。保存した直後は付かない。
    /// NOW・RAND などの式があるブックは、開いただけで計算し直されて付くことがある）。
    /// 一覧は MainWindow.Excel.cs が読み直して Show に渡す（ランチャーを出したとき・窓に戻ったとき・話しかけたとき・「更新」）。
    /// </summary>
    public sealed class ExcelListView : DockPanel
    {
        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer();

        /// <summary>シートを押した: ブック（フルパスか、保存していなければ名前）とシート。</summary>
        public event Action<string, string> SheetClicked;

        /// <summary>「更新」を押した。</summary>
        public event Action RefreshRequested;

        public ExcelListView()
        {
            var title = new TextBlock
            {
                Text = "Excel",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("SidebarText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var refresh = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Excel の一覧を読み直す",
            };
            var style = Application.Current != null ? Application.Current.TryFindResource("ToolButton") as Style : null;
            if (style != null) refresh.Style = style;
            refresh.Click += delegate
            {
                var requested = RefreshRequested;
                if (requested != null) requested();
            };
            DockPanel.SetDock(refresh, Dock.Right);

            var head = new DockPanel { Margin = new Thickness(12, 0, 6, 0), Height = 28 };
            head.Children.Add(refresh);
            head.Children.Add(title);
            DockPanel.SetDock(head, Dock.Top);
            Children.Add(head);

            _scroll.Content = _list;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Children.Add(_scroll);

            ShowNote("読んでいます…");
        }

        /// <summary>一覧を出し直す。null なら読めなかった（problem にわけ）。</summary>
        public void Show(List<ExcelBridge.Book> books, Func<string, bool> guarded, Func<ExcelBridge.Book, string, bool> chosenOf, string problem)
        {
            _list.Children.Clear();
            if (books == null)
            {
                ShowNote(string.IsNullOrEmpty(problem) ? "Excel を読めませんでした" : problem);
                return;
            }
            if (books.Count == 0)
            {
                ShowNote("開いている Excel のブックはありません");
                return;
            }

            foreach (var book in books)
            {
                var key = book.Path ?? book.Name;
                var hidden = guarded != null && guarded(book.Path);
                _list.Children.Add(CreateRow(key, null, book.Name + (book.Path == null ? "（未保存）" : string.Empty) + (book.Unsaved ? " ※" : string.Empty), 12, true, false,
                                             (book.Path ?? book.Name + "（まだ保存していない新しいブック）")
                                             + (book.Unsaved ? "\n※ 保存していない変更がある（Claude の分も、手で直した分も。保存せずに閉じれば、最後に保存したときに戻る。NOW・RAND などの式があると、開いただけでも付く）" : string.Empty)
                                             + (hidden ? "\nAI に渡さない場所（か、確かめられないネットの場所）のブック。シートを選べない" : string.Empty)));
                foreach (var sheet in book.Sheets)
                {
                    var chosen = chosenOf != null && chosenOf(book, sheet);
                    _list.Children.Add(CreateRow(key, hidden ? null : sheet, sheet, 34, false, chosen,
                                                 (hidden ? "AI に渡さない場所のブックなので、選べない"
                                                  : chosen ? "選んでいるシート（Claude への「このシート」。ブックとシートの名前だけを渡し、中身は渡さない）。もう一度押すと外す"
                                                  : "押すと選んで、ブックとシートの名前を Claude に渡す（「このシート」になる。いくつでも選べる。中身は渡さない）")));
                }
            }
        }

        private void ShowNote(string message)
        {
            _list.Children.Clear();
            _list.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = Brush("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 4, 8, 4),
            });
        }

        private Border CreateRow(string book, string sheet, string label, double indent, bool isBook, bool chosen, string tip)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = isBook ? 13 : 12,
                FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = chosen ? Brush("TextPrimary") : Brush("SidebarText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var content = new DockPanel();
            if (isBook)
            {
                var glyph = FileIcon.Create(label.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase) ? label : "book.xlsx", false);
                glyph.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(glyph, Dock.Left);
                content.Children.Add(glyph);
            }
            content.Children.Add(text);

            var normal = chosen ? Brush("SelectionFill") : Brushes.Transparent;
            var row = new Border
            {
                Child = content,
                Padding = new Thickness(indent, 3, 8, 3),
                Background = normal,
                ToolTip = tip,
            };
            if (isBook || sheet == null) return row;   // ブックの行と、選べないシートは押せない

            row.Cursor = Cursors.Hand;
            row.MouseEnter += delegate { if (!chosen) row.Background = Brush("HoverFill"); };
            row.MouseLeave += delegate { row.Background = normal; };
            row.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                if (e.ClickCount > 1) return;
                var clicked = SheetClicked;
                if (clicked != null) clicked(book, sheet);
            };
            return row;
        }

        private static Brush Brush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Gray;
        }
    }
}
