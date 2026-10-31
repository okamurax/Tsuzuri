using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TableEditor
{
    /// <summary>
    /// 表エディタの画面（SPEC 8.4.2）。一番上の段に「保存」「寄せ」「終了」、その下にマス目（TableGrid）。
    ///
    /// 受け渡しは文字列。1行目が列の寄せ、2行目からが表の行（1行目が見出し行）で、セルはタブ区切り。
    ///   #align	N	C	R      ← N: 指定なし、L: 左、C: 中央、R: 右
    ///   名前	担当	金額
    /// 「保存」で Result に同じ形の文字列を入れて閉じる。
    /// </summary>
    internal sealed class TableEditorWindow : Window
    {
        private readonly TableGrid _grid;

        // 開いたときの中身。閉じるときに変わっているかを比べる。
        private readonly string _opened;
        private bool _saved;
        private string _result;

        public TableEditorWindow(string exchange)
        {
            // タイトルバーを出さない（本体と同じ）。縁は残るので、大きさは変えられる。閉じるのは「終了」で。
            Title = "表エディタ";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Width = 960;
            Height = 600;
            MinWidth = 480;
            MinHeight = 300;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = Palette.Chrome;
            Foreground = Palette.Text;
            FontFamily = Palette.Font;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _grid = new TableGrid(TableData.Parse(exchange));

            var dock = new DockPanel();
            dock.LastChildFill = true;
            dock.Children.Add(CreateToolbar());
            dock.Children.Add(_grid);
            Content = dock;

            _opened = _grid.ToExchange();

            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            Activated += delegate { _grid.FocusEditor(); };
            SourceInitialized += delegate { UseDarkTitleBar(); };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>
        /// 上端の縁も紺にする（本体の MainWindow.UseDarkTitleBar と同じ）。タイトルバーを出さなくても、大きさを変えられるウィンドウは
        /// 上端に Windows が描く縁が残り、何もしないと明るい色の細い線に見える（2026-10-18）。
        /// 属性番号は Windows 10 2004 で 20 に変わった。古いビルド向けに 19 も試す。色の 35・36 は Windows 11 だけで効く（色は 0x00BBGGRR）。
        /// </summary>
        private void UseDarkTitleBar()
        {
            try
            {
                var window = new WindowInteropHelper(this).Handle;
                if (window == IntPtr.Zero) return;

                var on = 1;
                if (DwmSetWindowAttribute(window, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(window, 19, ref on, sizeof(int));

                var caption = 0x00392319;   // ツールバーの紺 #192339
                DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
                var captionText = 0x00E4DFDD;
                DwmSetWindowAttribute(window, 36, ref captionText, sizeof(int));
            }
            catch (Exception)
            {
                // 古い Windows では用意されていない。見た目だけの話なので黙って諦める。
            }
        }

        /// <summary>保存して閉じたときの表（受け渡しの形）。保存していなければ null。</summary>
        public string Result
        {
            get { return _saved ? _result : null; }
        }

        /// <summary>
        /// 一番上の段。本体のツールバーと同じ紺の地（高さ 40px）に、本体と同じ形のボタン（App.xaml の ToolButton）。
        /// 並びは 保存・寄せ・終了。寄せは押すと選ぶメニューが出る（2026-10-18。右クリックのメニューをやめ、寄せだけここに置いた）。
        /// </summary>
        private UIElement CreateToolbar()
        {
            var save = CreateButton("保存(_S)", "Ctrl+S。表を Markdown の表に書き戻して閉じる");
            save.Click += delegate { Save(); };

            var align = CreateButton("寄せ(_A)", "選んでいる列の寄せを変える（Markdown の表の寄せは列ごと）");
            align.Click += delegate { ShowAlignMenu(align); };

            var close = CreateButton("終了(_X)", "閉じる。変えていれば、書き戻すかを聞く");
            close.Click += delegate { Close(); };

            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(8, 0, 8, 0);
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(save);
            row.Children.Add(align);
            row.Children.Add(close);

            var bar = new Border();
            bar.Height = 40;
            bar.Background = Palette.Chrome;
            bar.Child = row;
            DockPanel.SetDock(bar, Dock.Top);

            // タイトルバーが無いので、この段の空いているところをドラッグしてウィンドウを動かす。ダブルクリックで最大化と元の大きさを切り替える。
            bar.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return;
                }

                if (WindowState != WindowState.Normal) return;

                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // ボタンを離したあとに届いたとき。動かさないだけ。
                }
            };

            return bar;
        }

        /// <summary>
        /// 寄せを選ぶ小さな画面を、ボタンの下に出す（2026-10-18）。見た目は本体のエクスポートの画面と同じ
        /// （サイドバーの地に角丸の枠と影、見出しは小さめの太字）。選んだ寄せは、選んでいる列すべてにすぐ入る。
        /// 今の寄せに ✓ を付ける（選んでいる列で違えば、どれにも付けない）。↑↓ で選び、Enter で決める、Esc でやめる。
        /// </summary>
        private void ShowAlignMenu(Button button)
        {
            var current = _grid.CurrentAlign();
            var choices = new[] { 'N', 'L', 'C', 'R' };
            var names = new[] { "指定なし", "左寄せ", "中央寄せ", "右寄せ" };

            var popup = new Popup();
            var rows = new List<Border>();
            var highlighted = Math.Max(0, Array.IndexOf(choices, current));

            Action<int> highlight = index =>
            {
                highlighted = index;
                for (var i = 0; i < rows.Count; i++) rows[i].Background = i == index ? Palette.Hover : Brushes.Transparent;
            };

            Action<int> choose = index =>
            {
                popup.IsOpen = false;
                _grid.SetAlign(choices[index]);
            };

            var list = new StackPanel();

            var title = new TextBlock();
            title.Text = "列の寄せ";
            title.FontSize = 12;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Palette.SidebarHeaderText;
            title.Margin = new Thickness(0, 0, 0, 8);
            list.Children.Add(title);

            for (var i = 0; i < choices.Length; i++)
            {
                var index = i;

                var mark = new TextBlock();
                mark.Text = choices[i] == current ? "✓" : string.Empty;
                mark.Width = 20;
                mark.Foreground = Palette.Text;

                var name = new TextBlock();
                name.Text = names[i];
                name.Foreground = Palette.Text;

                var content = new StackPanel();
                content.Orientation = Orientation.Horizontal;
                content.Children.Add(mark);
                content.Children.Add(name);

                var row = new Border();
                row.Child = content;
                row.Padding = new Thickness(8, 5, 16, 5);
                row.CornerRadius = new CornerRadius(4);
                row.Background = Brushes.Transparent;   // 地が無いと、字の無い所が押せない
                row.Cursor = Cursors.Hand;
                row.MouseEnter += delegate { highlight(index); };
                row.MouseLeftButtonUp += delegate { choose(index); };
                rows.Add(row);
                list.Children.Add(row);
            }

            var frame = new Border();
            frame.Background = Palette.Sidebar;
            frame.BorderBrush = Palette.FrameBorder;
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(8);
            frame.Padding = new Thickness(10, 12, 10, 10);
            frame.Margin = new Thickness(10, 4, 10, 14);   // 影の分の空き
            frame.MinWidth = 150;
            frame.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.45,
            };
            frame.Child = list;
            frame.Focusable = true;
            frame.FocusVisualStyle = null;

            // ポップアップは別の窓なので、ウィンドウの字の設定を引き継がない。揃えておく。
            TextElement.SetFontFamily(frame, FontFamily);
            TextElement.SetFontSize(frame, 13);
            TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);

            frame.PreviewKeyDown += (sender, e) =>
            {
                switch (e.Key)
                {
                    case Key.Up: highlight((highlighted + rows.Count - 1) % rows.Count); break;
                    case Key.Down: highlight((highlighted + 1) % rows.Count); break;
                    case Key.Enter: choose(highlighted); break;
                    case Key.Escape: popup.IsOpen = false; break;
                    default: return;
                }

                e.Handled = true;
            };

            popup.PlacementTarget = button;
            popup.Placement = PlacementMode.Bottom;
            popup.HorizontalOffset = -10;   // 影の分の空きを差し引いて、枠の左端をボタンの左端に揃える
            popup.AllowsTransparency = true;
            popup.StaysOpen = false;
            popup.Child = frame;
            popup.Opened += delegate
            {
                highlight(highlighted);
                frame.Focus();
            };
            popup.Closed += delegate { _grid.FocusEditor(); };
            popup.IsOpen = true;
        }

        private static Button CreateButton(string text, string tip)
        {
            var button = new Button();
            button.Content = text;
            button.ToolTip = tip;
            button.FontSize = 13;
            button.Focusable = false;   // フォーカスはいつもマス目の字の欄に置く

            // 本体と同じプロセスで開くので、本体のボタンの形（App.xaml）をそのまま使う。
            var style = Application.Current != null ? Application.Current.TryFindResource("ToolButton") as Style : null;
            if (style != null) button.Style = style;
            else button.Foreground = Palette.Text;

            return button;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Save();
                e.Handled = true;
            }
        }

        private void Save()
        {
            _grid.CommitEdit();   // 打っている途中のセルも確定する
            _result = _grid.ToExchange();
            _saved = true;
            Close();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_saved) return;

            _grid.CommitEdit();
            if (_grid.ToExchange() == _opened) return;   // 何も変えていない

            var answer = MessageBox.Show(this, "変更を表に書き戻しますか？", "表エディタ",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                _result = _grid.ToExchange();
                _saved = true;
            }
        }
    }
}
