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
    /// フロー図（2026-11-17。SPEC 21章）。AI の欄の Claude へ流し込むテキストを、カードとして自由に置いてつなぎ、最初のカードから順に送る。
    /// 元は文書のサブページ（FlowFile。図エディタの図と同じく今の文書の中）。直したら少し待ってページに書く（文書は未保存になり、ふつうの保存で書く）。
    /// 本文はカードの中で直接打つ（右の欄は無くした）。種類はカードの右クリック、線の名前は線の右クリックで、その場で変える。
    /// ファイルは添付のカードが持つ（取り込みは持ち主。本文へのドロップと同じ）。
    ///
    /// 操作は図エディタ（SPEC 8.4.3）にそろえる（2026-11-17）:
    /// ・押して選ぶ。Shift・Ctrl で足す・外す。何も無い所からドラッグで、範囲に収まるカードを選ぶ。Ctrl+A ですべて
    /// ・選んだカードをドラッグでまとめて動かす（Shift で縦か横だけ）。矢印キーで 10（Shift で 50、Ctrl で 1）
    /// ・Delete で消す。Ctrl+C・X・V（貼るたびに少しずらす）、Ctrl+D で複製、Ctrl+Z・Y（200 回まで）。Esc でつかむのをやめる・選ぶのをやめる
    /// ・ホイールで拡大・縮小（カーソルの所を中心に）。真ん中のボタンのドラッグで動かす。Shift+ホイールで左右、Ctrl+ホイールで上下
    /// ・ダブルクリック・F2 で本文を打つ（本文の欄を押しても打てる）
    /// フロー図だけのもの: カードの右の丸から線を引いてつなぐ。「▶ 送る」は最初のカード（どこからもつながっていない Claude のカード）だけに出し、
    /// 続くカードは持ち主の MainWindow が Claude の答えが終わるたびに順に送る。
    /// 送ったカードには ✓ を付け、そこからつながる先のカードを「次」として縁取る。✓ はファイルに書かない（アプリを閉じるまで、ファイルごとに覚える）
    /// </summary>
    public sealed class FlowView : DockPanel
    {
        private const double CardWidth = 250;
        private const double PortSize = 11;
        private const double CanvasMargin = 600;
        private const int MaxUndo = 200;
        private const double PasteStep = 20;

        /// <summary>カードをコピーしたときに Windows のクリップボードに置く印（中身はフローのテキスト）。</summary>
        private const string ClipboardFormat = "Tsuzuri.FlowCards";

        /// <summary>送ったカードの題。ファイルごと（アプリを閉じるまで）。</summary>
        private static readonly Dictionary<string, HashSet<string>> SentByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> LastSentByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Brush CardFill = Freeze(Color.FromRgb(0x18, 0x21, 0x36));
        private static readonly Brush CardBorder = Freeze(Color.FromRgb(0x31, 0x38, 0x4A));
        private static readonly Brush NextBorder = Freeze(Color.FromRgb(0x3F, 0xB6, 0x8B));
        private static readonly Brush DoneFill = Freeze(Color.FromRgb(0x2E, 0x9E, 0x6F));
        private static readonly Brush ClaudeFill = Freeze(Color.FromRgb(0xD9, 0x77, 0x57));
        private static readonly Brush FilesFill = Freeze(Color.FromRgb(0x2B, 0x8C, 0x8C));
        private static readonly Brush ResultFill = Freeze(Color.FromRgb(0x8A, 0x6C, 0xD8));
        private static readonly Brush EdgeStroke = Freeze(Color.FromRgb(0x5A, 0x63, 0x78));
        private static readonly Brush LabelFill = Freeze(Color.FromRgb(0x22, 0x2B, 0x3F));
        private static readonly Brush SurfaceFill = Freeze(Color.FromRgb(0x10, 0x18, 0x27));
        private static readonly Brush DotFill = Freeze(Color.FromRgb(0x23, 0x2B, 0x3D));
        private static readonly Brush BandFill = Freeze(Color.FromArgb(0x30, 0x4A, 0x7C, 0xE8));

        private readonly string _key;
        private readonly string _name;
        private readonly Func<string> _read;
        private readonly Action<string> _write;
        private FlowFile _flow;

        /// <summary>最後に読んだ・書いたページの中身。ほかで変わったかを見る。</summary>
        private string _known;

        private readonly Canvas _canvas = new Canvas();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly TextBlock _status = new TextBlock();
        private readonly Dictionary<FlowCard, CardParts> _cards = new Dictionary<FlowCard, CardParts>();
        private readonly List<UIElement> _edgeElements = new List<UIElement>();

        // 選んでいるカード
        private readonly HashSet<FlowCard> _selection = new HashSet<FlowCard>();

        // 上の段の「止める」（自動で進めている間だけ出す）
        private Button _stopButton;

        /// <summary>送った先（会話の名前など）。ファイルとページごと（アプリを閉じるまで）。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> SentWhereByFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>答えを待っている結果のカードの題。ファイルとページごと（アプリを閉じるまで。2026-11-17）。</summary>
        private static readonly Dictionary<string, HashSet<string>> WaitingByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private bool _filling;

        private readonly DispatcherTimer _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private bool _dirty;

        // 元に戻す（フローのテキストを丸ごと覚える）。欄に打つ間は、打ち始めに1回だけ覚える
        private readonly List<string> _undo = new List<string>();
        private readonly List<string> _redo = new List<string>();
        private bool _typingRecorded;
        private int _pasteCount;

        // ドラッグ
        private enum DragMode
        {
            None,
            Move,
            Connect,
            Band,
            Pan,
        }

        private DragMode _drag;
        private Point _dragStart;
        private Dictionary<FlowCard, Point> _dragOrigins;
        private bool _dragMoved;
        private bool _dragRecorded;
        private FlowCard _pressedCard;
        private FlowCard _connectFrom;
        private Path _connectLine;
        private Rectangle _band;
        private double _panX, _panY;

        private sealed class CardParts
        {
            public Grid Root;
            public Border Frame;
            public TextBlock Title;
            public TextBox Editor;
            public TextBlock Hint;
            public StackPanel FileList;
            public Border Icon;
            public TextBlock IconText;
            public Border Done;
            public Button Send;
            public FrameworkElement Footer;
        }

        /// <summary>カードを送る（enter: Enter まで押す）。送れたら持ち主が MarkSent を呼ぶ。</summary>
        public event Action<FlowCard, bool> SendRequested;

        /// <summary>× で閉じる。</summary>
        public event Action CloseRequested;

        /// <summary>「止める」（自動で進めるのをやめる）。</summary>
        public event Action StopRequested;

        /// <summary>カードにファイルを落とした。持ち主が添付フォルダに取り込み（外のファイルならコピー）、添付を返す。取り込めなければ null。</summary>
        public Func<string[], List<FlowAttachment>> ImportFiles;

        /// <summary>Ctrl+S（文書を保存する）。</summary>
        public event Action SaveRequested;

        /// <param name="key">✓ を覚える見出し（文書とページ）。</param>
        /// <param name="name">上の段に出す名前。</param>
        /// <param name="read">ページの中身を読む。ページが無くなっていれば null。</param>
        /// <param name="write">ページに書く。</param>
        public FlowView(string key, string name, Func<string> read, Action<string> write)
        {
            _key = key;
            _name = name;
            _read = read;
            _write = write;
            Background = SurfaceFill;
            Focusable = true;
            FocusVisualStyle = null;

            _saveDelay.Tick += delegate
            {
                _saveDelay.Stop();
                Save();
            };

            Children.Add(BuildBar());

            var dots = new DrawingBrush
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 20, 20),
                ViewportUnits = BrushMappingMode.Absolute,
                // 透明な 20×20 の四角を入れて、点を 1 つ分のマスに置く（点だけだと、点がマスいっぱいに引き伸ばされて大きな丸になった。2026-11-17）
                Drawing = new DrawingGroup
                {
                    Children =
                    {
                        new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 20, 20))),
                        new GeometryDrawing(DotFill, null, new EllipseGeometry(new Point(10, 10), 1, 1)),
                    },
                },
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

            _canvas.AllowDrop = true;
            _canvas.DragOver += (sender, e) => DragOverFiles(e);
            _canvas.Drop += (sender, e) => DropFilesOnCanvas(e);
            _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            _canvas.LostMouseCapture += delegate { EndDrag(false); };
            _scroll.PreviewMouseDown += Scroll_PreviewMouseDown;
            _scroll.PreviewMouseUp += Scroll_PreviewMouseUp;
            _scroll.PreviewMouseMove += Scroll_PreviewMouseMove;
            _scroll.PreviewMouseWheel += Scroll_PreviewMouseWheel;
            PreviewKeyDown += View_PreviewKeyDown;

            Load();
            ShowStatus("ホイールで拡大・真ん中のボタンで動かす・右の丸から線を引いてつなぐ・Ctrl+Z で元に戻す");
        }

        /// <summary>このフローの見出し（文書とページ）。会話を覚える見出しに使う。</summary>
        public string FlowKey
        {
            get { return _key; }
        }

        /// <summary>今のフロー（送るときに、会話の切れ目を探すのに使う）。</summary>
        public FlowFile Flow
        {
            get { return _flow; }
        }

        /// <summary>1枚だけ選んでいるときのカード。</summary>
        private FlowCard Single
        {
            get { return _selection.Count == 1 ? _selection.First() : null; }
        }

        // ---------------------------------------------------------------
        // 読み書き
        // ---------------------------------------------------------------

        private void Load()
        {
            var text = _read() ?? string.Empty;
            _known = text;
            _undo.Clear();
            _redo.Clear();
            Restore(text);
            _dirty = false;
        }

        /// <summary>フローのテキストから作り直す（読んだとき・元に戻すとき）。選んでいたカードは題で選び直す。</summary>
        private void Restore(string text)
        {
            var selected = _selection.Select(card => card.Title).ToList();
            _selection.Clear();
            _flow = FlowFile.Parse(text);
            PlaceUnplaced();
            Rebuild();
            foreach (var title in selected)
            {
                var card = _flow.Find(title);
                if (card != null) _selection.Add(card);
            }
            SelectionChanged();
        }

        /// <summary>ページがほかで（Claude の書き換えを読み直した・元に戻すなど）変わっていたら読み直す。直しかけ（書く前）なら、先に書く。</summary>
        public void ReloadIfChanged()
        {
            var text = _read();
            if (text == null || text == _known) return;
            if (_dirty)
            {
                // 書く前に読み直すと直しかけが消える。ページのほうが変わったときは、ページを取る（文書の読み直しは未保存なら起きないので、ここに来るのはまれ）
                _saveDelay.Stop();
                _dirty = false;
            }
            Load();
            ShowStatus("ページが変わったので読み直しました");
        }

        /// <summary>直しかけがあれば、すぐ書く（閉じるとき・保存の前）。</summary>
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
            var text = _flow.Serialize();
            _write(text);
            _known = text;
            _dirty = false;
        }

        public void ShowStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        // ---------------------------------------------------------------
        // 元に戻す
        // ---------------------------------------------------------------

        /// <summary>変える前に呼ぶ。今の形を覚える（同じなら覚えない）。やり直しの分は捨てる。</summary>
        private void Record()
        {
            var now = _flow.Serialize();
            if (_undo.Count == 0 || _undo[_undo.Count - 1] != now)
            {
                _undo.Add(now);
                if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            }
            _redo.Clear();
        }

        private void Undo()
        {
            if (_undo.Count == 0)
            {
                ShowStatus("元に戻すものがありません");
                return;
            }
            _redo.Add(_flow.Serialize());
            var text = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(text);
            Changed();
        }

        private void Redo()
        {
            if (_redo.Count == 0)
            {
                ShowStatus("やり直すものがありません");
                return;
            }
            _undo.Add(_flow.Serialize());
            var text = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(text);
            Changed();
        }

        // ---------------------------------------------------------------
        // 上の段
        // ---------------------------------------------------------------

        private UIElement BuildBar()
        {
            var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

            var close = ToolButton("", "閉じる");
            close.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            close.FontSize = 10;
            close.Click += delegate { if (CloseRequested != null) CloseRequested(); };
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);

            var name = new TextBlock
            {
                Text = _name,
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
            var addFiles = ToolButton("＋ 添付", "ファイルを渡すカードを足す（ファイルをドラッグして入れ、Claude のカードへつなぐ）");
            addFiles.Click += delegate { AddCard(FlowKind.Files); };
            var addResult = ToolButton("＋ 結果", "前の Claude のカードの答えを受け取るカードを足す（選んでいるカードからつなぐ）");
            addResult.Click += delegate { AddCard(FlowKind.Result); };
            var delete = ToolButton("削除 (Del)", "選んでいるカードを消す（Ctrl+Z で戻る）");
            delete.Click += delegate { DeleteSelection(); };
            var clear = ToolButton("印を消す", "送ったカードの ✓ を全部消す");
            clear.Click += delegate { ClearMarks(); };
            var reload = ToolButton("読み直す", "ページから読み直す");
            reload.Click += delegate
            {
                Flush();
                Load();
                ShowStatus("読み直しました");
            };
            buttons.Children.Add(addClaude);
            buttons.Children.Add(addResult);
            buttons.Children.Add(addFiles);
            _stopButton = ToolButton("■ 止める", "自動で次のカードへ進むのをやめる（今の Claude の作業は止めない）");
            _stopButton.Visibility = Visibility.Collapsed;
            _stopButton.Click += delegate { if (StopRequested != null) StopRequested(); };
            buttons.Children.Add(delete);
            buttons.Children.Add(Separator());
            buttons.Children.Add(clear);
            buttons.Children.Add(_stopButton);
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

        /// <summary>カードに打ち始めたときに1回だけ、打つ前の形を覚える（1字ごとに覚えると、元に戻すが1字ずつになる）。</summary>
        private void RecordTyping()
        {
            if (_typingRecorded) return;
            Record();
            _typingRecorded = true;
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
            _band = null;
            _connectLine = null;

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

            // 見出しは種類（題は見せない。2026-11-17）
            parts.Title = new TextBlock
            {
                Foreground = Resource("TextMuted"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var header = new DockPanel { LastChildFill = true };
            header.Children.Add(parts.Icon);
            header.Children.Add(parts.Done);
            header.Children.Add(parts.Title);
            DockPanel.SetDock(header, Dock.Top);

            // 本文はカードの中で直接打つ（2026-11-17。右の欄は無くした）。空のときは薄い字で何を書くかを出す
            parts.Editor = new TextBox
            {
                Text = card.Body,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 36,
                MaxHeight = 260,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = Resource("TextPrimary"),
                CaretBrush = Resource("TextPrimary"),
                FontFamily = Theme.BodyFont,
                FontSize = 13,
            };
            parts.Editor.GotKeyboardFocus += delegate
            {
                _typingRecorded = false;
                if (!_selection.Contains(card) || _selection.Count > 1) SelectOnly(card);
            };
            parts.Editor.TextChanged += delegate
            {
                if (_filling) return;
                var text = parts.Editor.Text.Replace("\r\n", "\n");
                if (text == card.Body) return;
                RecordTyping();
                card.Body = text;
                parts.Hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Changed();
            };
            parts.Hint = new TextBlock
            {
                Foreground = Resource("TextMuted"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false,
            };
            parts.FileList = new StackPanel();
            var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(parts.Hint);
            body.Children.Add(parts.Editor);
            body.Children.Add(parts.FileList);
            DockPanel.SetDock(body, Dock.Top);

            var send = ToolButton("▶ 送る", "Claude に送る。つながるカードは、Claude の答えが終わるたびに順に送る");
            send.FontSize = 12;
            send.Padding = new Thickness(8, 1, 8, 1);
            send.Click += delegate { RequestSend(card, true); };
            parts.Send = send;
            DockPanel.SetDock(send, Dock.Right);
            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
            footer.Children.Add(send);
            DockPanel.SetDock(footer, Dock.Bottom);
            parts.Footer = footer;

            var content = new DockPanel { LastChildFill = false };
            content.Children.Add(header);
            content.Children.Add(footer);
            content.Children.Add(body);

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
                CardPressed(card, e);
            };
            parts.Frame.ContextMenu = CardMenu(card);
            parts.Frame.AllowDrop = true;
            // 本文の欄（TextBox）が先にファイルを断らないよう、先回り（Preview）で受ける。ファイルでなければ欄に任せる
            parts.Frame.PreviewDragOver += (sender, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DragOverFiles(e); };
            parts.Frame.PreviewDrop += (sender, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropFiles(card, e); };
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
            var send = new MenuItem { Header = "送る（つながるカードは答えが終わるたびに順に）" };
            send.Click += delegate { RequestSend(card, true); };

            // 種類を変える（2026-11-17。右の欄を無くしたので、ここで）
            var kind = new MenuItem { Header = "種類" };
            foreach (var each in new[] { FlowKind.Claude, FlowKind.Result, FlowKind.Files })
            {
                var item = new MenuItem { Header = FlowFile.NameOf(each), IsCheckable = true };
                var value = each;
                item.Click += delegate
                {
                    if (card.Kind == value) return;
                    Record();
                    card.Kind = value;
                    UpdateCard(card);
                    RefreshMarks();
                    Changed();
                };
                kind.Items.Add(item);
            }
            var unmark = new MenuItem { Header = "✓ を外す" };
            unmark.Click += delegate
            {
                SentSet().Remove(card.Title);
                RefreshMarks();
            };
            var copy = new MenuItem { Header = "コピー", InputGestureText = "Ctrl+C" };
            copy.Click += delegate { CopySelection(false); };
            var duplicate = new MenuItem { Header = "複製", InputGestureText = "Ctrl+D" };
            duplicate.Click += delegate { Duplicate(); };
            var copyText = new MenuItem { Header = "送るテキストをコピー" };
            copyText.Click += delegate
            {
                try
                {
                    Clipboard.SetText(card.Body);
                }
                catch (Exception)
                {
                }
            };
            var remove = new MenuItem { Header = "削除", InputGestureText = "Del" };
            remove.Click += delegate { DeleteSelection(); };
            menu.Items.Add(send);
            menu.Items.Add(unmark);
            menu.Items.Add(kind);
            menu.Items.Add(new Separator());
            menu.Items.Add(copy);
            menu.Items.Add(duplicate);
            menu.Items.Add(copyText);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
            menu.Opened += delegate
            {
                // 選んでいないカードの右クリックは、そのカードだけを選ぶ
                if (!_selection.Contains(card)) SelectOnly(card);
                send.Visibility = IsStart(card) ? Visibility.Visible : Visibility.Collapsed;
                foreach (MenuItem item in kind.Items) item.IsChecked = (string)item.Header == FlowFile.NameOf(card.Kind);
            };
            return menu;
        }

        /// <summary>カードを1行で言う（つながりの一覧）。題は見せないので、送るテキストの最初の行。</summary>
        private static string Describe(FlowCard card)
        {
            if (card == null) return "（無いカード）";
            var first = card.Body.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
            return FlowFile.NameOf(card.Kind) + " " + card.Title + ": " + (first ?? "（空）");
        }

        private void UpdateCard(FlowCard card)
        {
            CardParts parts;
            if (!_cards.TryGetValue(card, out parts)) return;

            var result = card.Kind == FlowKind.Result;
            var waiting = result && WaitingSet().Contains(card.Title);
            parts.Title.Text = FlowFile.NameOf(card.Kind) + (waiting ? " — 答えを待っています…" : string.Empty);
            parts.Title.ToolTip = "カード " + card.Title + (result ? "（次のカードの本文に {{" + card.Title + "}} と書くと、ここの中身が入る）" : string.Empty);
            var files = card.Kind == FlowKind.Files;
            parts.Icon.Background = files ? FilesFill : result ? ResultFill : ClaudeFill;
            parts.IconText.Text = files ? "添" : result ? "=" : "AI";
            parts.IconText.FontSize = card.Kind == FlowKind.Claude ? 10 : 12;

            // 本文（Claude・結果）か、ファイルの一覧（添付）
            parts.Editor.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
            if (parts.Editor.Text.Replace("\r\n", "\n") != card.Body)
            {
                var filling = _filling;
                _filling = true;
                parts.Editor.Text = card.Body;
                _filling = filling;
            }
            parts.Hint.Text = result ? "（まだ受け取っていない。直接書いてもよい）" : "Claude に送るテキストを打つ";
            parts.Hint.Visibility = !files && card.Body.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            parts.FileList.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            parts.FileList.Children.Clear();
            if (files && card.Attachments.Count == 0)
                parts.FileList.Children.Add(new TextBlock { Text = "（ファイルをここへドラッグ）", Foreground = Resource("TextMuted"), FontSize = 13 });
            foreach (var attachment in files ? card.Attachments.ToList() : new List<FlowAttachment>())
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var remove = ToolButton("×", "このファイルを外す（ファイルは消さない）");
                remove.Padding = new Thickness(6, 0, 6, 0);
                var item = attachment;
                remove.Click += delegate
                {
                    Record();
                    card.Attachments.Remove(item);
                    UpdateCard(card);
                    Changed();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock
                {
                    Text = "📎 " + System.IO.Path.GetFileName(attachment.Target.Trim('<', '>')),
                    ToolTip = attachment.Target.Trim('<', '>'),
                    Foreground = Resource("TextPrimary"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                parts.FileList.Children.Add(row);
            }
            parts.Footer.Visibility = IsStart(card) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 送るボタンを出すカード（2026-11-17）: Claude のカードで、Claude・結果のカードからつながっていないもの（添付のカードからのつながりは数えない）。
        /// つながった先のカードは、Claude の答えが終わるたびに持ち主が順に送る。
        /// </summary>
        public bool IsStart(FlowCard card)
        {
            return card.Kind == FlowKind.Claude
                   && !_flow.Cards.Any(other => other.Kind != FlowKind.Files && other.Edges.Any(edge => edge.Target == card.Title));
        }

        /// <summary>カードの本文の欄に打てるようにする。</summary>
        private void FocusEditor(FlowCard card)
        {
            CardParts parts;
            if (card == null || !_cards.TryGetValue(card, out parts) || parts.Editor.Visibility != Visibility.Visible) return;
            parts.Editor.Focus();
            parts.Editor.CaretIndex = parts.Editor.Text.Length;
        }

        private void AddCard(FlowKind kind)
        {
            Record();
            var from = Single;
            var card = new FlowCard { Kind = kind, Title = _flow.NewId(), HasPosition = true };

            // 選んでいるカードの右（無ければ見えているところの左上）に置く。重なれば下へずらす
            // 添付のカードは、選んでいる Claude のカードの左に置いて、そこへつなぐ（ファイルを渡す向き）
            var feeds = kind == FlowKind.Files && from != null && from.Kind == FlowKind.Claude;
            Point point;
            if (feeds) point = new Point(Math.Max(20, from.X - CardWidth - 80), from.Y);
            else if (from != null) point = new Point(from.X + CardWidth + 80, from.Y);
            else point = new Point(_scroll.HorizontalOffset / _scale.ScaleX + 40, _scroll.VerticalOffset / _scale.ScaleY + 40);
            while (_flow.Cards.Any(other => Math.Abs(other.X - point.X) < CardWidth && Math.Abs(other.Y - point.Y) < 120)) point.Y += 140;
            card.X = Math.Round(point.X / 10) * 10;
            card.Y = Math.Round(point.Y / 10) * 10;

            // 選んでいたカードからつなぐ
            if (feeds) card.Edges.Add(new FlowEdge { Target = from.Title });
            else if (from != null && kind != FlowKind.Files) from.Edges.Add(new FlowEdge { Target = card.Title });

            _flow.Cards.Add(card);
            AddCardElement(card);
            UpdateExtent();
            SelectOnly(card);
            Dispatcher.BeginInvoke(new Action(RedrawEdges), DispatcherPriority.Loaded);
            Changed();

            if (kind != FlowKind.Files) Dispatcher.BeginInvoke(new Action(() => FocusEditor(card)), DispatcherPriority.Loaded);   // すぐ打てるように
        }

        /// <summary>選んでいるカードを消す（つながりも）。Ctrl+Z で戻るので尋ねない（図エディタと同じ）。</summary>
        private void DeleteSelection()
        {
            if (_selection.Count == 0) return;
            Record();
            var count = _selection.Count;
            foreach (var card in _selection.ToList())
            {
                _flow.Remove(card);
                CardParts parts;
                if (_cards.TryGetValue(card, out parts)) _canvas.Children.Remove(parts.Root);
                _cards.Remove(card);
            }
            _selection.Clear();
            SelectionChanged();
            RedrawEdges();
            Changed();
            ShowStatus(count + " 枚消しました（Ctrl+Z で戻る）");
        }

        // ---------------------------------------------------------------
        // 選ぶ
        // ---------------------------------------------------------------

        private void SelectOnly(FlowCard card)
        {
            _selection.Clear();
            if (card != null && _cards.ContainsKey(card)) _selection.Add(card);
            SelectionChanged();
        }

        private void SelectionChanged()
        {
            _selection.RemoveWhere(card => !_cards.ContainsKey(card));
            RefreshMarks();
        }

        private void CardPressed(FlowCard card, MouseButtonEventArgs e)
        {
            Focus();   // 欄からフォーカスを外す。キーをここで受ける

            if (e.ClickCount == 2)
            {
                SelectOnly(card);
                FocusEditor(card);
                return;
            }

            var modifiers = Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control);
            if (modifiers != 0)
            {
                // Shift・Ctrl で足す・外す
                if (!_selection.Remove(card)) _selection.Add(card);
                SelectionChanged();
                if (!_selection.Contains(card)) return;
            }
            else if (!_selection.Contains(card))
            {
                SelectOnly(card);
            }

            _pressedCard = modifiers == 0 ? card : null;
            StartMove(e);
        }

        // ---------------------------------------------------------------
        // 添付（ファイルのドロップ）
        // ---------------------------------------------------------------

        private static void DragOverFiles(DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// カードにファイルを落とした（2026-11-17。ファイルは添付のカードが持つ）。添付のカードなら足す。Claude のカードなら、そこへつないだ
        /// 添付のカード（無ければ左に作ってつなぐ）に足す。
        /// </summary>
        private void DropFiles(FlowCard card, DragEventArgs e)
        {
            e.Handled = true;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (card == null || paths == null || paths.Length == 0 || ImportFiles == null) return;
            if (card.Kind == FlowKind.Result)
            {
                ShowStatus("ファイルは、添付のカードか Claude のカードへドラッグしてください");
                return;
            }

            var added = ImportFiles(paths);
            if (added == null || added.Count == 0) return;

            Record();
            var holder = card;
            if (card.Kind == FlowKind.Claude)
            {
                holder = _flow.Cards.FirstOrDefault(other => other.Kind == FlowKind.Files && other.Edges.Any(edge => edge.Target == card.Title));
                if (holder == null)
                {
                    holder = NewFilesCard(new Point(card.X - CardWidth - 80, card.Y));
                    holder.Edges.Add(new FlowEdge { Target = card.Title });
                }
            }
            AddFiles(holder, added);
        }

        /// <summary>何も無い所にファイルを落とした。そこに添付のカードを作る（つなぐのは利用者）。</summary>
        private void DropFilesOnCanvas(DragEventArgs e)
        {
            e.Handled = true;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0 || ImportFiles == null) return;

            var added = ImportFiles(paths);
            if (added == null || added.Count == 0) return;

            Record();
            var at = e.GetPosition(_canvas);
            AddFiles(NewFilesCard(new Point(at.X - CardWidth / 2, at.Y - 20)), added);
        }

        /// <summary>添付のカードを作って置く（重なれば下へずらす）。Record は呼ぶ側で。</summary>
        private FlowCard NewFilesCard(Point point)
        {
            point.X = Math.Max(20, point.X);   // 左の端より外に出さない
            point.Y = Math.Max(20, point.Y);
            while (_flow.Cards.Any(other => Math.Abs(other.X - point.X) < CardWidth && Math.Abs(other.Y - point.Y) < 120)) point.Y += 140;
            var card = new FlowCard
            {
                Kind = FlowKind.Files,
                Title = _flow.NewId(),
                X = Math.Round(point.X / 10) * 10,
                Y = Math.Round(point.Y / 10) * 10,
                HasPosition = true,
            };
            _flow.Cards.Add(card);
            AddCardElement(card);
            UpdateExtent();
            return card;
        }

        private void AddFiles(FlowCard holder, List<FlowAttachment> added)
        {
            foreach (var attachment in added)
            {
                if (holder.Attachments.All(other => other.Target != attachment.Target)) holder.Attachments.Add(attachment);
            }
            SelectOnly(holder);
            UpdateCard(holder);
            Dispatcher.BeginInvoke(new Action(RedrawEdges), DispatcherPriority.Loaded);
            Changed();
            ShowStatus(added.Count + " 件のファイルを添付のカードに入れました");
        }

        // ---------------------------------------------------------------
        // 送る・印
        // ---------------------------------------------------------------

        private void RequestSend(FlowCard card, bool enter)
        {
            if (card == null) return;
            if (!IsStart(card))
            {
                ShowStatus("送れるのは、どこからもつながっていない Claude のカードだけです（続くカードは自動で送ります）");
                return;
            }
            if (card.Body.Trim().Length == 0)
            {
                ShowStatus("送るテキストがありません");
                return;
            }
            if (SendRequested != null) SendRequested(card, enter);
        }

        /// <summary>送れた（持ち主が呼ぶ）。✓ を付け、そこからつながる先を「次」にする。</summary>
        public void MarkSent(FlowCard card, string where)
        {
            SentSet().Add(card.Title);
            Dictionary<string, string> places;
            if (!SentWhereByFile.TryGetValue(_key, out places))
            {
                places = new Dictionary<string, string>();
                SentWhereByFile[_key] = places;
            }
            places[card.Title] = where;
            LastSentByFile[_key] = card.Title;
            RefreshMarks();
        }

        private HashSet<string> WaitingSet()
        {
            HashSet<string> waiting;
            if (!WaitingByFile.TryGetValue(_key, out waiting))
            {
                waiting = new HashSet<string>();
                WaitingByFile[_key] = waiting;
            }
            return waiting;
        }

        /// <summary>結果のカードが答えを待っているかを付け外しする（持ち主が呼ぶ。開いていないフローでも覚えておく）。</summary>
        public static void SetWaiting(string key, string title, bool waiting)
        {
            HashSet<string> set;
            if (!WaitingByFile.TryGetValue(key, out set))
            {
                set = new HashSet<string>();
                WaitingByFile[key] = set;
            }
            if (waiting) set.Add(title);
            else set.Remove(title);
        }

        /// <summary>待っている印を付け直す（SetWaiting のあと）。</summary>
        public void RefreshWaiting()
        {
            foreach (var card in _flow.Cards.Where(card => card.Kind == FlowKind.Result)) UpdateCard(card);
        }

        /// <summary>
        /// 結果のカードに答えを入れる（持ち主が呼ぶ）。元に戻せる。書くのは少し後（Changed）。カードが無ければ false。
        /// </summary>
        public bool SetResult(string title, string text)
        {
            var card = _flow.Find(title);
            if (card == null || card.Kind != FlowKind.Result) return false;
            Record();
            card.Body = text;
            UpdateCard(card);
            Changed();
            return true;
        }

        private HashSet<string> SentSet()
        {
            HashSet<string> sent;
            if (!SentByFile.TryGetValue(_key, out sent))
            {
                sent = new HashSet<string>();
                SentByFile[_key] = sent;
            }
            return sent;
        }

        private void ClearMarks()
        {
            SentSet().Clear();
            LastSentByFile.Remove(_key);
            RefreshMarks();
            ShowStatus("印を消しました");
        }

        /// <summary>✓ と「次」と選んでいる縁を付け直す。</summary>
        private void RefreshMarks()
        {
            var sent = SentSet();
            string last;
            LastSentByFile.TryGetValue(_key, out last);
            var lastCard = last != null ? _flow.Find(last) : null;

            HashSet<string> next;
            if (lastCard != null)
            {
                // 結果のカードは送らないので、その先のカードも「次」にする
                next = new HashSet<string>(lastCard.Edges.Select(edge => edge.Target));
                foreach (var target in next.ToList())
                {
                    var card = _flow.Find(target);
                    if (card != null && card.Kind == FlowKind.Result) next.UnionWith(card.Edges.Select(edge => edge.Target));
                }
            }
            else
            {
                // 添付のカードからのつながりは数えない。送らないカード（結果・添付）は「次」にしない
                var targets = new HashSet<string>(_flow.Cards.Where(card => card.Kind != FlowKind.Files).SelectMany(card => card.Edges).Select(edge => edge.Target));
                next = new HashSet<string>(_flow.Cards.Where(card => card.Kind == FlowKind.Claude && !targets.Contains(card.Title)).Select(card => card.Title));
            }

            foreach (var pair in _cards)
            {
                var card = pair.Key;
                var parts = pair.Value;
                parts.Footer.Visibility = IsStart(card) ? Visibility.Visible : Visibility.Collapsed;
                parts.Done.Visibility = sent.Contains(card.Title) ? Visibility.Visible : Visibility.Collapsed;
                Dictionary<string, string> places;
                string where;
                parts.Done.ToolTip = SentWhereByFile.TryGetValue(_key, out places) && places.TryGetValue(card.Title, out where) && where != null
                    ? where + " に送った"
                    : "送った";

                if (_selection.Contains(card))
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
                parts.Frame.ToolTip = next.Contains(card.Title) && !_selection.Contains(card) ? "次に送るカード" : null;
            }
        }

        // ---------------------------------------------------------------
        // コピー・貼り付け・複製
        // ---------------------------------------------------------------

        /// <summary>選んでいるカードを、フローのテキストにする（選んだカードどうしのつながりだけ残す）。</summary>
        private string SelectionText()
        {
            var chosen = _flow.Cards.Where(card => _selection.Contains(card)).ToList();
            var titles = new HashSet<string>(chosen.Select(card => card.Title));
            var part = new FlowFile();
            foreach (var card in chosen)
            {
                var copy = new FlowCard { Kind = card.Kind, Title = card.Title, Body = card.Body, X = card.X, Y = card.Y, HasPosition = true };
                foreach (var attachment in card.Attachments) copy.Attachments.Add(new FlowAttachment { Label = attachment.Label, Target = attachment.Target });
                foreach (var edge in card.Edges.Where(edge => titles.Contains(edge.Target)))
                    copy.Edges.Add(new FlowEdge { Target = edge.Target, Label = edge.Label });
                part.Cards.Add(copy);
            }
            return part.Serialize();
        }

        private void CopySelection(bool cut)
        {
            if (_selection.Count == 0) return;
            var text = SelectionText();
            try
            {
                var data = new DataObject();
                data.SetData(ClipboardFormat, text);
                data.SetText(string.Join("\n\n", _flow.Cards.Where(card => _selection.Contains(card)).Select(card => card.Body)));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex)
            {
                ShowStatus("コピーできませんでした: " + ex.Message);
                return;
            }
            _pasteCount = 0;
            if (cut) DeleteSelection();
            else ShowStatus(_selection.Count + " 枚コピーしました");
        }

        /// <summary>
        /// Ctrl+V。フロー図でコピーしたカードなら、少しずらして貼る（貼るたびにずらす）。ほかのアプリの文字なら、その文字の Claude のカードを作る。
        /// </summary>
        private void Paste()
        {
            string cards = null, text = null;
            try
            {
                var data = Clipboard.GetDataObject();
                if (data != null && data.GetDataPresent(ClipboardFormat)) cards = data.GetData(ClipboardFormat) as string;
                else if (Clipboard.ContainsText()) text = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                ShowStatus("クリップボードを読めませんでした: " + ex.Message);
                return;
            }

            if (cards != null)
            {
                _pasteCount++;
                Insert(cards, PasteStep * _pasteCount);
                return;
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                Record();
                var card = new FlowCard
                {
                    Kind = FlowKind.Claude,
                    Title = _flow.NewId(),
                    Body = text.Replace("\r\n", "\n").Trim('\n'),
                    X = Math.Round((_scroll.HorizontalOffset / _scale.ScaleX + 40) / 10) * 10,
                    Y = Math.Round((_scroll.VerticalOffset / _scale.ScaleY + 40) / 10) * 10,
                    HasPosition = true,
                };
                _flow.Cards.Add(card);
                AddCardElement(card);
                UpdateExtent();
                SelectOnly(card);
                Changed();
                ShowStatus("貼った文字でカードを作りました");
            }
        }

        private void Duplicate()
        {
            if (_selection.Count == 0) return;
            Insert(SelectionText(), PasteStep);
        }

        /// <summary>フローのテキストのカードを、題を付け直し、ずらして足す。足したカードを選ぶ。</summary>
        private void Insert(string text, double offset)
        {
            var part = FlowFile.Parse(text);
            if (part.Cards.Count == 0) return;

            Record();
            var names = new Dictionary<string, string>();
            foreach (var card in part.Cards)
            {
                var title = _flow.NewId();
                names[card.Title] = title;
                card.Title = title;
                card.X += offset;
                card.Y += offset;
                card.HasPosition = true;
                _flow.Cards.Add(card);   // 次の NewId に数える
            }
            foreach (var card in part.Cards)
            {
                card.Edges.RemoveAll(edge => !names.ContainsKey(edge.Target));
                foreach (var edge in card.Edges) edge.Target = names[edge.Target];
                AddCardElement(card);
            }

            _selection.Clear();
            foreach (var card in part.Cards) _selection.Add(card);
            SelectionChanged();
            UpdateExtent();
            Dispatcher.BeginInvoke(new Action(RedrawEdges), DispatcherPriority.Loaded);
            Changed();
            ShowStatus(part.Cards.Count + " 枚足しました");
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
                Focus();
                SelectOnly(owner);
            };

            // 先の矢じり
            var arrow = new Path
            {
                Data = Geometry.Parse(string.Format(System.Globalization.CultureInfo.InvariantCulture, "M {0},{1} L {2},{3} L {0},{4} Z",
                    end.X - 8, end.Y - 4.5, end.X, end.Y, end.Y + 4.5)),
                Fill = EdgeStroke,
                IsHitTestVisible = false,
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
                Focus();
                SelectOnly(owner);
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
            var at = new Point();
            menu.Opened += delegate { at = Mouse.GetPosition(_canvas); };
            name.Click += delegate { EditEdgeLabel(owner, edge, at); };
            var remove = new MenuItem { Header = "つながりを消す" };
            remove.Click += delegate
            {
                Record();
                owner.Edges.Remove(edge);
                RedrawEdges();
                RefreshMarks();
                Changed();
            };
            menu.Items.Add(name);
            menu.Items.Add(remove);
            return menu;
        }

        /// <summary>線の名前をその場で打つ（Enter・ほかを押すと決める。Esc でやめる）。名前は結果のカードへの頼みにも使う。</summary>
        private void EditEdgeLabel(FlowCard owner, FlowEdge edge, Point at)
        {
            var box = new TextBox { Width = 140, Text = edge.Label, FontSize = 12, Tag = "edge-label" };
            StyleBox(box);
            Canvas.SetLeft(box, at.X - 70);
            Canvas.SetTop(box, at.Y - 12);
            Panel.SetZIndex(box, 50);
            _canvas.Children.Add(box);

            var done = false;
            Action<bool> finish = keep =>
            {
                if (done) return;
                done = true;
                _canvas.Children.Remove(box);
                var label = FlowFile.CleanTitle(box.Text).Replace("[", "").Replace("]", "");
                if (keep && label != edge.Label && owner.Edges.Contains(edge))
                {
                    Record();
                    edge.Label = label;
                    RedrawEdges();
                    Changed();
                }
                Focus();
            };
            box.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Escape)
                {
                    e.Handled = true;
                    finish(e.Key == Key.Enter);
                }
            };
            box.LostKeyboardFocus += delegate { finish(true); };
            Dispatcher.BeginInvoke(new Action(delegate
            {
                box.Focus();
                box.SelectAll();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>自動で進めている間は「止める」を出す（持ち主が呼ぶ）。</summary>
        public void SetRunning(bool running)
        {
            _stopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateExtent()
        {
            var right = _flow.Cards.Count == 0 ? 0 : _flow.Cards.Max(card => card.X + CardWidth);
            var bottom = _flow.Cards.Count == 0 ? 0 : _flow.Cards.Max(card => card.Y + 160);
            _canvas.Width = Math.Max(right + CanvasMargin, 1600);
            _canvas.Height = Math.Max(bottom + CanvasMargin, 1000);
        }

        // ---------------------------------------------------------------
        // マウス
        // ---------------------------------------------------------------

        private void StartMove(MouseButtonEventArgs e)
        {
            _drag = DragMode.Move;
            _dragStart = e.GetPosition(_canvas);
            _dragOrigins = _selection.ToDictionary(card => card, card => new Point(card.X, card.Y));
            _dragMoved = false;
            _dragRecorded = false;
            _canvas.CaptureMouse();
        }

        private void StartConnect(FlowCard card, MouseButtonEventArgs e)
        {
            Focus();
            SelectOnly(card);
            _drag = DragMode.Connect;
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

        /// <summary>何も無いところを押した: 範囲で選ぶ（Shift・Ctrl なら今の選びに足す）。</summary>
        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            e.Handled = true;
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0 && _selection.Count > 0)
            {
                _selection.Clear();
                SelectionChanged();
            }

            _drag = DragMode.Band;
            _dragStart = e.GetPosition(_canvas);
            _band = new Rectangle { Stroke = Resource("AccentBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 }, Fill = BandFill, IsHitTestVisible = false };
            Panel.SetZIndex(_band, 30);
            Canvas.SetLeft(_band, _dragStart.X);
            Canvas.SetTop(_band, _dragStart.Y);
            _canvas.Children.Add(_band);
            _canvas.CaptureMouse();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var now = e.GetPosition(_canvas);
            switch (_drag)
            {
                case DragMode.Connect:
                    UpdateConnectLine(now);
                    return;

                case DragMode.Band:
                    Canvas.SetLeft(_band, Math.Min(now.X, _dragStart.X));
                    Canvas.SetTop(_band, Math.Min(now.Y, _dragStart.Y));
                    _band.Width = Math.Abs(now.X - _dragStart.X);
                    _band.Height = Math.Abs(now.Y - _dragStart.Y);
                    return;

                case DragMode.Move:
                    var dx = now.X - _dragStart.X;
                    var dy = now.Y - _dragStart.Y;
                    if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
                    if (!_dragRecorded)
                    {
                        // 動かす前の形を覚える（位置はまだ変えていない）
                        Record();
                        _dragRecorded = true;
                    }
                    _dragMoved = true;

                    // Shift で縦か横だけ（大きく動いた向き）
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    {
                        if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                        else dx = 0;
                    }

                    foreach (var pair in _dragOrigins)
                    {
                        // 10 ずつに吸い付ける（並べやすく）
                        pair.Key.X = Math.Max(10, Math.Round((pair.Value.X + dx) / 10) * 10);
                        pair.Key.Y = Math.Max(10, Math.Round((pair.Value.Y + dy) / 10) * 10);
                        pair.Key.HasPosition = true;
                        PlaceCard(pair.Key);
                    }
                    RedrawEdges();
                    return;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var now = e.GetPosition(_canvas);
            switch (_drag)
            {
                case DragMode.Connect:
                {
                    var from = _connectFrom;
                    var target = CardAt(now);
                    EndDrag(false);
                    if (target != null && !ReferenceEquals(target, from) && from.Edges.All(edge => edge.Target != target.Title))
                    {
                        Record();
                        from.Edges.Add(new FlowEdge { Target = target.Title });
                        RedrawEdges();
                        RefreshMarks();
                        Changed();
                    }
                    return;
                }

                case DragMode.Band:
                {
                    // 範囲に収まるカードを選ぶ
                    var area = new Rect(_dragStart, now);
                    EndDrag(false);
                    if (area.Width < 3 && area.Height < 3) return;
                    foreach (var pair in _cards)
                    {
                        var box = new Rect(pair.Key.X, pair.Key.Y, CardWidth, pair.Value.Root.ActualHeight);
                        if (area.Contains(box)) _selection.Add(pair.Key);
                    }
                    SelectionChanged();
                    return;
                }

                case DragMode.Move:
                {
                    var moved = _dragMoved;
                    var pressed = _pressedCard;
                    EndDrag(false);
                    if (moved)
                    {
                        UpdateExtent();
                        Changed();
                    }
                    else if (pressed != null && _selection.Count > 1)
                    {
                        SelectOnly(pressed);   // 動かさずに離した: そのカードだけを選ぶ
                    }
                    return;
                }
            }
        }

        /// <summary>つかむのをやめる。cancel なら、動かしていたカードを元の位置に戻す（Esc）。</summary>
        private void EndDrag(bool cancel)
        {
            if (cancel && _drag == DragMode.Move && _dragMoved && _dragOrigins != null)
            {
                foreach (var pair in _dragOrigins)
                {
                    pair.Key.X = pair.Value.X;
                    pair.Key.Y = pair.Value.Y;
                    PlaceCard(pair.Key);
                }
                RedrawEdges();
                if (_dragRecorded && _undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            }

            var wasPan = _drag == DragMode.Pan;
            _drag = DragMode.None;
            _dragOrigins = null;
            _pressedCard = null;
            if (_connectLine != null) _canvas.Children.Remove(_connectLine);
            _connectLine = null;
            _connectFrom = null;
            if (_band != null) _canvas.Children.Remove(_band);
            _band = null;
            if (wasPan) _scroll.Cursor = null;
            if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
            if (_scroll.IsMouseCaptured) _scroll.ReleaseMouseCapture();
        }

        private void PlaceCard(FlowCard card)
        {
            CardParts parts;
            if (!_cards.TryGetValue(card, out parts)) return;
            Canvas.SetLeft(parts.Root, card.X - PortSize / 2);
            Canvas.SetTop(parts.Root, card.Y);
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

        /// <summary>真ん中のボタンのドラッグで、図全体を動かす。</summary>
        private void Scroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            EndDrag(false);
            Focus();
            _drag = DragMode.Pan;
            _dragStart = e.GetPosition(_scroll);
            _panX = _scroll.HorizontalOffset;
            _panY = _scroll.VerticalOffset;
            _scroll.Cursor = Cursors.ScrollAll;
            _scroll.CaptureMouse();
        }

        private void Scroll_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_drag != DragMode.Pan) return;
            var point = e.GetPosition(_scroll);
            _scroll.ScrollToHorizontalOffset(_panX - (point.X - _dragStart.X));
            _scroll.ScrollToVerticalOffset(_panY - (point.Y - _dragStart.Y));
        }

        private void Scroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle || _drag != DragMode.Pan) return;
            e.Handled = true;
            EndDrag(false);
        }

        /// <summary>ホイールで拡大・縮小（カーソルの所を中心に）。Shift+ホイールで左右、Ctrl+ホイールで上下に動かす（図エディタと同じ）。</summary>
        private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
                return;
            }
            if ((modifiers & ModifierKeys.Control) != 0)
            {
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta);
                return;
            }

            var old = _scale.ScaleX;
            var scale = Math.Max(0.25, Math.Min(3.0, old * (e.Delta > 0 ? 1.15 : 1 / 1.15)));
            if (Math.Abs(scale - old) < 0.001) return;

            var mouse = e.GetPosition(_scroll);
            var anchor = new Point((_scroll.HorizontalOffset + mouse.X) / old, (_scroll.VerticalOffset + mouse.Y) / old);
            _scale.ScaleX = scale;
            _scale.ScaleY = scale;
            _scroll.UpdateLayout();
            _scroll.ScrollToHorizontalOffset(anchor.X * scale - mouse.X);
            _scroll.ScrollToVerticalOffset(anchor.Y * scale - mouse.Y);
            ShowStatus("拡大 " + Math.Round(scale * 100) + "%");
        }

        // ---------------------------------------------------------------
        // キー
        // ---------------------------------------------------------------

        private void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // 打っている欄（カードの本文）では、Esc でフロー図へ戻るだけ（ほかのキーは欄に任せる。元に戻すも欄の中の分）。Ctrl+S は保存
            var typing = Keyboard.FocusedElement as TextBox;
            if (typing != null)
            {
                if (Equals(typing.Tag, "edge-label")) return;   // 線の名前の欄は自分で Enter・Esc を受ける
                if (e.Key == Key.S && control)
                {
                    e.Handled = true;
                    Flush();
                    if (SaveRequested != null) SaveRequested();
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Focus();
                }
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Escape:
                    if (_drag != DragMode.None) EndDrag(true);
                    else if (_selection.Count > 0)
                    {
                        _selection.Clear();
                        SelectionChanged();
                    }
                    break;
                case Key.Delete:
                    DeleteSelection();
                    break;
                case Key.F2:
                    FocusEditor(Single);
                    break;
                case Key.A:
                    if (!control) return;
                    _selection.Clear();
                    foreach (var card in _cards.Keys) _selection.Add(card);
                    SelectionChanged();
                    break;
                case Key.C:
                    if (!control) return;
                    CopySelection(false);
                    break;
                case Key.X:
                    if (!control) return;
                    CopySelection(true);
                    break;
                case Key.V:
                    if (!control) return;
                    Paste();
                    break;
                case Key.D:
                    if (!control) return;
                    Duplicate();
                    break;
                case Key.Z:
                    if (!control) return;
                    if (shift) Redo();
                    else Undo();
                    break;
                case Key.Y:
                    if (!control) return;
                    Redo();
                    break;
                case Key.S:
                    if (!control) return;
                    Flush();
                    if (SaveRequested != null) SaveRequested();
                    break;
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    if (_selection.Count == 0) return;
                    Nudge(key, shift ? 50 : control ? 1 : 10);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        /// <summary>矢印キーで選んでいるカードを動かす（10。Shift で 50、Ctrl で 1）。</summary>
        private void Nudge(Key key, double step)
        {
            Record();
            var dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
            var dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
            foreach (var card in _selection)
            {
                card.X = Math.Max(10, card.X + dx);
                card.Y = Math.Max(10, card.Y + dy);
                card.HasPosition = true;
                PlaceCard(card);
            }
            RedrawEdges();
            UpdateExtent();
            Changed();
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
