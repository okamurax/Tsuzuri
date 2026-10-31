using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DrawingEditor
{
    /// <summary>
    /// 図エディタの画面（SPEC 8.4.3）。一番上の段に 保存・終了 と道具（選ぶ・直線・四角・丸・文字・寸法）と削除、
    /// 真ん中に描く面（DrawingCanvas）、右に選んだ図形の寸法の欄（mm）とボード（書き出す範囲）の大きさの欄。見た目は表エディタ・本体と同じ紺。
    /// </summary>
    internal sealed class DrawingEditorWindow : Window
    {
        private static readonly Brush Chrome = Make(0x19, 0x23, 0x39);
        private static readonly Brush Sidebar = Make(0x16, 0x1E, 0x32);
        private static readonly Brush Page = Make(0x10, 0x18, 0x27);
        private static readonly Brush Text = Make(0xDD, 0xDF, 0xE4);
        private static readonly Brush Muted = Make(0x93, 0x97, 0x9F);
        private static readonly Brush HeaderText = Make(0xB3, 0xB6, 0xBC);
        private static readonly Brush InputBorder = Make(0x3A, 0x42, 0x55);
        private static readonly Brush Active = Make(0x2C, 0x34, 0x46);
        private static readonly Brush Divider = Make(0x31, 0x38, 0x4A);

        private readonly DrawingCanvas _canvas;
        private readonly Dictionary<Tool, Button> _tools = new Dictionary<Tool, Button>();
        private Button _delete;
        private readonly TextBlock _boardLabel = new TextBlock();
        private readonly TextBlock _boardNote = new TextBlock();
        private TextBox _boardWidth;
        private TextBox _boardHeight;
        private readonly TextBlock _panelTitle = new TextBlock();
        private readonly StackPanel _fields = new StackPanel();
        private readonly Dictionary<string, TextBox> _fieldBoxes = new Dictionary<string, TextBox>();

        // 数値の欄ごとの「打った値を入れる」処理（2026-10-27）。保存・閉じる前に、打ちかけの値を入れるため（CommitFields）。
        private readonly Dictionary<TextBox, Action> _commits = new Dictionary<TextBox, Action>();

        // 塗りの色見本（四角・丸を選んでいるときだけ出す）。枠の色で今の色を示す。
        private readonly List<KeyValuePair<string, Border>> _swatches = new List<KeyValuePair<string, Border>>();

        private readonly string _opened;
        private bool _saved;
        private DrawingResult _result;

        public DrawingEditorWindow(DrawingFile file)
        {
            Title = "図エディタ";
            WindowStyle = WindowStyle.None;   // タイトルバーを出さない（本体・表エディタと同じ）
            ResizeMode = ResizeMode.CanResize;
            Width = 1100;
            Height = 720;
            MinWidth = 640;
            MinHeight = 400;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = Chrome;
            Foreground = Text;
            FontFamily = new FontFamily("Yu Gothic UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _canvas = new DrawingCanvas(file.Shapes, file.Board, file.Unit);
            _canvas.ToolChanged += delegate { UpdateTools(); };
            _canvas.BoardChanged += delegate { UpdateBoard(); };
            _canvas.SelectionChanged += delegate
            {
                BuildFields();
                _delete.IsEnabled = _canvas.HasSelection;
            };
            _canvas.ShapesChanged += delegate { UpdateFields(); };

            var dock = new DockPanel();
            dock.LastChildFill = true;
            dock.Children.Add(CreateToolbar());
            dock.Children.Add(CreatePanel());
            dock.Children.Add(_canvas);
            Content = dock;

            _opened = Serialize();

            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
            SourceInitialized += delegate { UseDarkTitleBar(); };
            Loaded += delegate
            {
                _canvas.FitToContent();
                _delete.IsEnabled = _canvas.HasSelection;
                UpdateTools();
                UpdateBoard();
                BuildFields();
                _canvas.Focus();
            };
        }

        /// <summary>保存して閉じたときの図。保存していなければ null。</summary>
        public DrawingResult Result
        {
            get { return _saved ? _result : null; }
        }

        private string Serialize()
        {
            return new DrawingFile { Shapes = new List<Shape>(_canvas.Shapes), Board = _canvas.Board, Unit = _canvas.Unit }.Write();
        }

        // =====================================================================
        // 一番上の段
        // =====================================================================

        private UIElement CreateToolbar()
        {
            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(8, 0, 8, 0);
            row.VerticalAlignment = VerticalAlignment.Center;

            var save = CreateButton("保存(_S)", "Ctrl+S。図を保存して閉じる（本文の図も描き直す）");
            save.Click += delegate { Save(); };
            row.Children.Add(save);

            var close = CreateButton("終了(_X)", "閉じる。変えていれば、保存するかを聞く");
            close.Click += delegate { Close(); };
            row.Children.Add(close);

            row.Children.Add(Gap());

            // 道具は1文字のキーでも選べる（2026-10-18）。描いても道具はそのまま。Esc か V で「選ぶ」に戻る。
            AddTool(row, Tool.Select, "選ぶ (V)", "V か Esc。図形を選ぶ・動かす・大きさを変える");
            AddTool(row, Tool.Line, "直線 (L)", "L。押して始点、押して終点（ドラッグでも）。いつも 22.5° ごと、Shift で自由な角度");
            AddTool(row, Tool.Rect, "四角 (R)", "R。押して角、押して反対の角（ドラッグでも）。Shift で正方形");
            AddTool(row, Tool.Ellipse, "丸 (C)", "C。押して角、押して反対の角（ドラッグでも）。Shift で正円");
            AddTool(row, Tool.Text, "文字 (T)", "T。押した所に置いて打つ。Esc か Ctrl+Enter で決める。ダブルクリックで打ち直す");
            AddTool(row, Tool.Dimension, "寸法 (D)", "D。測る2点を押す（ドラッグでも）。図形の端・中点・交点に吸い付く。Shift で水平・垂直");

            row.Children.Add(Gap());

            // 選んでいる図形を消す（Delete キーと同じ。2026-10-18）。何も選んでいなければ押せない。
            _delete = CreateButton("削除 (Del)", "Delete。選んでいる図形を消す（Ctrl+Z で戻せる）");
            _delete.Click += delegate
            {
                _canvas.DeleteSelected();
                _canvas.Focus();
            };
            row.Children.Add(_delete);

            // 右端はボードの大きさ（拡大の率は出さない。2026-10-18）。大きさは右の欄で変える。
            _boardLabel.Foreground = Muted;
            _boardLabel.FontSize = 12;
            _boardLabel.VerticalAlignment = VerticalAlignment.Center;
            _boardLabel.Margin = new Thickness(0, 0, 14, 0);
            _boardLabel.ToolTip = "ボード（書き出す範囲）の大きさ。右の欄で変える。\nホイールで拡大・縮小、真ん中のボタンのドラッグで動かす（Shift+ホイールで左右、Ctrl+ホイールで上下）";
            DockPanel.SetDock(_boardLabel, Dock.Right);

            var inner = new DockPanel();
            inner.LastChildFill = true;
            inner.Children.Add(_boardLabel);
            inner.Children.Add(row);

            var bar = new Border();
            bar.Height = 40;
            bar.Background = Chrome;
            bar.Child = inner;
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

        private void AddTool(Panel row, Tool tool, string text, string tip)
        {
            var button = CreateButton(text, tip);
            button.Click += delegate
            {
                _canvas.CurrentTool = tool;
                _canvas.Focus();
            };
            _tools[tool] = button;
            row.Children.Add(button);
        }

        /// <summary>今の道具のボタンを、押した見た目（少し明るい地）にする。</summary>
        private void UpdateTools()
        {
            foreach (var pair in _tools) pair.Value.Background = pair.Key == _canvas.CurrentTool ? Active : Brushes.Transparent;
        }

        /// <summary>ボードの大きさの表示と欄を合わせる。</summary>
        private void UpdateBoard()
        {
            var board = _canvas.Board;
            _boardLabel.Text = "ボード " + Format(board.Width) + " × " + Format(board.Height) + " mm";

            if (_boardWidth != null && !_boardWidth.IsKeyboardFocusWithin) ShowValue(_boardWidth, board.Width);
            if (_boardHeight != null && !_boardHeight.IsKeyboardFocusWithin) ShowValue(_boardHeight, board.Height);

            // 本文・Word に入れるときの倍率（ボードの幅 ÷ 160）。
            var ratio = board.Width / Geometry.OutputWidth;
            _boardNote.Text = "本文・Word には幅 " + Format(Geometry.OutputWidth) + "mm で入る（"
                + (Math.Abs(ratio - 1) < 1e-9 ? "そのままの大きさ" : ratio > 1 ? "1/" + Format(ratio) + " に縮める" : Format(1 / ratio) + " 倍に広げる")
                + "）。文字・線の大きさは、ボードを変えても変わらない（何も描いていない間は、ボードに合わせて変わる）";
        }

        private static UIElement Gap()
        {
            var gap = new Border();
            gap.Width = 1;
            gap.Height = 16;
            gap.Margin = new Thickness(8, 0, 10, 0);
            gap.Background = Divider;
            gap.VerticalAlignment = VerticalAlignment.Center;
            return gap;
        }

        private static Button CreateButton(string text, string tip)
        {
            var button = new Button();
            button.Content = text;
            button.ToolTip = tip;
            button.FontSize = 13;
            button.Focusable = false;   // キーはいつも描く面で受ける

            // 本体と同じプロセスで開くので、本体のボタンの形（App.xaml の ToolButton）をそのまま使う。
            var style = Application.Current != null ? Application.Current.TryFindResource("ToolButton") as Style : null;
            if (style != null) button.Style = style;
            else button.Foreground = Text;

            return button;
        }

        // =====================================================================
        // 右の欄（選んだ図形の寸法）
        // =====================================================================

        private UIElement CreatePanel()
        {
            _panelTitle.FontSize = 12;
            _panelTitle.FontWeight = FontWeights.SemiBold;
            _panelTitle.Foreground = HeaderText;
            _panelTitle.TextWrapping = TextWrapping.Wrap;
            _panelTitle.Margin = new Thickness(0, 0, 0, 12);

            var note = new TextBlock();
            note.Text = "寸法は mm。打って Enter で入る";
            note.FontSize = 11;
            note.Foreground = Muted;
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 14, 0, 0);

            // ボード（書き出す範囲）の大きさ。いつも出しておく。
            var boardTitle = new TextBlock();
            boardTitle.Text = "ボード（書き出す範囲）";
            boardTitle.FontSize = 12;
            boardTitle.FontWeight = FontWeights.SemiBold;
            boardTitle.Foreground = HeaderText;
            boardTitle.Margin = new Thickness(0, 0, 0, 12);

            _boardWidth = CreateNumberBox(() => _canvas.Board.Width, value => _canvas.SetBoardSize(value, _canvas.Board.Height));
            _boardHeight = CreateNumberBox(() => _canvas.Board.Height, value => _canvas.SetBoardSize(_canvas.Board.Width, value));

            _boardNote.FontSize = 11;
            _boardNote.Foreground = Muted;
            _boardNote.TextWrapping = TextWrapping.Wrap;
            _boardNote.Margin = new Thickness(0, 4, 0, 0);

            var boardSection = new StackPanel();
            boardSection.Children.Add(boardTitle);
            boardSection.Children.Add(FieldRow("幅", _boardWidth));
            boardSection.Children.Add(FieldRow("高さ", _boardHeight));
            boardSection.Children.Add(_boardNote);

            var line = new Border();
            line.Height = 1;
            line.Background = Divider;
            line.Margin = new Thickness(0, 16, 0, 16);

            var stack = new StackPanel();
            stack.Children.Add(_panelTitle);
            stack.Children.Add(_fields);
            stack.Children.Add(note);
            stack.Children.Add(line);
            stack.Children.Add(boardSection);

            var panel = new Border();
            panel.Width = 210;
            panel.Background = Sidebar;
            panel.BorderBrush = Divider;
            panel.BorderThickness = new Thickness(1, 0, 0, 0);
            panel.Padding = new Thickness(16, 16, 16, 16);
            panel.Child = stack;
            DockPanel.SetDock(panel, Dock.Right);
            return panel;
        }

        /// <summary>選んだ図形が変わったら、欄を作り直す。</summary>
        private void BuildFields()
        {
            _panelTitle.Text = _canvas.SelectionTitle;

            // 前の欄は捨てる。外したあとに届く LostKeyboardFocus で、前の図形向けに打った値を、新しく選んだ図形に入れないよう、打った字を「出していた字」にしておく。
            foreach (var old in _fieldBoxes.Values)
            {
                old.Tag = old.Text;
                _commits.Remove(old);
            }

            _fields.Children.Clear();
            _fieldBoxes.Clear();

            foreach (var field in _canvas.Fields())
            {
                var key = field.Key;
                var box = CreateNumberBox(() => FieldValue(key), value => Apply(key, value));
                ShowValue(box, field.Value);
                _fieldBoxes[key] = box;
                _fields.Children.Add(FieldRow(field.Label, box));
            }

            _swatches.Clear();
            if (_canvas.CanFill) _fields.Children.Add(FillRow());
            UpdateSwatches();
        }

        /// <summary>塗りの段（2026-10-20）。色見本を押すと、選んでいる四角・丸をその色で塗る。左端の「なし」は塗らない。</summary>
        private UIElement FillRow()
        {
            var label = new TextBlock();
            label.Text = "塗り";
            label.FontSize = 12;
            label.Foreground = Muted;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Width = 72;
            DockPanel.SetDock(label, Dock.Left);

            var swatches = new WrapPanel();
            foreach (var choice in Fills.Choices)
            {
                var fill = choice.Value;

                var swatch = new Border();
                swatch.Width = 18;
                swatch.Height = 18;
                swatch.Margin = new Thickness(0, 0, 4, 4);
                swatch.BorderThickness = new Thickness(2);
                swatch.CornerRadius = new CornerRadius(2);
                swatch.Background = Fills.BrushOf(fill) ?? Page;
                swatch.ToolTip = choice.Key;
                swatch.Cursor = Cursors.Hand;

                // 「なし」は暗い地に斜めの線。
                if (fill == null)
                {
                    var slash = new System.Windows.Shapes.Line { X1 = 0, Y1 = 14, X2 = 14, Y2 = 0, Stroke = Muted, StrokeThickness = 1 };
                    swatch.Child = slash;
                }

                swatch.MouseLeftButtonUp += delegate
                {
                    _canvas.SetFill(fill);
                    UpdateSwatches();
                    _canvas.Focus();
                };

                _swatches.Add(new KeyValuePair<string, Border>(fill, swatch));
                swatches.Children.Add(swatch);
            }

            var row = new DockPanel();
            row.Margin = new Thickness(0, 0, 0, 8);
            row.Children.Add(label);
            row.Children.Add(swatches);
            return row;
        }

        /// <summary>今の塗りの色見本だけ、枠を明るくする。色がそろっていなければどれも明るくしない。</summary>
        private void UpdateSwatches()
        {
            if (_swatches.Count == 0) return;

            var current = _canvas.SelectionFill;
            foreach (var pair in _swatches)
            {
                var on = current != string.Empty && string.Equals(pair.Key, current, StringComparison.OrdinalIgnoreCase);
                pair.Value.BorderBrush = on ? Text : InputBorder;
            }
        }

        /// <summary>
        /// 数値の欄。打って Enter（か欄の外を押す）で apply に渡す。Esc で戻す。読めない値は戻す。current は今の値（無ければ null）。
        /// 欄に出した字は Tag に覚えておき、字を変えていなければ何もしない（2026-10-27）。
        /// 前は押して離れるだけでも、丸めて出した値（小数2桁）を入れ直していた（元に戻すの履歴が増え、変えた扱いになり、つないだ線の端も外れた）。
        /// Esc は、字を今の値に戻してから描く面へ移る（2026-10-27。前は移るときの LostKeyboardFocus で、打った値が入ってしまった）。
        /// </summary>
        private TextBox CreateNumberBox(Func<double?> current, Action<double> apply)
        {
            var box = new TextBox();
            box.FontSize = 13;
            box.Background = Page;
            box.Foreground = Text;
            box.CaretBrush = Text;
            box.BorderBrush = InputBorder;
            box.Padding = new Thickness(4, 2, 4, 2);
            box.HorizontalContentAlignment = HorizontalAlignment.Right;

            Action revert = delegate
            {
                var value = current();
                if (value.HasValue) ShowValue(box, value.Value);
            };

            Action commit = delegate
            {
                double value;
                if (box.Text != box.Tag as string && TryParse(box.Text, out value)) apply(value);
                revert();   // 入れた値（丸めた・限りに収めた値）を出す。読めなかった・限りを超えたなら元の値に戻る
                UpdateFields();
                UpdateBoard();
            };
            _commits[box] = commit;

            box.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    commit();
                    box.SelectAll();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    revert();
                    _canvas.Focus();
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += delegate { commit(); };
            box.GotKeyboardFocus += delegate { box.SelectAll(); };
            return box;
        }

        /// <summary>欄に値を出し、出した字を覚えておく（字を変えたかを見分けるため。2026-10-27）。</summary>
        private static void ShowValue(TextBox box, double value)
        {
            var text = Format(value);
            box.Text = text;
            box.Tag = text;
        }

        /// <summary>選んでいる図形の、その欄の今の値。欄が無くなっていれば null。</summary>
        private double? FieldValue(string key)
        {
            foreach (var field in _canvas.Fields())
            {
                if (field.Key == key) return field.Value;
            }

            return null;
        }

        /// <summary>
        /// 打ちかけの欄の値を入れる（2026-10-27）。保存・終了の前に呼ぶ。
        /// ボタンはキーを取らない（Focusable = false）ので、Enter を押さずに保存・終了・Ctrl+S すると、欄の外へ移らず、打った値が入らなかった。
        /// 字を変えていない欄は何もしない。
        /// </summary>
        private void CommitFields()
        {
            foreach (var commit in new List<Action>(_commits.Values)) commit();
        }

        private static UIElement FieldRow(string text, TextBox box)
        {
            var label = new TextBlock();
            label.Text = text;
            label.FontSize = 12;
            label.Foreground = Muted;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Width = 72;
            DockPanel.SetDock(label, Dock.Left);

            var row = new DockPanel();
            row.Margin = new Thickness(0, 0, 0, 8);
            row.Children.Add(label);
            row.Children.Add(box);
            return row;
        }

        /// <summary>図形が動いたら、欄の値を合わせる（打っている欄はそのまま）。</summary>
        private void UpdateFields()
        {
            _panelTitle.Text = _canvas.SelectionTitle;

            foreach (var field in _canvas.Fields())
            {
                TextBox box;
                if (!_fieldBoxes.TryGetValue(field.Key, out box) || box.IsKeyboardFocusWithin) continue;
                ShowValue(box, field.Value);
            }

            UpdateSwatches();
        }

        private void Apply(string key, double value)
        {
            // 変わっていなければ入れない（元に戻すの履歴を増やさない）。
            foreach (var field in _canvas.Fields())
            {
                if (field.Key == key && Math.Abs(field.Value - value) < 1e-9) return;
            }

            _canvas.SetField(key, value);
        }

        /// <summary>
        /// 欄の字を数にする。数でない値（NaN・∞。日本語の設定の double.TryParse は "NaN"・"∞" も読んでしまう）と、
        /// 大きすぎる値（Geometry.MaxFieldValue を超える）は読めない扱い（2026-10-27。前はボードが NaN になり、方眼を描く所で固まった）。
        /// </summary>
        private static bool TryParse(string text, out double value)
        {
            text = (text ?? string.Empty).Trim().Replace("mm", string.Empty).Replace("°", string.Empty).Replace("pt", string.Empty);
            var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return parsed && Geometry.IsFinite(value) && Math.Abs(value) <= Geometry.MaxFieldValue;
        }

        private static string Format(double value)
        {
            var text = Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        // =====================================================================
        // 保存・閉じる
        // =====================================================================

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
            CommitFields();   // 右の欄に打ちかけの値も入れてから（2026-10-27）
            _canvas.CommitText();
            if (!MakeResult()) return;

            _saved = true;
            Close();
        }

        /// <summary>
        /// 保存するもの（JSON と画像）を作る。図形が無ければ知らせて作らない。
        /// 作れなければ（画像が大きすぎる・メモリが足りない等）知らせて false。エディタは開いたままにして、描いた図をなくさない（2026-10-27。
        /// 前は例外がそのまま ShowDialog の外まで抜け、エディタが閉じて図が消えた）。
        /// </summary>
        private bool MakeResult()
        {
            if (_canvas.Shapes.Count == 0)
            {
                MessageBox.Show(this, "図形がありません。何か描いてから保存してください。", "図エディタ",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var shapes = new List<Shape>(_canvas.Shapes);
            try
            {
                _result = new DrawingResult
                {
                    Json = new DrawingFile { Shapes = shapes, Board = _canvas.Board, Unit = _canvas.Unit }.Write(),
                    Png = Renderer.RenderPng(shapes, _canvas.Board, _canvas.Unit, VisualTreeHelper.GetDpi(this).DpiScaleX),
                };
            }
            catch (Exception ex)
            {
                _result = null;
                MessageBox.Show(this, "図の画像を作れませんでした。保存していません（図はそのまま残っています）。" + Environment.NewLine
                    + "ボードを小さくするか、大きな画像を減らしてから、もう一度保存してください。" + Environment.NewLine + Environment.NewLine + ex.Message,
                    "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_saved) return;

            CommitFields();   // 右の欄に打ちかけの値も、変えたものに数える（2026-10-27）
            _canvas.CommitText();
            if (Serialize() == _opened) return;   // 何も変えていない

            var answer = MessageBox.Show(this, "変更を保存しますか？", "図エディタ",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                if (!MakeResult())
                {
                    e.Cancel = true;
                    return;
                }

                _saved = true;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>上端の縁も紺にする（本体・表エディタと同じ。Windows 11 で効く）。</summary>
        private void UseDarkTitleBar()
        {
            try
            {
                var window = new WindowInteropHelper(this).Handle;
                if (window == IntPtr.Zero) return;

                var on = 1;
                if (DwmSetWindowAttribute(window, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(window, 19, ref on, sizeof(int));

                var caption = 0x00392319;   // ツールバーの紺 #192339（0x00BBGGRR）
                DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
                var captionText = 0x00E4DFDD;
                DwmSetWindowAttribute(window, 36, ref captionText, sizeof(int));
            }
            catch (Exception)
            {
                // 古い Windows では用意されていない。見た目だけの話なので黙って諦める。
            }
        }

        private static Brush Make(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
