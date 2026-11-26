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
    /// 左の列の、開いている Excel の一覧（Tsuzuri の Views/ExcelListView.cs から写したもの）。
    /// ブックを並べ、その下に字下げしてシートを並べる。選び方はエクスプローラーと同じ（2026-11-26。利用者の判断）: 押すとそれだけを選び（ほかは外れる）、
    /// Ctrl を押しながらだと足す・外す、Shift を押しながらだと前に押した所からの範囲。ブックの行は、そのブックのシート全部として扱う。何もない所を押すと全部外す。初めは何も選んでいない。
    /// 選んだブックとシート（名前だけ。中身は渡さない）が AI の欄の AI に渡り、AI への「このシート」になる（MainWindow.Excel.cs）。Excel の側は何も切り替えない。
    /// Excel でどのブック・シートが前に出ているかは使わない（手で作業しているシートが、AI に扱ってほしいシートとは限らないため。利用者の判断）。
    /// 保存していない変更があるブックは、名前の右に「※」（2026-11-25。利用者の判断。AI の分も、手で直した分も。保存した直後は付かない。
    /// NOW・RAND などの式があるブックは、開いただけで計算し直されて付くことがある）。
    /// 一覧は MainWindow.Excel.cs が読み直して Show に渡す（窓が出たとき・窓に戻ったとき・「更新」）。
    /// </summary>
    public sealed class ExcelListView : DockPanel
    {
        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer();

        /// <summary>シートかブックの行を押した: ブック（フルパスか、保存していなければ名前）、シート（ブックの行なら null）、Excel のプロセスの番号（同じ名前の未保存のブックを見分ける）、Ctrl を押していたか、Shift を押していたか。</summary>
        public event Action<string, string, long, bool, bool> RowClicked;

        /// <summary>一覧の何もない所を押した（選んでいるシートを全部外す）。</summary>
        public event Action EmptyClicked;

        /// <summary>「更新」を押した。</summary>
        public event Action RefreshRequested;

        /// <summary>一番下の「Excel で AI にいつも伝えること」を押した（メモ帳で開く。MainWindow.Excel.cs の OpenExcelNotes）。</summary>
        public event Action NotesRequested;

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

            // 一番下に1行: Excel を扱うときに AI へいつも伝える前提（2026-11-26。利用者の考え。押すとメモ帳で開き、次に開く会話から AI に渡る）
            var notes = new TextBlock
            {
                Text = "Excel で AI にいつも伝えること",
                FontSize = 12,
                Foreground = Brush("AccentBrush"),
                Margin = new Thickness(12, 4, 8, 6),
                Cursor = Cursors.Hand,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "Excel を読む・書くときに AI へいつも伝える前提（言葉の意味・書き方の好み・社内の決まりなど）を、メモ帳で書く。\n次に開く会話から AI に渡る（今の会話には渡らない）",
            };
            notes.MouseEnter += delegate { notes.TextDecorations = TextDecorations.Underline; };
            notes.MouseLeave += delegate { notes.TextDecorations = null; };
            notes.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                var requested = NotesRequested;
                if (requested != null) requested();
            };
            DockPanel.SetDock(notes, Dock.Bottom);
            Children.Add(notes);

            _scroll.Content = _list;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            // 何もない所でも押せるように地を塗る（行は押されたことを自分で止めるので、ここに来るのは行の外を押したときだけ）。
            // ScrollViewer はフォーカスを取れると、押されたことを自分で止めてしまう（+= の受け手に来ない）ので、フォーカスを取らせない
            _scroll.Background = Brushes.Transparent;
            _scroll.Focusable = false;
            _scroll.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ClickCount > 1) return;
                var empty = EmptyClicked;
                if (empty != null) empty();
            };
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
                _list.Children.Add(CreateRow(hidden ? null : key, null, book.Instance, book.Name + (book.Path == null ? "（未保存）" : string.Empty) + (book.Unsaved ? " ※" : string.Empty), 12, true, false,
                                             (book.Path ?? book.Name + "（まだ保存していない新しいブック）")
                                             + (book.Unsaved ? "\n※ 保存していない変更がある（AI の分も、手で直した分も。保存せずに閉じれば、最後に保存したときに戻る。NOW・RAND などの式があると、開いただけでも付く）" : string.Empty)
                                             + (hidden ? "\nAI に渡さない場所（か、確かめられないネットの場所）のブック。シートを選べない"
                                                : "\n押すと、このブックのシートを全部選ぶ（ほかは外れる）。Ctrl を押しながらだと足す（全部選んでいれば外す）。Shift を押しながらだと範囲。何もない所を押すと全部外す")));
                foreach (var sheet in book.Sheets)
                {
                    var chosen = chosenOf != null && chosenOf(book, sheet);
                    _list.Children.Add(CreateRow(key, hidden ? null : sheet, book.Instance, sheet, 34, false, chosen,
                                                 (hidden ? "AI に渡さない場所のブックなので、選べない"
                                                  : chosen ? "選んでいるシート（AI への「このシート」。ブックとシートの名前だけを渡し、中身は渡さない）。Ctrl を押しながら押すと外す"
                                                  : "押すとこのシートだけを選んで、ブックとシートの名前を AI に渡す（「このシート」になる。中身は渡さない）。Ctrl を押しながらだと足す。Shift を押しながらだと範囲")));
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

        private Border CreateRow(string book, string sheet, long instance, string label, double indent, bool isBook, bool chosen, string tip)
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
            // 選べないシート・AI に渡さない場所のブックの行は押せない（ブックの行の sheet は null なので、ブックは isBook で見る）
            if (isBook ? book == null : sheet == null) return row;

            row.Cursor = Cursors.Hand;
            row.MouseEnter += delegate { if (!chosen) row.Background = Brush("HoverFill"); };
            row.MouseLeave += delegate { row.Background = normal; };
            row.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                if (e.ClickCount > 1) return;
                var modifiers = Keyboard.Modifiers;
                var clicked = RowClicked;
                if (clicked != null) clicked(book, isBook ? null : sheet, instance, (modifiers & ModifierKeys.Control) != 0, (modifiers & ModifierKeys.Shift) != 0);
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
