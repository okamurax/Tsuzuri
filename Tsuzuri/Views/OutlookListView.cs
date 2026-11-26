using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tsuzuri.Office;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 開いている Outlook（classic）のメールのフォルダの一覧（2026-11-26。利用者の考え。Tsuzuri と AiLink で同じファイルを使う。AiLink はリンクしてコンパイルする）。
    /// Excel の一覧の下に置く（Stack）。ストア（メールボックス・PST など）を見出しに、その下にメールのフォルダを字下げして並べる。
    /// 選び方はエクスプローラーと同じ: 押すとそれだけを選び（ほかは外れる）、Ctrl を押しながらだと足す・外す、何もない所を押すと全部外す。ストアの行は押せない。
    /// 選んだフォルダ（パスだけ。中身は渡さない）が AI への「このフォルダ」になる（OutlookLink）。Outlook の側は何も切り替えない。
    /// </summary>
    public sealed class OutlookListView : DockPanel
    {
        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer();

        /// <summary>フォルダの行を押した: フォルダのパス（\\ストア\受信トレイ\…）、Ctrl を押していたか。</summary>
        public event Action<string, bool> RowClicked;

        /// <summary>一覧の何もない所を押した（選んでいるフォルダを全部外す）。</summary>
        public event Action EmptyClicked;

        /// <summary>「更新」を押した。</summary>
        public event Action RefreshRequested;

        public OutlookListView()
        {
            var title = new TextBlock
            {
                Text = "Outlook",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("SidebarText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var refresh = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Outlook のフォルダの一覧を読み直す",
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
            // 何もない所でも押せるように地を塗り、フォーカスを取らせない（ExcelListView と同じ。取れると押されたことを自分で止めてしまう）
            _scroll.Background = Brushes.Transparent;
            _scroll.Focusable = false;
            _scroll.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ClickCount > 1) return;
                var empty = EmptyClicked;
                if (empty != null) empty();
            };
            Children.Add(_scroll);

            ShowNote("押すと読みます（「更新」）");
        }

        /// <summary>一覧を出し直す。null なら読めなかった（problem にわけ）。</summary>
        public void Show(List<OutlookBridge.Store> stores, Func<string, bool> chosenOf, string problem)
        {
            _list.Children.Clear();
            if (stores == null)
            {
                ShowNote(string.IsNullOrEmpty(problem) ? "Outlook を読めませんでした" : problem);
                return;
            }
            if (stores.Count == 0)
            {
                ShowNote("Outlook にメールのフォルダがありません");
                return;
            }

            foreach (var store in stores)
            {
                _list.Children.Add(CreateRow(null, store.Name, 12, true, false,
                                             store.Name + (string.IsNullOrEmpty(store.Problem) ? string.Empty : "\n" + store.Problem)));
                if (!string.IsNullOrEmpty(store.Problem) && store.Folders.Count == 0)
                {
                    _list.Children.Add(CreateRow(null, store.Problem, 34, false, false, store.Problem));
                    continue;
                }
                foreach (var folder in store.Folders)
                {
                    var chosen = chosenOf != null && chosenOf(folder.Path);
                    _list.Children.Add(CreateRow(folder.Path, folder.Name, 34 + 14 * folder.Depth, false, chosen,
                                                 folder.Path + "\n"
                                                 + (chosen ? "選んでいるフォルダ（AI への「このフォルダ」。フォルダのパスだけを渡し、メールは渡さない）。Ctrl を押しながら押すと外す"
                                                    : "押すとこのフォルダだけを選んで、パスを AI に渡す（「このフォルダ」になる。メールは渡さない）。Ctrl を押しながらだと足す。何もない所を押すと全部外す")));
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

        /// <summary>行。path が null なら押せない（ストアの見出し・知らせ）。</summary>
        private Border CreateRow(string path, string label, double indent, bool isStore, bool chosen, string tip)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = isStore ? 13 : 12,
                FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = chosen ? Brush("TextPrimary") : isStore || path != null ? Brush("SidebarText") : Brush("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var normal = chosen ? Brush("SelectionFill") : Brushes.Transparent;
            var row = new Border
            {
                Child = text,
                Padding = new Thickness(indent, 3, 8, 3),
                Background = normal,
                ToolTip = tip,
            };
            if (path == null)
            {
                // 押せない行（ストアの見出し・知らせ）を押しても、何もない所を押したことにしない（選びを外さない）
                row.MouseLeftButtonDown += (sender, e) => e.Handled = true;
                return row;
            }

            row.Cursor = Cursors.Hand;
            row.MouseEnter += delegate { if (!chosen) row.Background = Brush("HoverFill"); };
            row.MouseLeave += delegate { row.Background = normal; };
            row.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                if (e.ClickCount > 1) return;
                var clicked = RowClicked;
                if (clicked != null) clicked(path, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            };
            return row;
        }

        /// <summary>Excel の一覧（上）と Outlook の一覧（下）を、仕切りで高さを変えられる形に重ねる。</summary>
        public static Grid Stack(UIElement top, UIElement bottom)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            var splitter = new GridSplitter
            {
                Height = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                Focusable = false,
            };
            // 上下の境界の見た目（アプリの RowSplitter。無ければ薄い線）
            var rowStyle = Application.Current != null ? Application.Current.TryFindResource("RowSplitter") as Style : null;
            if (rowStyle != null) splitter.Style = rowStyle;
            else splitter.Background = Brush("BorderBrushSoft");
            Grid.SetRow(top, 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(bottom, 2);
            grid.Children.Add(top);
            grid.Children.Add(splitter);
            grid.Children.Add(bottom);
            return grid;
        }

        private static Brush Brush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Gray;
        }
    }
}
