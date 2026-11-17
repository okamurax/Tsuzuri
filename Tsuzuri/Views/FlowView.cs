using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Path = System.Windows.Shapes.Path;

namespace Tsuzuri.Views
{
    /// <summary>
    /// フロー図（2026-11-17。SPEC 21章）。AI の欄のターミナルへ流し込むテキストを、カードとして自由に置いてつなぎ、1枚ずつ送る。
    /// 元はテキストのファイル（`名前.flow.md`。FlowFile）。直したら少し待って書く。
    ///
    /// ・カードは掴んで動かす。右の丸から線を引いて、ほかのカードへつなぐ。何も無いところを掴むと、図全体を動かす。Ctrl+ホイールで拡大・縮小
    /// ・カードを押すと右の欄で直す（種類・題・送るテキスト・つながりと線の名前）
    /// ・「送る」でターミナルに入れて Enter まで押す。「置くだけ」は Enter を押さない。送り先と送り方は持ち主（MainWindow）が決める
    /// ・送ったカードには ✓ を付け、そこからつながる先のカードを「次」として縁取る（何も送っていなければ、どこからもつながっていないカード）。
    ///   ✓ はファイルに書かない（アプリを閉じるまで、ファイルごとに覚える）。「印を消す」で消す
    /// </summary>
    public sealed class FlowView : DockPanel
    {
        private const double CardWidth = 250;
        private const double PortSize = 11;
        private const double CanvasMargin = 400;

        /// <summary>送ったカードの題。ファイルごと（アプリを閉じるまで）。</summary>
        private static readonly Dictionary<string, HashSet<string>> SentByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> LastSentByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Brush CardFill = Freeze(Color.FromRgb(0x18, 0x21, 0x36));
        private static readonly Brush CardBorder = Freeze(Color.FromRgb(0x31, 0x38, 0x4A));
        private static readonly Brush NextBorder = Freeze(Color.FromRgb(0x3F, 0xB6, 0x8B));
        private static readonly Brush DoneFill = Freeze(Color.FromRgb(0x2E, 0x9E, 0x6F));
        private static readonly Brush ClaudeFill = Freeze(Color.FromRgb(0xD9, 0x77, 0x57));
        private static readonly Brush ShellFill = Freeze(Color.FromRgb(0x2F, 0x6F, 0xD0));
        private static readonly Brush EdgeStroke = Freeze(Color.FromRgb(0x5A, 0x63, 0x78));
        private static readonly Brush LabelFill = Freeze(Color.FromRgb(0x22, 0x2B, 0x3F));
        private static readonly Brush SurfaceFill = Freeze(Color.FromRgb(0x10, 0x18, 0x27));
        private static readonly Brush DotFill = Freeze(Color.FromRgb(0x23, 0x2B, 0x3D));

        private readonly string _path;
        private FlowFile _flow;
        private DateTime _stamp;

        private readonly Canvas _canvas = new Canvas();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly TextBlock _status = new TextBlock();
        private readonly Dictionary<FlowCard, CardParts> _cards = new Dictionary<FlowCard, CardParts>();
        private readonly List<UIElement> _edgeElements = new List<UIElement>();

        // 右の欄（選んだカードを直す）
        private readonly Border _panel = new Border();
        private readonly TextBox _titleBox = new TextBox();
        private readonly TextBox _bodyBox = new TextBox();
        private readonly Button _claudeKind = new Button();
        private readonly Button _shellKind = new Button();
        private readonly StackPanel _edgeList = new StackPanel();
        private FlowCard _selected;
        private bool _filling;

        private readonly DispatcherTimer _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private bool _dirty;

        // ドラッグ
        private FlowCard _dragCard;
        private Point _dragStart;
        private double _dragX, _dragY;
        private bool _dragMoved;
        private FlowCard _connectFrom;
        private Path _connectLine;
        private Point _panStart;
        private double _panX, _panY;
        private bool _panning;

        private sealed class CardParts
        {
            public Grid Root;
            public Border Frame;
            public TextBlock Title;
            public TextBlock Preview;
            public Border Icon;
            public TextBlock IconText;
            public TextBlock KindText;
            public Border Done;
        }

        /// <summary>カードを送る（enter: Enter まで押す）。送れたら持ち主が MarkSent を呼ぶ。</summary>
        public event Action<FlowCard, bool> SendRequested;

        /// <summary>× か Esc で閉じる。</summary>
        public event Action CloseRequested;

        public FlowView(string path)
        {
            _path = path;
            Background = SurfaceFill;
            Focusable = true;
            FocusVisualStyle = null;

            _saveDelay.Tick += delegate
            {
                _saveDelay.Stop();
                Save();
            };

            Children.Add(BuildBar());
            Children.Add(BuildPanel());

            var dots = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 20, 20),
                ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new GeometryDrawing(DotFill, null, new EllipseGeometry(new Point(1, 1), 1, 1)),
            };
            dots.Freeze();
            _canvas.Background = dots;
            _canvas.LayoutTransform = _scale;
            _canvas.ClipToBounds = true;

            _scroll.Content = _canvas;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.Focusable = false;
            Children.Add(_scroll);

            _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            _canvas.LostMouseCapture += delegate { EndDrag(); };
            _scroll.PreviewMouseWheel += Scroll_PreviewMouseWheel;
            PreviewKeyDown += View_PreviewKeyDown;

            Load();
        }

        public string FilePath
        {
            get { return _path; }
        }

        // ---------------------------------------------------------------
        // 読み書き
        // ---------------------------------------------------------------

        private void Load()
        {
            string text;
            try
            {
                text = File.Exists(_path) ? File.ReadAllText(_path, Encoding.UTF8) : string.Empty;
                _stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            }
            catch (Exception ex)
            {
                text = string.Empty;
                ShowStatus("読めませんでした: " + ex.Message);
            }

            var selected = _selected != null ? _selected.Title : null;
            _selected = null;   // 前のフローのカード（題を確かめ直さない）
            _flow = FlowFile.Parse(text);
            _dirty = false;
            PlaceUnplaced();
            Rebuild();
            Select(selected != null ? _flow.Find(selected) : null);
        }

        /// <summary>ファイルがほかで（Claude など）書き換わっていたら読み直す。直しかけ（書く前）なら、書くときに尋ねる。</summary>
        public void ReloadIfChanged()
        {
            DateTime stamp;
            try
            {
                if (!File.Exists(_path)) return;
                stamp = File.GetLastWriteTimeUtc(_path);
            }
            catch (Exception)
            {
                return;
            }
            if (stamp == _stamp || _dirty) return;

            Load();
            ShowStatus("ほかで書き換わったので読み直しました");
        }

        /// <summary>直しかけがあれば、すぐ書く（閉じるとき）。</summary>
        public void Flush()
        {
            _saveDelay.Stop();
            if (_dirty) Save();
        }

        private void Changed()
        {
            _dirty = true;
            _saveDelay.Stop();
            _saveDelay.Start();
        }

        private void Save()
        {
            if (!_dirty) return;

            try
            {
                // ほかで書き換わっていたら尋ねる（黙って上書きすると、Claude の直しが消える）
                if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _stamp)
                {
                    var answer = MessageBox.Show(Window.GetWindow(this),
                        "フローのファイルが、ほかで（AI など）書き換わっています。" + Environment.NewLine + Environment.NewLine +
                        "はい: ここで直した内容で上書きする（ほかでの書き換えは消える）" + Environment.NewLine +
                        "いいえ: ここで直した分を捨て、ファイルを読み直す" + Environment.NewLine + Environment.NewLine + _path,
                        "フロー", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (answer != MessageBoxResult.Yes)
                    {
                        _dirty = false;
                        Load();
                        return;
                    }
                }

                var data = new UTF8Encoding(false).GetBytes(_flow.Serialize());
                PageDocument.WriteAtomically(_path, data);
                _stamp = File.GetLastWriteTimeUtc(_path);
                _dirty = false;
            }
            catch (Exception ex)
            {
                ShowStatus("書けませんでした（直した内容は画面に残っています）: " + ex.Message);
                _saveDelay.Interval = TimeSpan.FromSeconds(5);   // 少し待ってまた試す
                _saveDelay.Start();
                return;
            }
            _saveDelay.Interval = TimeSpan.FromMilliseconds(600);
        }

        public void ShowStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        // ---------------------------------------------------------------
        // 上の段と右の欄
        // ---------------------------------------------------------------

        private UIElement BuildBar()
        {
            var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

            var close = ToolButton("", "閉じる（Esc）");
            close.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            close.FontSize = 10;
            close.Click += delegate { if (CloseRequested != null) CloseRequested(); };
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);

            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(_path),
                Foreground = Resource("TextPrimary"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
            };
            DockPanel.SetDock(name, Dock.Left);
            bar.Children.Add(name);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var addClaude = ToolButton("＋ Claude", "Claude に送るカードを足す");
            addClaude.Click += delegate { AddCard(FlowKind.Claude); };
            var addShell = ToolButton("＋ PowerShell", "共通ターミナル（PowerShell）に送るカードを足す");
            addShell.Click += delegate { AddCard(FlowKind.PowerShell); };
            var clear = ToolButton("印を消す", "送ったカードの ✓ を全部消す");
            clear.Click += delegate { ClearMarks(); };
            var reload = ToolButton("読み直す", "ファイルから読み直す");
            reload.Click += delegate
            {
                Flush();
                Load();
                ShowStatus("読み直しました");
            };
            buttons.Children.Add(addClaude);
            buttons.Children.Add(addShell);
            buttons.Children.Add(Separator());
            buttons.Children.Add(clear);
            buttons.Children.Add(reload);
            DockPanel.SetDock(buttons, Dock.Left);
            bar.Children.Add(buttons);

            _status.Foreground = Resource("TextMuted");
            _status.FontSize = 12;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.Margin = new Thickness(12, 0, 8, 0);
            bar.Children.Add(_status);

            var border = new Border
            {
                Height = 34,
                Background = Resource("SidebarBackground"),
                BorderBrush = Resource("BorderBrushSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = bar,
            };
            DockPanel.SetDock(border, Dock.Top);
            return border;
        }

        private UIElement BuildPanel()
        {
            var stack = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 10, 14, 12) };

            // 下: 送る・置くだけ・消す
            var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
            var send = new Button { Content = "送る（Enter まで）", Style = (Style)Application.Current.FindResource("AccentButton"), ToolTip = "ターミナルに入れて Enter を押す" };
            send.Click += delegate { RequestSend(_selected, true); };
            var place = ToolButton("置くだけ", "ターミナルに入れるだけ（Enter は押さない）");
            place.Click += delegate { RequestSend(_selected, false); };
            var remove = ToolButton("カードを消す", "このカードと、このカードへのつながりを消す");
            remove.Click += delegate { RemoveCard(_selected); };
            actions.Children.Add(send);
            actions.Children.Add(place);
            DockPanel.SetDock(remove, Dock.Right);
            actions.Children.Add(remove);
            DockPanel.SetDock(actions, Dock.Bottom);
            stack.Children.Add(actions);

            // 上: 種類・題
            var top = new StackPanel();
            var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            SetupKind(_claudeKind, "Claude", FlowKind.Claude);
            SetupKind(_shellKind, "PowerShell", FlowKind.PowerShell);
            kinds.Children.Add(_claudeKind);
            kinds.Children.Add(_shellKind);
            top.Children.Add(kinds);

            top.Children.Add(Caption("題"));
            StyleBox(_titleBox);
            _titleBox.LostKeyboardFocus += delegate { CommitTitle(); };
            _titleBox.KeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                CommitTitle();
            };
            top.Children.Add(_titleBox);
            top.Children.Add(Caption("送るテキスト"));
            DockPanel.SetDock(top, Dock.Top);
            stack.Children.Add(top);

            // つながり（本文の下）
            var edges = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            edges.Children.Add(Caption("つながり（右の丸から線を引いて足す。名前は分かれ道の目印）"));
            edges.Children.Add(_edgeList);
            DockPanel.SetDock(edges, Dock.Bottom);
            stack.Children.Add(edges);

            StyleBox(_bodyBox);
            _bodyBox.AcceptsReturn = true;
            _bodyBox.AcceptsTab = true;
            _bodyBox.TextWrapping = TextWrapping.Wrap;
            _bodyBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _bodyBox.VerticalContentAlignment = VerticalAlignment.Top;
            _bodyBox.FontFamily = Theme.CodeFont;
            _bodyBox.TextChanged += delegate
            {
                if (_filling || _selected == null) return;
                _selected.Body = _bodyBox.Text.Replace("\r\n", "\n");
                UpdateCard(_selected);
                Changed();
            };
            stack.Children.Add(_bodyBox);

            _panel.Width = 380;
            _panel.Background = Resource("SidebarBackground");
            _panel.BorderBrush = Resource("BorderBrushSoft");
            _panel.BorderThickness = new Thickness(1, 0, 0, 0);
            _panel.Child = stack;
            _panel.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_panel, Dock.Right);
            return _panel;
        }

        private void SetupKind(Button button, string text, FlowKind kind)
        {
            button.Content = text;
            button.Style = (Style)Application.Current.FindResource("ToolButton");
            button.Margin = new Thickness(0, 0, 4, 0);
            button.Padding = new Thickness(10, 3, 10, 3);
            button.BorderThickness = new Thickness(1);
            button.Click += delegate
            {
                if (_filling || _selected == null) return;
                _selected.Kind = kind;
                FillPanel();
                UpdateCard(_selected);
                Changed();
            };
        }

        private void CommitTitle()
        {
            if (_filling || _selected == null) return;
            var before = _selected.Title;
            _flow.Rename(_selected, _titleBox.Text);
            if (_selected.Title == before)
            {
                if (_titleBox.Text != before) FillPanel();   // 空など、使えない題は戻す
                return;
            }
            FillPanel();
            UpdateCard(_selected);
            RedrawEdges();
            Changed();
        }

        private void FillPanel()
        {
            _filling = true;
            try
            {
                _panel.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
                if (_selected == null) return;

                _titleBox.Text = _selected.Title;
                if (_bodyBox.Text.Replace("\r\n", "\n") != _selected.Body) _bodyBox.Text = _selected.Body;
                _claudeKind.BorderBrush = _selected.Kind == FlowKind.Claude ? Resource("AccentBrush") : Brushes.Transparent;
                _shellKind.BorderBrush = _selected.Kind == FlowKind.PowerShell ? Resource("AccentBrush") : Brushes.Transparent;

                _edgeList.Children.Clear();
                if (_selected.Edges.Count == 0)
                {
                    _edgeList.Children.Add(new TextBlock { Text = "（なし）", Foreground = Resource("TextMuted"), FontSize = 12, Margin = new Thickness(2, 2, 0, 2) });
                }
                foreach (var edge in _selected.Edges.ToList())
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var delete = ToolButton("×", "このつながりを消す");
                    delete.Padding = new Thickness(6, 0, 6, 0);
                    var owner = _selected;
                    var target = edge;
                    delete.Click += delegate
                    {
                        owner.Edges.Remove(target);
                        FillPanel();
                        RedrawEdges();
                        RefreshMarks();
                        Changed();
                    };
                    DockPanel.SetDock(delete, Dock.Right);
                    row.Children.Add(delete);

                    var label = new TextBox { Width = 90, Text = edge.Label, ToolTip = "線の名前（分かれ道の目印。無くてもよい）" };
                    StyleBox(label);
                    label.Margin = new Thickness(6, 0, 4, 0);
                    label.TextChanged += delegate
                    {
                        var clean = FlowFile.CleanTitle(label.Text).Replace("]", string.Empty);
                        if (target.Label == clean) return;
                        target.Label = clean;
                        RedrawEdges();
                        Changed();
                    };
                    DockPanel.SetDock(label, Dock.Right);
                    row.Children.Add(label);

                    row.Children.Add(new TextBlock
                    {
                        Text = "→ " + edge.Target + (_flow.Find(edge.Target) == null ? "（無いカード）" : string.Empty),
                        Foreground = Resource("TextPrimary"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    _edgeList.Children.Add(row);
                }
            }
            finally
            {
                _filling = false;
            }
        }

        // ---------------------------------------------------------------
        // カード
        // ---------------------------------------------------------------

        /// <summary>位置の無いカードを、ほかと重ならないところに並べる。左上を (40, 40) 以上にする。</summary>
        private void PlaceUnplaced()
        {
            var placed = _flow.Cards.Where(card => card.HasPosition).ToList();
            if (placed.Count > 0)
            {
                var minX = placed.Min(card => card.X);
                var minY = placed.Min(card => card.Y);
                var dx = minX < 40 ? 40 - minX : 0;
                var dy = minY < 40 ? 40 - minY : 0;
                foreach (var card in placed)
                {
                    card.X += dx;
                    card.Y += dy;
                }
            }

            var next = 0;
            foreach (var card in _flow.Cards.Where(card => !card.HasPosition))
            {
                Point point;
                do
                {
                    point = new Point(40 + next % 4 * (CardWidth + 70), 40 + next / 4 * 190);
                    next++;
                } while (placed.Any(other => Math.Abs(other.X - point.X) < CardWidth && Math.Abs(other.Y - point.Y) < 150));

                card.X = point.X;
                card.Y = point.Y;
                placed.Add(card);
            }
        }

        private void Rebuild()
        {
            _canvas.Children.Clear();
            _cards.Clear();
            _edgeElements.Clear();

            foreach (var card in _flow.Cards) AddCardElement(card);
            RefreshMarks();
            UpdateExtent();

            // 大きさが決まってから線を引く
            Dispatcher.BeginInvoke(new Action(RedrawEdges), DispatcherPriority.Loaded);
        }

        private void AddCardElement(FlowCard card)
        {
            var parts = new CardParts();

            parts.Icon = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0) };
            parts.IconText = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
            };
            parts.Icon.Child = parts.IconText;
            DockPanel.SetDock(parts.Icon, Dock.Left);

            parts.Done = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = DoneFill,
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = "送った",
            };
            DockPanel.SetDock(parts.Done, Dock.Right);

            parts.Title = new TextBlock
            {
                Foreground = Resource("TextPrimary"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var header = new DockPanel { LastChildFill = true };
            header.Children.Add(parts.Icon);
            header.Children.Add(parts.Done);
            header.Children.Add(parts.Title);
            DockPanel.SetDock(header, Dock.Top);

            parts.Preview = new TextBlock
            {
                Foreground = Resource("TextMuted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 52,
                Margin = new Thickness(32, 6, 0, 0),
            };
            DockPanel.SetDock(parts.Preview, Dock.Top);

            parts.KindText = new TextBlock { Foreground = Resource("TextMuted"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            var kindTag = new Border
            {
                BorderBrush = Resource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = parts.KindText,
            };
            var send = ToolButton("▶ 送る", "ターミナルに入れて Enter を押す（右クリック: 置くだけ）");
            send.FontSize = 12;
            send.Padding = new Thickness(8, 1, 8, 1);
            send.Click += delegate { RequestSend(card, true); };
            send.MouseRightButtonUp += (sender, e) =>
            {
                e.Handled = true;
                RequestSend(card, false);
            };
            DockPanel.SetDock(send, Dock.Right);
            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(32, 8, 0, 0) };
            footer.Children.Add(send);
            footer.Children.Add(kindTag);
            DockPanel.SetDock(footer, Dock.Bottom);

            var content = new DockPanel { LastChildFill = false };
            content.Children.Add(header);
            content.Children.Add(footer);
            content.Children.Add(parts.Preview);

            parts.Frame = new Border
            {
                Width = CardWidth,
                CornerRadius = new CornerRadius(10),
                Background = CardFill,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 10, 8),
                Margin = new Thickness(PortSize / 2, 0, PortSize / 2, 0),
                Child = content,
                Cursor = Cursors.SizeAll,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
            };

            var inPort = new Ellipse { Width = PortSize, Height = PortSize, Fill = CardFill, Stroke = EdgeStroke, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var outPort = new Ellipse
            {
                Width = PortSize,
                Height = PortSize,
                Fill = CardFill,
                Stroke = EdgeStroke,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Cross,
                ToolTip = "ドラッグして、つなぐカードの上で離す",
            };
            outPort.MouseEnter += delegate { outPort.Fill = Resource("AccentBrush"); };
            outPort.MouseLeave += delegate { outPort.Fill = CardFill; };
            outPort.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                StartConnect(card, e);
            };

            parts.Root = new Grid { Tag = card };
            parts.Root.Children.Add(parts.Frame);
            parts.Root.Children.Add(inPort);
            parts.Root.Children.Add(outPort);
            Panel.SetZIndex(parts.Root, 10);

            parts.Frame.MouseLeftButtonDown += (sender, e) =>
            {
                if (IsInButton(e.OriginalSource as DependencyObject)) return;
                e.Handled = true;
                Select(card);
                Focus();   // 題の欄などからフォーカスを外す（直した題を確かめる。Delete・Esc をここで受ける）
                if (e.ClickCount == 2)
                {
                    _bodyBox.Focus();
                    return;
                }
                StartDrag(card, e);
            };
            parts.Frame.ContextMenu = CardMenu(card);
            parts.Root.SizeChanged += delegate { RedrawEdges(); };

            Canvas.SetLeft(parts.Root, card.X - PortSize / 2);
            Canvas.SetTop(parts.Root, card.Y);
            _canvas.Children.Add(parts.Root);
            _cards[card] = parts;
            UpdateCard(card);
        }

        private ContextMenu CardMenu(FlowCard card)
        {
            var menu = new ContextMenu();
            var send = new MenuItem { Header = "送る（Enter まで）" };
            send.Click += delegate { RequestSend(card, true); };
            var place = new MenuItem { Header = "置くだけ（Enter は押さない）" };
            place.Click += delegate { RequestSend(card, false); };
            var unmark = new MenuItem { Header = "✓ を外す" };
            unmark.Click += delegate
            {
                var sent = SentSet();
                sent.Remove(card.Title);
                RefreshMarks();
            };
            var copy = new MenuItem { Header = "テキストをコピー" };
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(card.Body);
                }
                catch (Exception)
                {
                }
            };
            var remove = new MenuItem { Header = "カードを消す" };
            remove.Click += delegate { RemoveCard(card); };
            menu.Items.Add(send);
            menu.Items.Add(place);
            menu.Items.Add(unmark);
            menu.Items.Add(copy);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
            menu.Opened += delegate { Select(card); };
            return menu;
        }

        private void UpdateCard(FlowCard card)
        {
            CardParts parts;
            if (!_cards.TryGetValue(card, out parts)) return;

            parts.Title.Text = card.Title;
            var lines = card.Body.Split('\n').Where(line => line.Trim().Length > 0).Take(3).ToList();
            parts.Preview.Text = lines.Count == 0 ? "（送るテキストが無い）" : string.Join("\n", lines);
            parts.Preview.FontFamily = card.Kind == FlowKind.PowerShell ? Theme.CodeFont : Theme.BodyFont;
            parts.Icon.Background = card.Kind == FlowKind.PowerShell ? ShellFill : ClaudeFill;
            parts.IconText.Text = card.Kind == FlowKind.PowerShell ? ">_" : "AI";
            parts.IconText.FontSize = card.Kind == FlowKind.PowerShell ? 11 : 10;
            parts.KindText.Text = card.Kind == FlowKind.PowerShell ? "POWERSHELL" : "CLAUDE";
        }

        private void AddCard(FlowKind kind)
        {
            var card = new FlowCard
            {
                Kind = kind,
                Title = _flow.UniqueTitle(kind == FlowKind.PowerShell ? "コマンド" : "頼む", null),
                HasPosition = true,
            };

            // 選んでいるカードの右（無ければ見えているところの左上）に置く。重なれば下へずらす
            Point point;
            if (_selected != null) point = new Point(_selected.X + CardWidth + 80, _selected.Y);
            else point = new Point(_scroll.HorizontalOffset / _scale.ScaleX + 40, _scroll.VerticalOffset / _scale.ScaleY + 40);
            while (_flow.Cards.Any(other => Math.Abs(other.X - point.X) < CardWidth && Math.Abs(other.Y - point.Y) < 120)) point.Y += 140;
            card.X = Math.Round(point.X);
            card.Y = Math.Round(point.Y);

            // 選んでいたカードからつなぐ
            if (_selected != null) _selected.Edges.Add(new FlowEdge { Target = card.Title });

            _flow.Cards.Add(card);
            AddCardElement(card);
            UpdateExtent();
            Select(card);
            RefreshMarks();
            Dispatcher.BeginInvoke(new Action(RedrawEdges), DispatcherPriority.Loaded);
            Changed();

            _titleBox.Focus();
            _titleBox.SelectAll();
        }

        private void RemoveCard(FlowCard card)
        {
            if (card == null) return;
            if (card.Body.Trim().Length > 0)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), "カード「" + card.Title + "」を消しますか。", "フロー",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            _flow.Remove(card);
            CardParts parts;
            if (_cards.TryGetValue(card, out parts)) _canvas.Children.Remove(parts.Root);
            _cards.Remove(card);
            if (ReferenceEquals(_selected, card)) Select(null);
            else FillPanel();
            RedrawEdges();
            RefreshMarks();
            Changed();
        }

        private void Select(FlowCard card)
        {
            if (_selected != null && !ReferenceEquals(_selected, card)) CommitTitle();
            _selected = card != null && _cards.ContainsKey(card) ? card : null;
            FillPanel();
            RefreshMarks();
        }

        // ---------------------------------------------------------------
        // 送る・印
        // ---------------------------------------------------------------

        private void RequestSend(FlowCard card, bool enter)
        {
            if (card == null) return;
            if (card.Body.Trim().Length == 0)
            {
                ShowStatus("送るテキストがありません: " + card.Title);
                return;
            }
            if (SendRequested != null) SendRequested(card, enter);
        }

        /// <summary>送れた（持ち主が呼ぶ）。✓ を付け、そこからつながる先を「次」にする。</summary>
        public void MarkSent(FlowCard card)
        {
            SentSet().Add(card.Title);
            LastSentByFile[_path] = card.Title;
            RefreshMarks();
        }

        private HashSet<string> SentSet()
        {
            HashSet<string> sent;
            if (!SentByFile.TryGetValue(_path, out sent))
            {
                sent = new HashSet<string>();
                SentByFile[_path] = sent;
            }
            return sent;
        }

        private void ClearMarks()
        {
            SentSet().Clear();
            LastSentByFile.Remove(_path);
            RefreshMarks();
            ShowStatus("印を消しました");
        }

        /// <summary>✓ と「次」と選んでいる縁を付け直す。</summary>
        private void RefreshMarks()
        {
            var sent = SentSet();
            string last;
            LastSentByFile.TryGetValue(_path, out last);
            var lastCard = last != null ? _flow.Find(last) : null;

            HashSet<string> next;
            if (lastCard != null) next = new HashSet<string>(lastCard.Edges.Select(edge => edge.Target));
            else
            {
                var targets = new HashSet<string>(_flow.Cards.SelectMany(card => card.Edges).Select(edge => edge.Target));
                next = new HashSet<string>(_flow.Cards.Where(card => !targets.Contains(card.Title)).Select(card => card.Title));
            }

            foreach (var pair in _cards)
            {
                var card = pair.Key;
                var parts = pair.Value;
                parts.Done.Visibility = sent.Contains(card.Title) ? Visibility.Visible : Visibility.Collapsed;

                if (ReferenceEquals(card, _selected))
                {
                    parts.Frame.BorderBrush = Resource("AccentBrush");
                    parts.Frame.BorderThickness = new Thickness(2);
                }
                else if (next.Contains(card.Title))
                {
                    parts.Frame.BorderBrush = NextBorder;
                    parts.Frame.BorderThickness = new Thickness(2);
                }
                else
                {
                    parts.Frame.BorderBrush = CardBorder;
                    parts.Frame.BorderThickness = new Thickness(1);
                }
                parts.Frame.ToolTip = next.Contains(card.Title) && !ReferenceEquals(card, _selected) ? "次に送るカード" : null;
            }
        }

        // ---------------------------------------------------------------
        // 線
        // ---------------------------------------------------------------

        private void RedrawEdges()
        {
            foreach (var element in _edgeElements) _canvas.Children.Remove(element);
            _edgeElements.Clear();

            foreach (var card in _flow.Cards)
            {
                CardParts from;
                if (!_cards.TryGetValue(card, out from)) continue;

                foreach (var edge in card.Edges)
                {
                    var target = _flow.Find(edge.Target);
                    CardParts to;
                    if (target == null || !_cards.TryGetValue(target, out to)) continue;

                    var start = new Point(card.X + CardWidth + PortSize / 2, card.Y + from.Root.ActualHeight / 2);
                    var end = new Point(target.X - PortSize / 2, target.Y + to.Root.ActualHeight / 2);
                    AddEdge(card, edge, start, end);
                }
            }
        }

        private void AddEdge(FlowCard owner, FlowEdge edge, Point start, Point end)
        {
            var bend = Math.Max(50, Math.Abs(end.X - start.X) / 2);
            var c1 = new Point(start.X + bend, start.Y);
            var c2 = new Point(end.X - bend, end.Y);

            var figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new BezierSegment(c1, c2, end, true));
            var geometry = new PathGeometry(new[] { figure });

            var line = new Path { Data = geometry, Stroke = EdgeStroke, StrokeThickness = 1.6 };
            // 掴みやすいよう、見えない太い線を重ねる（右クリックで消す・名前）
            var hit = new Path { Data = geometry, Stroke = Brushes.Transparent, StrokeThickness = 10, Cursor = Cursors.Hand };
            hit.MouseEnter += delegate { line.Stroke = Resource("AccentBrush"); };
            hit.MouseLeave += delegate { line.Stroke = EdgeStroke; };
            hit.ContextMenu = EdgeMenu(owner, edge);
            hit.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                Select(owner);
            };

            // 先の矢じり
            var arrow = new Path
            {
                Data = Geometry.Parse(string.Format(System.Globalization.CultureInfo.InvariantCulture, "M {0},{1} L {2},{3} L {0},{4} Z",
                    end.X - 8, end.Y - 4.5, end.X, end.Y, end.Y + 4.5)),
                Fill = EdgeStroke,
            };

            foreach (var element in new UIElement[] { line, arrow, hit })
            {
                Panel.SetZIndex(element, 1);
                _canvas.Children.Add(element);
                _edgeElements.Add(element);
            }

            if (edge.Label.Length == 0) return;

            // 名前は線の真ん中（t=0.5）に
            var middle = new Point((start.X + 3 * c1.X + 3 * c2.X + end.X) / 8, (start.Y + 3 * c1.Y + 3 * c2.Y + end.Y) / 8);
            var label = new Border
            {
                Background = LabelFill,
                BorderBrush = EdgeStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                Child = new TextBlock { Text = edge.Label, Foreground = Resource("TextPrimary"), FontSize = 11 },
                ContextMenu = EdgeMenu(owner, edge),
            };
            label.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                Select(owner);
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, middle.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, middle.Y - label.DesiredSize.Height / 2);
            Panel.SetZIndex(label, 5);
            _canvas.Children.Add(label);
            _edgeElements.Add(label);
        }

        private ContextMenu EdgeMenu(FlowCard owner, FlowEdge edge)
        {
            var menu = new ContextMenu();
            var name = new MenuItem { Header = "名前を付ける・変える…" };
            name.Click += delegate { Select(owner); };
            var remove = new MenuItem { Header = "つながりを消す" };
            remove.Click += delegate
            {
                owner.Edges.Remove(edge);
                FillPanel();
                RedrawEdges();
                RefreshMarks();
                Changed();
            };
            menu.Items.Add(name);
            menu.Items.Add(remove);
            return menu;
        }

        private void UpdateExtent()
        {
            var right = _flow.Cards.Count == 0 ? 0 : _flow.Cards.Max(card => card.X + CardWidth);
            var bottom = _flow.Cards.Count == 0 ? 0 : _flow.Cards.Max(card => card.Y + 160);
            _canvas.Width = Math.Max(right + CanvasMargin, 1200);
            _canvas.Height = Math.Max(bottom + CanvasMargin, 800);
        }

        // ---------------------------------------------------------------
        // マウス
        // ---------------------------------------------------------------

        private void StartDrag(FlowCard card, MouseButtonEventArgs e)
        {
            _dragCard = card;
            _dragStart = e.GetPosition(_canvas);
            _dragX = card.X;
            _dragY = card.Y;
            _dragMoved = false;
            _canvas.CaptureMouse();
        }

        private void StartConnect(FlowCard card, MouseButtonEventArgs e)
        {
            Select(card);
            _connectFrom = card;
            _connectLine = new Path { Stroke = Resource("AccentBrush"), StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
            Panel.SetZIndex(_connectLine, 20);
            _canvas.Children.Add(_connectLine);
            UpdateConnectLine(e.GetPosition(_canvas));
            _canvas.CaptureMouse();
        }

        private void UpdateConnectLine(Point to)
        {
            CardParts parts;
            if (_connectFrom == null || _connectLine == null || !_cards.TryGetValue(_connectFrom, out parts)) return;
            var start = new Point(_connectFrom.X + CardWidth + PortSize / 2, _connectFrom.Y + parts.Root.ActualHeight / 2);
            var bend = Math.Max(40, Math.Abs(to.X - start.X) / 2);
            var figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(to.X - bend, to.Y), to, true));
            _connectLine.Data = new PathGeometry(new[] { figure });
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 何も無いところ: 選びを外し、図全体を動かす
            Select(null);
            Focus();
            _panning = true;
            _panStart = e.GetPosition(_scroll);
            _panX = _scroll.HorizontalOffset;
            _panY = _scroll.VerticalOffset;
            _canvas.Cursor = Cursors.ScrollAll;
            _canvas.CaptureMouse();
            e.Handled = true;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_panning)
            {
                var point = e.GetPosition(_scroll);
                _scroll.ScrollToHorizontalOffset(_panX - (point.X - _panStart.X));
                _scroll.ScrollToVerticalOffset(_panY - (point.Y - _panStart.Y));
                return;
            }

            if (_connectFrom != null)
            {
                UpdateConnectLine(e.GetPosition(_canvas));
                return;
            }

            if (_dragCard == null) return;

            var now = e.GetPosition(_canvas);
            var dx = now.X - _dragStart.X;
            var dy = now.Y - _dragStart.Y;
            if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
            _dragMoved = true;

            // 10 ずつに吸い付ける（並べやすく）
            _dragCard.X = Math.Max(10, Math.Round((_dragX + dx) / 10) * 10);
            _dragCard.Y = Math.Max(10, Math.Round((_dragY + dy) / 10) * 10);
            _dragCard.HasPosition = true;
            var parts = _cards[_dragCard];
            Canvas.SetLeft(parts.Root, _dragCard.X - PortSize / 2);
            Canvas.SetTop(parts.Root, _dragCard.Y);
            RedrawEdges();
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_connectFrom != null)
            {
                var from = _connectFrom;
                var target = CardAt(e.GetPosition(_canvas));
                EndDrag();
                if (target != null && !ReferenceEquals(target, from) && from.Edges.All(edge => edge.Target != target.Title))
                {
                    from.Edges.Add(new FlowEdge { Target = target.Title });
                    FillPanel();
                    RedrawEdges();
                    RefreshMarks();
                    Changed();
                }
                return;
            }

            var moved = _dragCard != null && _dragMoved;
            EndDrag();
            if (moved)
            {
                UpdateExtent();
                Changed();
            }
        }

        private void EndDrag()
        {
            _dragCard = null;
            _panning = false;
            _canvas.Cursor = null;
            if (_connectLine != null) _canvas.Children.Remove(_connectLine);
            _connectLine = null;
            _connectFrom = null;
            if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        }

        private FlowCard CardAt(Point point)
        {
            foreach (var pair in _cards)
            {
                var left = pair.Key.X - PortSize;
                var top = pair.Key.Y;
                if (point.X >= left && point.X <= left + CardWidth + PortSize * 2 && point.Y >= top && point.Y <= top + pair.Value.Root.ActualHeight)
                    return pair.Key;
            }
            return null;
        }

        private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            var scale = Math.Max(0.4, Math.Min(1.6, Math.Round((_scale.ScaleX + (e.Delta > 0 ? 0.1 : -0.1)) * 10) / 10));
            _scale.ScaleX = scale;
            _scale.ScaleY = scale;
            ShowStatus("拡大 " + Math.Round(scale * 100) + "%");
        }

        private void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;   // 打っている欄のキーは欄に任せる

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (CloseRequested != null) CloseRequested();
            }
            else if (e.Key == Key.Delete && _selected != null)
            {
                e.Handled = true;
                RemoveCard(_selected);
            }
        }

        private static bool IsInButton(DependencyObject node)
        {
            for (; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is ButtonBase) return true;
                if (node is Grid && ((Grid)node).Tag is FlowCard) return false;   // カードの外まで来た
            }
            return false;
        }

        // ---------------------------------------------------------------
        // 部品
        // ---------------------------------------------------------------

        private static Button ToolButton(string text, string tip)
        {
            return new Button
            {
                Content = text,
                Style = (Style)Application.Current.FindResource("ToolButton"),
                ToolTip = tip,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static UIElement Separator()
        {
            return new Border { Width = 1, Height = 16, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = Resource("BorderBrushSoft") };
        }

        private static TextBlock Caption(string text)
        {
            return new TextBlock { Text = text, Foreground = Resource("TextMuted"), FontSize = 12, Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };
        }

        private static void StyleBox(TextBox box)
        {
            box.Background = Resource("PageBackground");
            box.Foreground = Resource("TextPrimary");
            box.CaretBrush = Resource("TextPrimary");
            box.BorderBrush = Resource("InputBorder");
            box.SelectionBrush = Resource("AccentBrush");
            box.Padding = new Thickness(4, 3, 4, 3);
            box.FontSize = 13;
        }

        private static Brush Resource(string key)
        {
            return (Brush)Application.Current.FindResource(key);
        }

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
