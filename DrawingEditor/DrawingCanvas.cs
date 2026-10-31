using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DrawingEditor
{
    /// <summary>描く道具。</summary>
    internal enum Tool
    {
        Select,
        Line,
        Rect,
        Ellipse,
        Text,
        Dimension,
    }

    /// <summary>右の欄に出す寸法の1つ（mm・度・pt）。</summary>
    internal sealed class Field
    {
        public string Key;
        public string Label;
        public double Value;
    }

    /// <summary>
    /// 図を描く面（SPEC 8.4.3）。Excel のオートシェイプの操作に合わせる。
    ///
    /// ・道具を選んで、1回押して始点、カーソルを動かしてもう1回押して終点（AutoCAD と同じ。ドラッグでも描ける）。
    ///   描いたら「選ぶ」に戻る（2026-10-19。前の日に道具のままにしたが、戻した）。図形を右クリックすると、その図形と同じ道具になり、何も無い所の右クリックで「選ぶ」に戻る
    /// ・四角・丸は中に文字を書ける（ダブルクリックか F2。draw.io と同じ）。中を押しても選べる
    /// ・直線・寸法線の端を図形の点に吸い付けて描くと、その図形につながる。図形を動かすと、線の端が付いてくる（直線のまま）
    /// ・直線の途中をダブルクリックすると折れ目を足し、折れ目をドラッグで動かす（隣の点と縦・横にそろう）。折れ目のダブルクリックで消す
    /// ・道具は1文字のキーでも選べる: V 選ぶ・L 直線・R 四角・C 丸・T 文字・D 寸法
    /// ・直線はいつも 22.5° ごと（Shift で自由な角度）。四角・丸は Shift で正方形・正円、寸法線は Shift で水平・垂直
    /// ・押して選ぶ（Shift・Ctrl で足す）、何も無い所からドラッグで範囲の中を選ぶ。1つ選ぶと、つまみで大きさ・端を変えられる
    /// ・どの道具のときも、カーソルは図形の端と、直線の部分の真ん中に吸い付き、四角い目印を出す（Alt で吸い付かない）。
    ///   点が無ければ、縦・横それぞれで枠の辺・真ん中にそろう（線の目印）
    /// ・寸法は mm。面は白い紙（書き出す画像と同じ見た目）に、うすい 5mm の方眼
    ///
    /// 描くのは1枚の面（Surface）にまとめて行う。図形の寸法は mm で持ち、描くときに拡大（mm → 画面）を掛ける。
    /// </summary>
    internal sealed class DrawingCanvas : Grid
    {
        private const double HandleSize = 7;      // つまみの大きさ（画面）
        private const double HitTolerance = 5;    // 図形を押せる線からの距離（画面）
        private const double SnapDistance = 7;    // 吸い付く距離（画面）
        private const double MinZoom = 0.25;
        private const double MaxZoom = 8;
        private const int UndoLimit = 200;

        private static readonly Brush Paper = Brushes.White;
        private static readonly Brush Outside = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xCF, 0xD9)));
        private static readonly Pen BoardPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2))), 1));
        private static readonly Brush Ink = Brushes.Black;
        private static readonly Brush GridMinor = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5)));
        private static readonly Brush GridMajor = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEC)));
        private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0xE8)));
        private static readonly Brush AccentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x4A, 0x7C, 0xE8)));
        private static readonly Pen AccentPen = Freeze(new Pen(Accent, 1));
        private static readonly Pen DashedAccent = MakeDashed(Accent);
        private static readonly Pen GuidePen = MakeDashed(Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x8C))));

        private enum Gesture
        {
            None,
            Draw,
            Move,
            Resize,
            Board,
            Rubber,
            Pan,
        }

        private sealed class Snapshot
        {
            public List<Shape> Shapes;
            public List<int> Selected;
            public Rect Board;
            public double Unit;
        }

        private sealed class Surface : FrameworkElement
        {
            private readonly Action<DrawingContext> _render;

            public Surface(Action<DrawingContext> render)
            {
                _render = render;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                _render(drawingContext);
            }
        }

        // コピーした図形。エディタを閉じても残す（別の図へ貼れるように）。
        private static List<Shape> _clipboard = new List<Shape>();

        private readonly List<Shape> _shapes;
        private readonly List<Shape> _selection = new List<Shape>();

        // ボード（書き出す範囲。mm）と、文字の大きさ・線の太さの基準（紙の 1mm が図の何 mm か。DrawingFile.Unit）。
        private Rect _board;
        private double _unit;
        private readonly Surface _surface;
        private readonly TextBox _textBox;

        private Tool _tool = Tool.Select;
        private double _zoom = 1;
        private double _originX = -10;   // 面の左上の mm
        private double _originY = -10;

        // つかんでいる間の状態。
        private Gesture _gesture = Gesture.None;
        private Point _downScreen;
        private Point _downModel;
        private bool _undoPushed;
        private Shape _drawing;
        private Point _drawStart;
        private bool _drawWaiting;      // 1回押して離した（始点を決め、終点を押すのを待っている）
        private Shape _drawStartOwner;  // 始点を吸い付けた図形（線をつなぐ相手）と、その位置
        private double _drawStartU;
        private double _drawStartV;
        private Shape _drawEndOwner;    // 終点を吸い付けた図形と、その位置
        private double _drawEndU;
        private double _drawEndV;
        private Point _moveBase;        // 動かすときにつかんだ点（図形の端・中点に吸い付けたもの）
        private List<Shape> _moveOriginals;
        private Shape _resizeTarget;
        private Shape _resizeOriginal;
        private Rect _boardOriginal;
        private double _unitOriginal;
        private int _handle;
        private bool _additive;
        private double _panOriginX;
        private double _panOriginY;

        // 吸い付いた所の目印（mm）。
        private readonly List<double> _guidesX = new List<double>();
        private readonly List<double> _guidesY = new List<double>();
        private Point? _snapMark;

        // 吸い付いた点の持ち主の図形と、その中の位置（Geometry.Anchor）。交点などに吸い付いたときは null。
        private Shape _snapOwner;
        private double _snapU;
        private double _snapV;

        // 打っている文字。四角・丸の中の文字のこともある（_textIsLabel）。
        private Shape _textShape;
        private bool _textIsNew;
        private bool _textIsLabel;
        private Snapshot _textBefore;

        private int _pasteCount;

        private readonly List<Snapshot> _undo = new List<Snapshot>();
        private readonly List<Snapshot> _redo = new List<Snapshot>();

        public event EventHandler SelectionChanged;
        public event EventHandler ShapesChanged;
        public event EventHandler ToolChanged;
        public event EventHandler ZoomChanged;
        public event EventHandler BoardChanged;

        public DrawingCanvas(List<Shape> shapes, Rect board, double unit)
        {
            _shapes = shapes;
            _board = Geometry.ClampBoard(board);   // 大きさの限りの中に（2026-10-27）
            _unit = unit > 0 && Geometry.IsFinite(unit) ? unit : _board.Width / Geometry.OutputWidth;

            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            Background = Paper;

            _surface = new Surface(Render);
            TextOptions.SetTextFormattingMode(_surface, TextFormattingMode.Ideal);   // 拡大しても字の形を崩さない
            RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);   // 貼った画像を縮めて見せても粗くしない（2026-10-27）
            Children.Add(_surface);

            _textBox = new TextBox();
            _textBox.AcceptsReturn = true;
            _textBox.TextWrapping = TextWrapping.NoWrap;
            _textBox.Background = Paper;
            _textBox.Foreground = Ink;
            _textBox.CaretBrush = Ink;
            _textBox.BorderBrush = Accent;
            _textBox.BorderThickness = new Thickness(1);
            _textBox.Padding = new Thickness(0);
            _textBox.MinWidth = 24;
            _textBox.FontFamily = Geometry.Font;
            _textBox.Visibility = Visibility.Collapsed;
            _textBox.PreviewKeyDown += OnTextKeyDown;
            _textBox.LostKeyboardFocus += delegate { EndTextEdit(); };

            var overlay = new Canvas();
            overlay.Children.Add(_textBox);
            Children.Add(overlay);

            _surface.MouseDown += OnMouseDown;
            _surface.MouseMove += OnMouseMove;
            _surface.MouseUp += OnMouseUp;
            _surface.LostMouseCapture += delegate
            {
                if (_drawWaiting) return;   // 終点を待つ間は、わざとつかむのをやめている
                EndGesture(false);
            };
            _surface.MouseLeave += delegate
            {
                if (_gesture != Gesture.None || !_snapMark.HasValue) return;
                _snapMark = null;
                _surface.InvalidateVisual();
            };
            PreviewMouseWheel += OnMouseWheel;
            PreviewKeyDown += OnKeyDown;
            SizeChanged += delegate { Refresh(); };
        }

        // =====================================================================
        // 外から使うもの
        // =====================================================================

        public IList<Shape> Shapes
        {
            get { return _shapes; }
        }

        public Tool CurrentTool
        {
            get { return _tool; }
            set
            {
                EndTextEdit();
                if (_gesture != Gesture.None && _tool != value) CancelGesture();   // 終点を待っている途中で道具を替えたら、描くのをやめる
                if (_tool == value) return;

                _tool = value;
                if (value != Tool.Select) SetSelection(Enumerable.Empty<Shape>());
                _surface.Cursor = value == Tool.Select ? null : value == Tool.Text ? Cursors.IBeam : Cursors.Cross;
                Raise(ToolChanged);
            }
        }

        public double Zoom
        {
            get { return _zoom; }
        }

        /// <summary>ボード（書き出す範囲。mm）。</summary>
        public Rect Board
        {
            get { return _board; }
        }

        /// <summary>
        /// 文字の大きさ・線の太さの基準: 紙（基準）の 1mm が図の何 mm か。文字（pt）・線の太さ（mm）・寸法の数値と矢印は、これを掛けた大きさで描く。
        /// ボードの大きさを変えても変わらない（2026-10-19。前は「ボードの幅 ÷ 160」で、ボードを変えると文字・線の見え方まで変わった）。
        /// 何も描いていない間だけは、ボードに合わせて変える（実寸で描くとき、先にボードを広げれば、文字・線もその大きさに合う）。
        /// </summary>
        public double Unit
        {
            get { return _unit; }
        }

        /// <summary>何も描いていなければ、基準をボードの幅に合わせる（ボードの幅 ÷ 160。ボードがそのまま本文の幅になる大きさ）。</summary>
        private void FollowBoardWhileEmpty()
        {
            if (_shapes.Count == 0) _unit = _board.Width / Geometry.OutputWidth;
        }

        /// <summary>
        /// ボードの大きさを変える（左上は動かさない。10mm 〜 100m）。
        /// 高さは幅の 10 倍まで（2026-10-27。Geometry.MaxBoardAspect）。超えたら、変えたほうを戻す: 高さを変えたなら高さを縮め、幅を変えたなら幅を広げる。
        /// 数でない値（NaN・∞）は入れない。
        /// </summary>
        public void SetBoardSize(double width, double height)
        {
            if (!Geometry.IsFinite(width) || !Geometry.IsFinite(height)) return;

            var widthChanged = Math.Abs(width - _board.Width) >= 1e-9;
            width = Math.Max(Geometry.MinBoardSize, Math.Min(Geometry.MaxBoardSize, width));
            height = Math.Max(Geometry.MinBoardSize, Math.Min(Geometry.MaxBoardSize, height));
            if (height > width * Geometry.MaxBoardAspect)
            {
                if (widthChanged) width = height / Geometry.MaxBoardAspect;
                else height = width * Geometry.MaxBoardAspect;
            }

            if (Math.Abs(width - _board.Width) < 1e-9 && Math.Abs(height - _board.Height) < 1e-9) return;

            EndTextEdit();
            PushUndo();
            _board = new Rect(_board.X, _board.Y, width, height);
            FollowBoardWhileEmpty();
            Raise(BoardChanged);
            FitToContent();
            Changed();
        }

        /// <summary>図形を選んでいるか（「削除」のボタンを押せるか）。</summary>
        public bool HasSelection
        {
            get { return _selection.Count > 0; }
        }

        /// <summary>選んでいる図形を消す（「削除」のボタン。Delete キーと同じ）。打っている文字があれば先に決める。</summary>
        public void DeleteSelected()
        {
            EndTextEdit();
            DeleteSelection();
        }

        /// <summary>打っている文字を確定する（保存の前に呼ぶ）。</summary>
        public void CommitText()
        {
            EndTextEdit();
        }

        /// <summary>ボードがちょうど見えるように、拡大と位置を合わせる（開いたとき・ボードの大きさを変えたとき）。</summary>
        public void FitToContent()
        {
            if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0) return;

            var fit = Math.Min((_surface.ActualWidth - 60) / (_board.Width * Geometry.PxPerMm),
                               (_surface.ActualHeight - 60) / (_board.Height * Geometry.PxPerMm));
            _zoom = double.IsNaN(fit) || double.IsInfinity(fit) || fit <= 0 ? 1 : fit;

            // ボードを真ん中に。
            _originX = _board.Left + _board.Width / 2 - _surface.ActualWidth / 2 / Scale;
            _originY = _board.Top + _board.Height / 2 - _surface.ActualHeight / 2 / Scale;

            Refresh();
            Raise(ZoomChanged);
        }

        /// <summary>選んでいる図形の説明（右の欄の見出し）。</summary>
        public string SelectionTitle
        {
            get
            {
                if (_selection.Count == 0) return "図形を選ぶと、位置と大きさを mm で変えられます";
                if (_selection.Count > 1) return _selection.Count + " 個を選んでいます";

                switch (_selection[0].Type)
                {
                    case Kinds.Line: return "直線";
                    case Kinds.Rect: return "四角";
                    case Kinds.Ellipse: return "丸";
                    case Kinds.Text: return "文字";
                    case Kinds.Dimension: return "寸法線";
                    case Kinds.Image: return "画像";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>右の欄に出す寸法。1つだけ選んでいるときだけ。</summary>
        public List<Field> Fields()
        {
            var fields = new List<Field>();
            if (_selection.Count != 1) return fields;

            var shape = _selection[0];
            if (shape.Type == Kinds.Image)
            {
                var r = Geometry.Bounds(shape, Unit);
                fields.Add(MakeField("x", "X", r.Left));
                fields.Add(MakeField("y", "Y", r.Top));
                fields.Add(MakeField("w", "幅", r.Width));
                fields.Add(MakeField("h", "高さ", r.Height));
            }
            else if (shape.IsBox)
            {
                var r = Geometry.Bounds(shape, Unit);
                fields.Add(MakeField("lw", "線の太さ", Geometry.LineWidthOf(shape)));
                fields.Add(MakeField("x", "X", r.Left));
                fields.Add(MakeField("y", "Y", r.Top));
                fields.Add(MakeField("w", "幅", r.Width));
                fields.Add(MakeField("h", "高さ", r.Height));
                fields.Add(MakeField("size", "文字（pt）", Renderer.LabelSize(shape)));
            }
            else if (shape.Type == Kinds.Line && shape.WaypointCount > 0)
            {
                // 折れ線は、長さ・角度が1つに決まらないので、位置だけ（線ごと動かす）。
                fields.Add(MakeField("x", "始点 X", shape.X1));
                fields.Add(MakeField("y", "始点 Y", shape.Y1));
                fields.Add(MakeField("lw", "線の太さ", Geometry.LineWidthOf(shape)));
            }
            else if (shape.IsSegment)
            {
                var delta = shape.End - shape.Start;
                fields.Add(MakeField("x", "始点 X", shape.X1));
                fields.Add(MakeField("y", "始点 Y", shape.Y1));
                fields.Add(MakeField("len", "長さ", delta.Length));
                fields.Add(MakeField("ang", "角度（°）", AngleOf(delta)));
                fields.Add(MakeField("lw", "線の太さ", Geometry.LineWidthOf(shape)));
                if (shape.Type == Kinds.Dimension)
                {
                    fields.Add(MakeField("off", "離れ", shape.Offset));
                    fields.Add(MakeField("size", "数値（pt）", shape.Size > 0 ? shape.Size : Geometry.DimensionTextSize));
                }
            }
            else if (shape.Type == Kinds.Text)
            {
                fields.Add(MakeField("x", "X", shape.X1));
                fields.Add(MakeField("y", "Y", shape.Y1));
                fields.Add(MakeField("size", "大きさ（pt）", shape.Size));
            }

            return fields;
        }

        private static Field MakeField(string key, string label, double value)
        {
            return new Field { Key = key, Label = label, Value = value };
        }

        /// <summary>
        /// 右の欄で打った寸法を入れる。位置（X・Y）は図形ごと動かす。長さ・角度は始点を動かさずに終点を動かす。
        /// 幅・高さは左上を動かさない。
        /// </summary>
        public void SetField(string key, double value)
        {
            if (_selection.Count != 1) return;
            if (!Geometry.IsFinite(value) || Math.Abs(value) > Geometry.MaxFieldValue) return;   // 数でない・大きすぎる値は入れない（2026-10-27）
            var shape = _selection[0];

            PushUndo();

            // つないだ線の位置・長さ・角度を数値で変えたら、動いた端のつながりは外す（外さないと、つないだ所へ引き戻される）。
            if (shape.IsSegment)
            {
                if (key == "x" || key == "y") shape.StartShape = shape.EndShape = null;
                if (key == "len" || key == "ang") shape.EndShape = null;
            }

            switch (key)
            {
                case "x":
                    if (shape.IsFrame) shape.Move(value - Geometry.Bounds(shape, Unit).Left, 0);
                    else shape.Move(value - shape.X1, 0);
                    break;

                case "y":
                    if (shape.IsFrame) shape.Move(0, value - Geometry.Bounds(shape, Unit).Top);
                    else shape.Move(0, value - shape.Y1);
                    break;

                case "w":
                case "h":
                    {
                        var r = Geometry.Bounds(shape, Unit);
                        var size = Math.Max(0.1, value);
                        shape.Start = r.TopLeft;

                        // 画像は、もう一方も変えて縦横の比を保つ（角のつまみと同じ。2026-10-27。前は打った辺だけ変わり、画像が伸びた）。
                        if (shape.Type == Kinds.Image && r.Width > 0 && r.Height > 0)
                        {
                            var ratio = r.Width / r.Height;
                            shape.End = key == "w" ? new Point(r.Left + size, r.Top + size / ratio) : new Point(r.Left + size * ratio, r.Top + size);
                        }
                        else
                        {
                            shape.End = key == "w" ? new Point(r.Left + size, r.Bottom) : new Point(r.Right, r.Top + size);
                        }
                    }
                    break;

                case "len":
                case "ang":
                    {
                        var delta = shape.End - shape.Start;
                        var length = key == "len" ? Math.Max(0.1, value) : delta.Length;
                        var angle = (key == "ang" ? value : AngleOf(delta)) * Math.PI / 180;
                        shape.End = shape.Start + new Vector(Math.Cos(angle), -Math.Sin(angle)) * length;
                    }
                    break;

                case "off":
                    shape.Offset = value;
                    break;

                case "size":
                    shape.Size = Math.Max(4, Math.Min(200, value));
                    break;

                case "lw":
                    shape.LineWidth = Math.Max(0.05, Math.Min(10, value));
                    break;
            }

            Changed();
        }

        /// <summary>選んでいる中に四角・丸があるか（右の欄に塗りの色を出すか）。2つ以上選んでいてもよい。</summary>
        public bool CanFill
        {
            get { return _selection.Any(shape => shape.IsBox); }
        }

        /// <summary>選んでいる四角・丸の塗り。色がそろっていなければ空の文字（どの色見本も押した見た目にしない）。</summary>
        public string SelectionFill
        {
            get
            {
                var fills = _selection.Where(shape => shape.IsBox).Select(shape => shape.Fill).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return fills.Count == 1 ? fills[0] : string.Empty;
            }
        }

        /// <summary>選んでいる四角・丸を塗る（null で塗らない）。直線・文字・寸法線は選んでいても変えない。</summary>
        public void SetFill(string fill)
        {
            var boxes = _selection.Where(shape => shape.IsBox).ToList();
            if (boxes.All(shape => string.Equals(shape.Fill, fill, StringComparison.OrdinalIgnoreCase))) return;   // 履歴を増やさない

            EndTextEdit();
            PushUndo();
            foreach (var shape in boxes) shape.Fill = fill;
            Changed();
        }

        /// <summary>角度（度）。右が 0、反時計回りが正（画面の y は下向きなので、y を反転して測る）。</summary>
        private static double AngleOf(Vector delta)
        {
            if (delta.Length < 1e-9) return 0;
            var angle = Math.Atan2(-delta.Y, delta.X) * 180 / Math.PI;
            return Math.Abs(angle) < 1e-9 ? 0 : angle;
        }

        // =====================================================================
        // 座標
        // =====================================================================

        /// <summary>1mm の画面の大きさ（拡大込み）。</summary>
        private double Scale
        {
            get { return Geometry.PxPerMm * _zoom; }
        }

        /// <summary>ボードがちょうど見える拡大。拡大・縮小はこれの 1/4 〜 8 倍まで（ボードが大きくても小さくても同じ操作感に）。</summary>
        private double FitZoom
        {
            get
            {
                if (_surface.ActualWidth <= 0) return 1;
                var fit = Math.Min((_surface.ActualWidth - 60) / (_board.Width * Geometry.PxPerMm),
                                   (_surface.ActualHeight - 60) / (_board.Height * Geometry.PxPerMm));
                return fit > 0 && !double.IsInfinity(fit) ? fit : 1;
            }
        }

        private Point ToScreen(Point model)
        {
            return new Point((model.X - _originX) * Scale, (model.Y - _originY) * Scale);
        }

        private Point ToModel(Point screen)
        {
            return new Point(screen.X / Scale + _originX, screen.Y / Scale + _originY);
        }

        private Rect ToScreen(Rect model)
        {
            return new Rect(ToScreen(model.TopLeft), ToScreen(model.BottomRight));
        }

        // =====================================================================
        // 描く
        // =====================================================================

        private void Refresh()
        {
            PlaceTextBox();
            _surface.InvalidateVisual();
        }

        private void Render(DrawingContext dc)
        {
            var width = _surface.ActualWidth;
            var height = _surface.ActualHeight;

            // ボードの外は少し暗い灰色（書き出さない所）。ボードは白い紙に方眼。
            dc.DrawRectangle(Outside, null, new Rect(0, 0, width, height));
            var board = ToScreen(_board);
            dc.DrawRectangle(Paper, null, board);
            dc.PushClip(new RectangleGeometry(board));
            DrawGrid(dc, width, height);
            dc.Pop();
            dc.DrawRectangle(null, BoardPen, board);

            // ボードのつまみ（「選ぶ」のとき）。ドラッグで大きさを変える。図形のつまみと見分けるよう、灰色。
            if (_tool == Tool.Select && _textShape == null)
            {
                foreach (var handle in BoardHandles())
                    dc.DrawRectangle(Paper, BoardPen, new Rect(handle.X - HandleSize / 2 + 1, handle.Y - HandleSize / 2 + 1, HandleSize - 2, HandleSize - 2));
            }

            // 図形（mm で描き、拡大を掛ける）。
            dc.PushTransform(new MatrixTransform(Scale, 0, 0, Scale, -_originX * Scale, -_originY * Scale));
            Renderer.Draw(dc, _shapes, Ink, _textShape, Unit);
            if (_drawing != null) Renderer.DrawShape(dc, _drawing, Ink, InkPen(), Unit);
            dc.Pop();

            // 選んでいる図形。2つ以上なら破線の枠、1つならつまみ。
            if (_selection.Count > 1)
            {
                foreach (var shape in _selection) dc.DrawRectangle(null, DashedAccent, Inflate(ToScreen(Geometry.Bounds(shape, Unit)), 3));
            }
            else if (_selection.Count == 1 && _textShape == null)
            {
                var shape = _selection[0];
                if (!shape.IsSegment) dc.DrawRectangle(null, AccentPen, Inflate(ToScreen(Geometry.Bounds(shape, Unit)), shape.Type == Kinds.Text ? 3 : 0));

                foreach (var handle in Handles(shape))
                {
                    var rect = new Rect(handle.X - HandleSize / 2, handle.Y - HandleSize / 2, HandleSize, HandleSize);
                    if (shape.IsSegment) dc.DrawEllipse(Paper, AccentPen, handle, HandleSize / 2, HandleSize / 2);
                    else dc.DrawRectangle(Paper, AccentPen, rect);
                }
            }

            // 範囲で選んでいる四角。
            if (_gesture == Gesture.Rubber)
            {
                dc.DrawRectangle(AccentFill, AccentPen, new Rect(_downScreen, Mouse.GetPosition(_surface)));
            }

            // 吸い付いた所の目印。
            foreach (var x in _guidesX)
            {
                var sx = ToScreen(new Point(x, 0)).X;
                dc.DrawLine(GuidePen, new Point(sx, 0), new Point(sx, height));
            }
            foreach (var y in _guidesY)
            {
                var sy = ToScreen(new Point(0, y)).Y;
                dc.DrawLine(GuidePen, new Point(0, sy), new Point(width, sy));
            }
            if (_snapMark.HasValue)
            {
                var mark = ToScreen(_snapMark.Value);
                dc.DrawRectangle(null, new Pen(GuidePen.Brush, 1.5), new Rect(mark.X - 5, mark.Y - 5, 10, 10));
            }

            if (_drawing != null) DrawMeasure(dc, width, height);
            if (IsDroppingOutside) DrawCursorLabel(dc, "離すと、削除するか尋ねます", width, height);
        }

        /// <summary>
        /// ボードの中でつかんだ図形を、ボードの外（灰色の所）で離そうとしているか。離すと削除するか尋ねる（2026-10-20）。
        /// ボードの外は、見せたくない図形を置いておく場所にもなるので、外でつかんで外で離す（置き直す）ときは尋ねない。
        /// </summary>
        private bool IsDroppingOutside
        {
            get
            {
                return _gesture == Gesture.Move && _undoPushed && _board.Contains(_downModel)
                    && !_board.Contains(ToModel(Mouse.GetPosition(_surface)));
            }
        }

        /// <summary>
        /// 描いている途中の大きさを、カーソルの右上に出す（CAD と同じ。2026-10-20）。
        /// 直線・寸法線は長さと角度、四角・丸は幅 × 高さ。数値は寸法線と同じ書き方（図の mm）。
        /// 拡大しても読める大きさのまま（画面の px で描く）。上にはみ出すならカーソルの右下に出す。
        /// </summary>
        private void DrawMeasure(DrawingContext dc, double width, double height)
        {
            string label;
            if (_drawing.IsSegment)
            {
                var delta = _drawing.End - _drawing.Start;
                label = Geometry.FormatLength(delta.Length) + " mm   " + Geometry.FormatLength(AngleOf(delta)) + "°";
            }
            else
            {
                var r = new Rect(_drawing.Start, _drawing.End);
                label = Geometry.FormatLength(r.Width) + " × " + Geometry.FormatLength(r.Height) + " mm";
            }

            DrawCursorLabel(dc, label, width, height);
        }

        /// <summary>カーソルの右上に出す小さな札（描いている大きさ・ボードの外で離すと消すこと）。</summary>
        private void DrawCursorLabel(DrawingContext dc, string label, double width, double height)
        {
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Geometry.Face, 12, Ink, 1.0);
            var cursor = Mouse.GetPosition(_surface);
            var box = new Rect(0, 0, text.Width + 10, text.Height + 4);

            box.X = Math.Min(cursor.X + 14, width - box.Width - 2);
            box.Y = cursor.Y - 14 - box.Height;
            if (box.Y < 2) box.Y = cursor.Y + 20;
            box.Y = Math.Min(box.Y, height - box.Height - 2);

            dc.DrawRoundedRectangle(Paper, AccentPen, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 5, box.Y + 2));
        }

        /// <summary>
        /// うすい方眼。間隔は 1・2・5・10・20・50…mm のうち、画面で 8px 以上になる一番細かいもの（実寸で描くので、大きな図では粗くなる）。
        /// 5 つか 2 つおきに少し濃くする。
        /// </summary>
        private void DrawGrid(DrawingContext dc, double width, double height)
        {
            var step = 1.0;
            var multipliers = new[] { 2.0, 2.5, 2.0 };   // 1 → 2 → 5 → 10 → 20 → 50 …
            for (var i = 0; step * Scale < 8 && i < 60; i++) step *= multipliers[i % 3];

            // 線の数に限りを付ける（2026-10-27）。前は「画面の端を越えるまで」だけで回し、拡大・位置が数でない値（NaN）になったり、
            // 位置の値が大きすぎて x += step で x が変わらなくなったりすると、終わらずにアプリが固まった。
            // 画面の幅 ÷ 8px 本あれば足りるので、それより十分多い本数で打ち切る。位置は足していかず、startX + n × step で出す。
            const int MaxLines = 4000;
            if (!Geometry.IsFinite(step) || step <= 0 || !Geometry.IsFinite(Scale) || !Geometry.IsFinite(_originX) || !Geometry.IsFinite(_originY)) return;

            var startX = Math.Floor(_originX / step) * step;
            for (var n = 0; n < MaxLines; n++)
            {
                var x = startX + n * step;
                var sx = Math.Round(ToScreen(new Point(x, 0)).X);
                if (!(sx <= width)) break;   // NaN でも抜ける
                var major = Math.Abs(Math.IEEERemainder(x, MajorStep(step))) < 1e-6;
                dc.DrawRectangle(major ? GridMajor : GridMinor, null, new Rect(sx, 0, 1, height));
            }

            var startY = Math.Floor(_originY / step) * step;
            for (var n = 0; n < MaxLines; n++)
            {
                var y = startY + n * step;
                var sy = Math.Round(ToScreen(new Point(0, y)).Y);
                if (!(sy <= height)) break;
                var major = Math.Abs(Math.IEEERemainder(y, MajorStep(step))) < 1e-6;
                dc.DrawRectangle(major ? GridMajor : GridMinor, null, new Rect(0, sy, width, 1));
            }
        }

        /// <summary>濃い線の間隔。1・10… は 5 つおき、2・20… は 5 つおき、5・50… は 2 つおき。</summary>
        private static double MajorStep(double step)
        {
            var lead = step / Math.Pow(10, Math.Floor(Math.Log10(step) + 1e-9));
            return Math.Abs(lead - 5) < 1e-6 ? step * 2 : step * 5;
        }

        private static Rect Inflate(Rect rect, double amount)
        {
            rect.Inflate(amount, amount);
            return rect;
        }

        /// <summary>
        /// つまみの位置（画面）。四角・丸は 8つ（左上から時計回り: 左上・上・右上・右・右下・下・左下・左）、
        /// 直線は両端と折れ目、寸法線は両端と寸法線の真ん中（離れを変える）。文字は無し（動かすだけ）。
        /// </summary>
        private List<Point> Handles(Shape shape)
        {
            var points = new List<Point>();

            if (shape.IsFrame)
            {
                var r = ToScreen(Geometry.Bounds(shape, Unit));
                var cx = r.Left + r.Width / 2;
                var cy = r.Top + r.Height / 2;
                points.Add(r.TopLeft);
                points.Add(new Point(cx, r.Top));
                points.Add(r.TopRight);
                points.Add(new Point(r.Right, cy));
                points.Add(r.BottomRight);
                points.Add(new Point(cx, r.Bottom));
                points.Add(r.BottomLeft);
                points.Add(new Point(r.Left, cy));
            }
            else if (shape.IsSegment)
            {
                points.Add(ToScreen(shape.Start));
                points.Add(ToScreen(shape.End));

                // 直線の折れ目（2 番から）。
                for (var i = 0; i < shape.WaypointCount; i++) points.Add(ToScreen(shape.GetWaypoint(i)));

                if (shape.Type == Kinds.Dimension)
                {
                    var d = Geometry.Dimension(shape);
                    points.Add(ToScreen(d.LineA + (d.LineB - d.LineA) / 2));
                }
            }

            return points;
        }

        // =====================================================================
        // マウス
        // =====================================================================

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            EndTextEdit();
            Focus();

            var screen = e.GetPosition(_surface);
            var model = ToModel(screen);
            _downScreen = screen;
            _downModel = model;
            _undoPushed = false;

            if (e.ChangedButton == MouseButton.Middle)
            {
                StartGesture(Gesture.Pan);
                _panOriginX = _originX;
                _panOriginY = _originY;
                e.Handled = true;
                return;
            }

            // 図形を右クリックすると、その図形と同じ道具にする（文字なら、新しく文字を置く状態）。何も無い所の右クリックは「選ぶ」に戻す。2026-10-19。
            if (e.ChangedButton == MouseButton.Right)
            {
                if (_gesture != Gesture.None && !_drawWaiting) return;
                var target = HitShape(model);
                CurrentTool = target != null ? ToolOf(target.Type) : Tool.Select;   // 終点を待っていたら、描くのをやめる（CurrentTool が取りやめる）
                e.Handled = true;
                return;
            }

            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;

            // 始点を決めて待っているときの2回目の押下は、終点を決める。
            if (_gesture == Gesture.Draw && _drawWaiting)
            {
                DragDraw(model, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                EndGesture(true);
                return;
            }

            var modifiers = Keyboard.Modifiers;
            var add = (modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;

            switch (_tool)
            {
                case Tool.Select:
                    {
                        // ダブルクリック: 文字を打ち直す。四角・丸なら中の文字を打つ（draw.io と同じ）。
                        // 直線なら、折れ目の上で折れ目を消し、線の途中で折れ目を足す（2026-10-19）。
                        if (e.ClickCount == 2)
                        {
                            if (_gesture != Gesture.None) EndGesture(false);

                            if (_selection.Count == 1 && _selection[0].Type == Kinds.Line)
                            {
                                var waypoint = HitHandle(screen) - 2;
                                if (waypoint >= 0)
                                {
                                    PushUndo();
                                    _selection[0].RemoveWaypoint(waypoint);
                                    Changed();
                                    return;
                                }
                            }

                            var text = HitShape(model);
                            if (text != null && (text.Type == Kinds.Text || text.IsBox)) BeginTextEdit(text, false);
                            else if (text != null && text.Type == Kinds.Line) AddWaypoint(text, model);
                            return;
                        }

                        var handle = HitHandle(screen);
                        if (handle >= 0)
                        {
                            _resizeTarget = _selection[0];
                            _resizeOriginal = _resizeTarget.Clone();
                            _handle = handle;
                            StartGesture(Gesture.Resize);
                            return;
                        }

                        // ボードのつまみ（四隅と辺の真ん中）で、ボードの大きさを変える。
                        var boardHandle = HitBoardHandle(screen);
                        if (boardHandle >= 0)
                        {
                            _handle = boardHandle;
                            _boardOriginal = _board;
                            _unitOriginal = _unit;
                            StartGesture(Gesture.Board);
                            return;
                        }

                        var hit = HitShape(model);
                        if (hit != null)
                        {
                            if (add)
                            {
                                if (_selection.Contains(hit)) _selection.Remove(hit);
                                else _selection.Add(hit);
                                Raise(SelectionChanged);
                            }
                            else if (!_selection.Contains(hit))
                            {
                                SetSelection(new[] { hit });
                            }

                            if (_selection.Contains(hit))
                            {
                                // つかんだ点。図形の端・中点の近くなら、その点（動かした先で、ほかの図形の点に合わせるため）。
                                _moveBase = NearestKeyPoint(model, null) ?? model;
                                _moveOriginals = _selection.Select(shape => shape.Clone()).ToList();
                                StartGesture(Gesture.Move);
                            }

                            Refresh();
                            return;
                        }

                        _additive = add;
                        if (!add) SetSelection(Enumerable.Empty<Shape>());
                        StartGesture(Gesture.Rubber);
                    }
                    break;

                case Tool.Text:
                    {
                        var at = SnapPoint(model, null);
                        ClearGuides();

                        PushUndo();
                        var shape = new Shape { Type = Kinds.Text, Id = Shape.NewId(), X1 = at.X, Y1 = at.Y, X2 = at.X, Y2 = at.Y, Size = Geometry.DefaultTextSize, Text = string.Empty };
                        _shapes.Add(shape);
                        SetSelection(new[] { shape });
                        CurrentTool = Tool.Select;   // 置いたら「選ぶ」に戻る
                        BeginTextEdit(shape, true);
                    }
                    break;

                default:
                    {
                        _drawStart = SnapPoint(model, null);
                        _drawStartOwner = _snapOwner;
                        _drawStartU = _snapU;
                        _drawStartV = _snapV;
                        _drawEndOwner = null;
                        _drawWaiting = false;
                        _drawing = new Shape { Type = KindOf(_tool), X1 = _drawStart.X, Y1 = _drawStart.Y, X2 = _drawStart.X, Y2 = _drawStart.Y };
                        if (_tool == Tool.Dimension)
                        {
                            _drawing.Offset = Geometry.DefaultOffset * Unit;
                            _drawing.Size = Geometry.DimensionTextSize;
                        }
                        StartGesture(Gesture.Draw);
                    }
                    break;
            }

            Refresh();
        }

        private static Tool ToolOf(string type)
        {
            switch (type)
            {
                case Kinds.Line: return Tool.Line;
                case Kinds.Rect: return Tool.Rect;
                case Kinds.Ellipse: return Tool.Ellipse;
                case Kinds.Dimension: return Tool.Dimension;
                case Kinds.Text: return Tool.Text;
                default: return Tool.Select;   // 画像（描く道具が無い）は「選ぶ」（2026-10-27。前は「文字」になった）
            }
        }

        private static string KindOf(Tool tool)
        {
            switch (tool)
            {
                case Tool.Line: return Kinds.Line;
                case Tool.Rect: return Kinds.Rect;
                case Tool.Ellipse: return Kinds.Ellipse;
                case Tool.Dimension: return Kinds.Dimension;
                default: return Kinds.Text;
            }
        }

        private void StartGesture(Gesture gesture)
        {
            _gesture = gesture;
            _surface.CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var screen = e.GetPosition(_surface);
            var model = ToModel(screen);

            if (_gesture == Gesture.None)
            {
                UpdateCursor(screen, model);

                // つかんでいないときも、カーソルが吸い付く点に目印を出す（どの道具のときも）。
                var previous = _snapMark;
                ClearGuides();
                _snapMark = NearestKeyPoint(model, null);
                if (previous != _snapMark) _surface.InvalidateVisual();
                return;
            }

            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (_gesture)
            {
                case Gesture.Pan:
                    _originX = _panOriginX - (screen.X - _downScreen.X) / Scale;
                    _originY = _panOriginY - (screen.Y - _downScreen.Y) / Scale;
                    break;

                case Gesture.Draw:
                    DragDraw(model, shift);
                    break;

                case Gesture.Move:
                    DragMove(model, shift);
                    break;

                case Gesture.Resize:
                    DragResize(model, shift);
                    break;

                case Gesture.Board:
                    DragBoard(model);
                    break;

                case Gesture.Rubber:
                    break;
            }

            if (_gesture == Gesture.Move || _gesture == Gesture.Resize) UpdateConnections();
            Refresh();
            if (_gesture == Gesture.Move || _gesture == Gesture.Resize) Raise(ShapesChanged);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_gesture == Gesture.None) return;
            e.Handled = true;

            // 押してすぐ離したら、始点だけ決めて終点を待つ（AutoCAD と同じ。カーソルを動かすと形が付いてくる）。
            // 押したまま動かして離したら、そこで決める（ドラッグで描く）。
            if (_gesture == Gesture.Draw && !_drawWaiting && (e.GetPosition(_surface) - _downScreen).Length < 4)
            {
                // 待つ間はマウスをつかまない。つかんだままだと、道具のボタンを押しても、ここで終点を決めてしまう。
                _drawWaiting = true;
                _surface.ReleaseMouseCapture();
                return;
            }

            if (_gesture == Gesture.Draw && _drawWaiting) return;   // 終点は次に押したときに決める

            if (IsDroppingOutside)
            {
                ConfirmDropDelete();
                return;
            }

            EndGesture(true);
        }

        /// <summary>
        /// ボードの外で離した図形を消すか尋ねる（2026-10-20）。すぐには消さない。動かすのはいつもどおりで、尋ねるのが足されるだけ。
        /// 「いいえ」なら離した所（ボードの外）に置いたまま。
        /// 「はい」なら動かした分を取り消してから消す（元に戻すの履歴を「動かした → 消した」の2段にしない。Ctrl+Z でボードの中の元の位置に戻る）。
        /// </summary>
        private void ConfirmDropDelete()
        {
            var originals = _moveOriginals;
            var moved = _selection.ToList();
            EndGesture(true);   // ダイアログがマウスを取るので、先につかむのを終えておく

            var answer = MessageBox.Show(Window.GetWindow(this),
                moved.Count == 1 ? "この図形を削除しますか？" : moved.Count + " 個の図形を削除しますか？",
                "図エディタ", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

            if (answer == MessageBoxResult.Yes)
            {
                for (var i = 0; i < moved.Count && i < originals.Count; i++) Copy(originals[i], moved[i]);
                if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
                DeleteSelection();
            }

            Focus();
        }

        /// <summary>つかむのを終える。commit なら描いた・範囲で選んだものを決める（false なら取りやめる）。</summary>
        private void EndGesture(bool commit)
        {
            var gesture = _gesture;
            if (gesture == Gesture.None) return;
            _gesture = Gesture.None;

            if (commit && gesture == Gesture.Draw && _drawing != null)
            {
                var drawn = _drawing;
                _drawing = null;

                if (IsBigEnough(drawn))
                {
                    drawn.Id = Shape.NewId();

                    // 線の端を図形の点に吸い付けて描いたら、その図形につなぐ（図形を動かすと付いてくる）。
                    if (drawn.IsSegment)
                    {
                        Attach(drawn, true, _drawStartOwner, _drawStartU, _drawStartV);
                        Attach(drawn, false, _drawEndOwner, _drawEndU, _drawEndV);
                    }

                    PushUndo();
                    _shapes.Add(drawn);
                    SetSelection(new[] { drawn });   // 描いた図形を選んでおく（続けて右の欄で長さなどを打てる）
                    CurrentTool = Tool.Select;       // 描いたら「選ぶ」に戻る（2026-10-19）
                    Changed();
                }
            }

            if (commit && gesture == Gesture.Rubber)
            {
                var rect = new Rect(ToModel(_downScreen), ToModel(Mouse.GetPosition(_surface)));
                var inside = _shapes.Where(shape => rect.Contains(Geometry.Bounds(shape, Unit))).ToList();
                SetSelection(_additive ? _selection.Union(inside).ToList() : inside);
            }

            _drawing = null;
            _drawWaiting = false;
            _moveOriginals = null;
            _resizeTarget = null;
            ClearGuides();

            if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
            Refresh();
            Raise(ShapesChanged);
        }

        /// <summary>描いた図形が小さすぎないか（押しただけで描いてしまわないように）。大きさは紙の上の mm で見る。</summary>
        private bool IsBigEnough(Shape shape)
        {
            if (shape.IsSegment) return (shape.End - shape.Start).Length >= 1 * Unit;
            var r = Geometry.Bounds(shape, Unit);
            return r.Width >= 0.5 * Unit && r.Height >= 0.5 * Unit;
        }

        private void UpdateCursor(Point screen, Point model)
        {
            if (_tool != Tool.Select)
            {
                _surface.Cursor = _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
                return;
            }

            var handle = HitHandle(screen);
            if (handle >= 0)
            {
                var shape = _selection[0];
                if (shape.IsFrame)
                {
                    _surface.Cursor = SizeCursors[handle];
                }
                else
                {
                    _surface.Cursor = Cursors.Cross;
                }
                return;
            }

            var boardHandle = HitBoardHandle(screen);
            if (boardHandle >= 0)
            {
                _surface.Cursor = SizeCursors[boardHandle];
                return;
            }

            _surface.Cursor = HitShape(model) != null ? Cursors.SizeAll : null;
        }

        /// <summary>
        /// 描いている図形の終わりを、カーソルの所にする。図形の端・中点に吸い付いたら、その点を優先する（線をつなげられるように）。
        /// そうでなければ、直線は 22.5° ごと（Shift で自由な角度）、寸法線は Shift で水平・垂直、四角・丸は Shift で正方形・正円。
        /// </summary>
        private void DragDraw(Point model, bool shift)
        {
            _drawing.End = ConstrainEnd(_drawing.Type, _drawStart, model, shift, null);
            _drawEndOwner = _snapMark.HasValue ? _snapOwner : null;
            _drawEndU = _snapU;
            _drawEndV = _snapV;

            if (!shift || _drawing.IsSegment || _snapMark.HasValue) return;

            var delta = _drawing.End - _drawStart;
            var size = Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
            _drawing.End = new Point(_drawStart.X + Math.Sign(delta.X == 0 ? 1 : delta.X) * size,
                                     _drawStart.Y + Math.Sign(delta.Y == 0 ? 1 : delta.Y) * size);
            ClearGuides();
        }

        /// <summary>
        /// 直線・寸法線・四角・丸の終わりの点（start が動かない側）。吸い付き・角度のそろえ方は DragDraw の説明のとおり。
        /// 直線は、延ばした先がほかの線とぶつかる点にも吸い付く（向きは 22.5° ごとにそろえた向き。Shift なら押さえている向き）。
        /// </summary>
        private Point ConstrainEnd(string type, Point start, Point model, bool shift, ICollection<Shape> exclude)
        {
            ClearGuides();

            var crossings = type == Kinds.Line
                ? Crossings(start, shift ? model - start : ConstrainAngle(model - start), exclude)
                : null;

            var snapped = NearestKeyPoint(model, exclude, crossings);
            if (snapped.HasValue)
            {
                _snapMark = snapped;   // 端・中点・交点に吸い付いた
                return snapped.Value;
            }

            var end = SnapPoint(model, exclude);

            if (type == Kinds.Line && !shift)
            {
                ClearGuides();
                return start + ConstrainAngle(model - start);
            }

            if (type == Kinds.Dimension && shift)
            {
                ClearGuides();
                var delta = model - start;
                return Math.Abs(delta.X) >= Math.Abs(delta.Y) ? new Point(model.X, start.Y) : new Point(start.X, model.Y);
            }

            return end;
        }

        /// <summary>直線の向きをそろえる刻み（22.5°。2026-10-18。前は 45° で、角度がきつかった）。</summary>
        private const double AngleStep = Math.PI / 8;

        /// <summary>22.5° ごとの向きにそろえる（長さはそのまま）。</summary>
        private static Vector ConstrainAngle(Vector delta)
        {
            var length = delta.Length;
            if (length < 1e-9) return delta;

            var angle = Math.Round(Math.Atan2(delta.Y, delta.X) / AngleStep) * AngleStep;
            return new Vector(Math.Cos(angle), Math.Sin(angle)) * length;
        }

        /// <summary>選んでいる図形を動かす。枠の辺・真ん中が、ほかの図形にそろうように吸い付く。Shift で縦か横だけ。</summary>
        private void DragMove(Point model, bool shift)
        {
            Vector delta;

            // つかんだ点が、ほかの図形の端・中点に吸い付くなら、そこへ合わせる（AutoCAD の基点と同じ）。
            var target = shift ? null : NearestKeyPoint(model + (_moveBase - _downModel), _selection);
            if (target.HasValue)
            {
                ClearGuides();
                _snapMark = target;
                delta = target.Value - _moveBase;
            }
            else
            {
                delta = model - _downModel;
                if (shift)
                {
                    if (Math.Abs(delta.X) >= Math.Abs(delta.Y)) delta.Y = 0;
                    else delta.X = 0;
                }

                var bounds = Rect.Empty;
                foreach (var original in _moveOriginals) bounds.Union(Geometry.Bounds(original, Unit));
                bounds.Offset(delta);

                var snap = SnapBox(bounds, _selection);
                if (shift)
                {
                    if (delta.Y == 0) snap.Y = 0;
                    else snap.X = 0;
                }
                delta += snap;
            }

            if (delta.Length > 0 && !_undoPushed)
            {
                PushUndoOnce(_moveOriginals);
                DetachFromOutside(_selection);   // 線そのものを動かしたら、選んでいない図形とのつながりは外す（Excel のコネクタと同じ）
            }

            for (var i = 0; i < _selection.Count && i < _moveOriginals.Count; i++)
                _selection[i].MoveFrom(_moveOriginals[i], delta.X, delta.Y);   // 折れ目も一緒に動かす
        }

        /// <summary>つまみで大きさ・端・離れを変える。</summary>
        private void DragResize(Point model, bool shift)
        {
            var target = _resizeTarget;
            var original = _resizeOriginal;
            var exclude = new[] { target };

            if (!_undoPushed) PushUndoOnce(null);

            if (target.IsFrame)
            {
                var r = Geometry.Bounds(original, Unit);
                double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

                var movesLeft = _handle == 0 || _handle == 6 || _handle == 7;
                var movesRight = _handle == 2 || _handle == 3 || _handle == 4;
                var movesTop = _handle == 0 || _handle == 1 || _handle == 2;
                var movesBottom = _handle == 4 || _handle == 5 || _handle == 6;

                ClearGuides();
                var x = model.X;
                var y = model.Y;
                if (movesLeft || movesRight) x = SnapAxis(x, CandidatesX(exclude), _guidesX);
                if (movesTop || movesBottom) y = SnapAxis(y, CandidatesY(exclude), _guidesY);

                if (movesLeft) left = x;
                if (movesRight) right = x;
                if (movesTop) top = y;
                if (movesBottom) bottom = y;

                // Shift で縦横の比を保つ（角のつまみのとき）。動かさない角を起点にする。
                // 画像は逆で、ふだん比を保ち、Shift で崩せる（Excel と同じ。画像は比を崩さないことが多いため。2026-10-25）。
                var corner = (movesLeft || movesRight) && (movesTop || movesBottom);
                var keepRatio = target.Type == Kinds.Image ? !shift : shift;
                if (keepRatio && corner && r.Width > 0 && r.Height > 0)
                {
                    var ratio = r.Width / r.Height;
                    var width = Math.Abs(right - left);
                    var height = Math.Abs(bottom - top);
                    if (width / ratio >= height) height = width / ratio;
                    else width = height * ratio;

                    if (movesLeft) left = right - Math.Sign(right - left == 0 ? 1 : right - left) * width;
                    else right = left + Math.Sign(right - left == 0 ? 1 : right - left) * width;
                    if (movesTop) top = bottom - Math.Sign(bottom - top == 0 ? 1 : bottom - top) * height;
                    else bottom = top + Math.Sign(bottom - top == 0 ? 1 : bottom - top) * height;
                    ClearGuides();
                }

                target.Start = new Point(left, top);
                target.End = new Point(right, bottom);
                return;
            }

            if (target.Type == Kinds.Line && _handle >= 2)
            {
                DragWaypoint(target, original, _handle - 2, model);
                return;
            }

            if (target.IsSegment && _handle <= 1)
            {
                // 動かさない側は、隣の点（折れ目があれば、その折れ目）。角度のそろえ方・延ばした先の交点は、そこから見る。
                var vertices = original.Type == Kinds.Line ? original.Vertices() : new List<Point> { original.Start, original.End };
                var fixedPoint = _handle == 0 ? vertices[1] : vertices[vertices.Count - 2];
                var point = ConstrainEnd(target.Type, fixedPoint, model, shift, exclude);

                if (_handle == 0) target.Start = point;
                else target.End = point;

                // 図形の点に吸い付けたら、その図形につなぐ。吸い付けなければ、つながりを外す。
                Attach(target, _handle == 0, _snapMark.HasValue ? _snapOwner : null, _snapU, _snapV);
                return;
            }

            if (target.Type == Kinds.Dimension && _handle == 2)
            {
                var d = Geometry.Dimension(original);
                target.Offset = Vector.Multiply(model - d.A, d.Normal);
            }
        }

        /// <summary>8つのつまみ（左上から時計回り）の、大きさを変える向きのカーソル。</summary>
        private static readonly Cursor[] SizeCursors =
        {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
        };

        /// <summary>ボードのつまみの位置（画面）。四角と同じ並び（左上から時計回り）。</summary>
        private List<Point> BoardHandles()
        {
            var r = ToScreen(_board);
            var cx = r.Left + r.Width / 2;
            var cy = r.Top + r.Height / 2;
            return new List<Point>
            {
                r.TopLeft, new Point(cx, r.Top), r.TopRight, new Point(r.Right, cy),
                r.BottomRight, new Point(cx, r.Bottom), r.BottomLeft, new Point(r.Left, cy),
            };
        }

        /// <summary>押した所がボードのつまみなら、その番号。「選ぶ」のときだけ。</summary>
        private int HitBoardHandle(Point screen)
        {
            if (_tool != Tool.Select || _textShape != null) return -1;

            var handles = BoardHandles();
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - screen).Length <= HandleSize) return i;
            }

            return -1;
        }

        /// <summary>
        /// ボードのつまみをドラッグして、大きさを変える（2026-10-18）。動かす辺は、ほかの図形の端・枠にそろうように吸い付く（Alt で吸い付かない）。
        /// 10mm より小さくはしない（反対側の辺を越えない）。拡大はドラッグの間は変えない（画面が飛ばないように）。
        /// </summary>
        private void DragBoard(Point model)
        {
            const double Minimum = 10;

            if (!_undoPushed)
            {
                _undoPushed = true;
                var snapshot = Capture();
                snapshot.Board = _boardOriginal;
                snapshot.Unit = _unitOriginal;
                PushSnapshot(snapshot);
            }

            var r = _boardOriginal;
            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

            var movesLeft = _handle == 0 || _handle == 6 || _handle == 7;
            var movesRight = _handle == 2 || _handle == 3 || _handle == 4;
            var movesTop = _handle == 0 || _handle == 1 || _handle == 2;
            var movesBottom = _handle == 4 || _handle == 5 || _handle == 6;

            ClearGuides();
            var x = (movesLeft || movesRight) ? SnapAxis(model.X, CandidatesX(null), _guidesX) : model.X;
            var y = (movesTop || movesBottom) ? SnapAxis(model.Y, CandidatesY(null), _guidesY) : model.Y;

            if (movesLeft) left = Math.Min(x, right - Minimum);
            if (movesRight) right = Math.Max(x, left + Minimum);
            if (movesTop) top = Math.Min(y, bottom - Minimum);
            if (movesBottom) bottom = Math.Max(y, top + Minimum);

            // 大きさの限り（2026-10-27。SetBoardSize と同じ。辺は 100m まで、高さは幅の 10 倍まで）。動かしている辺の側で収める。
            if (right - left > Geometry.MaxBoardSize)
            {
                if (movesLeft) left = right - Geometry.MaxBoardSize;
                else right = left + Geometry.MaxBoardSize;
            }
            if (bottom - top > Geometry.MaxBoardSize)
            {
                if (movesTop) top = bottom - Geometry.MaxBoardSize;
                else bottom = top + Geometry.MaxBoardSize;
            }
            if (bottom - top > (right - left) * Geometry.MaxBoardAspect)
            {
                if (movesTop || movesBottom)
                {
                    var limit = (right - left) * Geometry.MaxBoardAspect;
                    if (movesTop) top = bottom - limit;
                    else bottom = top + limit;
                }
                else
                {
                    var limit = (bottom - top) / Geometry.MaxBoardAspect;
                    if (movesLeft) left = right - limit;
                    else right = left + limit;
                }
            }

            _board = new Rect(left, top, right - left, bottom - top);
            FollowBoardWhileEmpty();
            Raise(BoardChanged);
        }

        /// <summary>直線の、押した所に一番近い区間に、折れ目を足す（押した所を線の上に落とした点）。</summary>
        private void AddWaypoint(Shape line, Point model)
        {
            var points = line.Vertices();
            var best = 0;
            var bestDistance = double.MaxValue;

            for (var i = 1; i < points.Count; i++)
            {
                var distance = Geometry.DistanceToSegment(model, points[i - 1], points[i]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = i - 1;
            }

            var a = points[best];
            var b = points[best + 1];
            var segment = b - a;
            var t = segment.LengthSquared < 1e-12 ? 0.5 : Math.Max(0, Math.Min(1, Vector.Multiply(model - a, segment) / segment.LengthSquared));

            PushUndo();
            line.InsertWaypoint(best, a + segment * t);   // 区間 best の後ろ（始点から数えて best 番目の折れ目）
            SetSelection(new[] { line });
            Changed();
        }

        /// <summary>
        /// 折れ目を動かす。ほかの図形の点に吸い付き、無ければ、同じ線のほかの点（隣の点など）やほかの図形と縦・横にそろう
        /// （手で動かしても、ぴったり直角に折れるように）。Alt で吸い付かない。
        /// </summary>
        private void DragWaypoint(Shape line, Shape original, int index, Point model)
        {
            ClearGuides();
            var exclude = new[] { line };

            var snapped = NearestKeyPoint(model, exclude);
            if (snapped.HasValue)
            {
                _snapMark = snapped;
                line.SetWaypoint(index, snapped.Value);
                return;
            }

            var xs = CandidatesX(exclude);
            var ys = CandidatesY(exclude);
            var own = original.Vertices();
            for (var i = 0; i < own.Count; i++)
            {
                if (i == index + 1) continue;   // 動かしている折れ目そのもの
                xs.Add(own[i].X);
                ys.Add(own[i].Y);
            }

            line.SetWaypoint(index, new Point(SnapAxis(model.X, xs, _guidesX), SnapAxis(model.Y, ys, _guidesY)));
        }

        private int HitHandle(Point screen)
        {
            if (_selection.Count != 1 || _textShape != null) return -1;

            var handles = Handles(_selection[0]);
            for (var i = handles.Count - 1; i >= 0; i--)
            {
                if ((handles[i] - screen).Length <= HandleSize) return i;
            }

            return -1;
        }

        /// <summary>
        /// 押した所の図形。線（直線・寸法線）と文字を先に探し、無ければ四角・丸（どちらも上に描いたものから）。
        /// 四角・丸は中を押しても選べるので、描いた順だけで決めると、先に描いた線が四角に隠れてつかめない。
        /// 線を優先する（draw.io もコネクタが図形より先。2026-10-19）。
        /// </summary>
        private Shape HitShape(Point model)
        {
            var tolerance = HitTolerance / Scale;

            for (var i = _shapes.Count - 1; i >= 0; i--)
            {
                if (!_shapes[i].IsFrame && Geometry.Hits(_shapes[i], model, tolerance, Unit)) return _shapes[i];
            }

            for (var i = _shapes.Count - 1; i >= 0; i--)
            {
                if (_shapes[i].IsFrame && Geometry.Hits(_shapes[i], model, tolerance, Unit)) return _shapes[i];
            }

            return null;
        }

        /// <summary>
        /// ホイールで、カーソルの所を中心に拡大・縮小する（2026-10-18。前は Ctrl+ホイール。CAD と同じく、ホイールだけで）。
        /// 動かすのは真ん中のボタンのドラッグ。Shift+ホイールで左右、Ctrl+ホイールで上下にも動かせる（タッチパッド向け）。
        /// </summary>
        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var screen = e.GetPosition(_surface);
            var modifiers = Keyboard.Modifiers;

            if ((modifiers & ModifierKeys.Shift) != 0)
            {
                _originX -= e.Delta / 3.0 / Scale;
            }
            else if ((modifiers & ModifierKeys.Control) != 0)
            {
                _originY -= e.Delta / 3.0 / Scale;
            }
            else
            {
                var model = ToModel(screen);
                _zoom = Math.Max(MinZoom * FitZoom, Math.Min(MaxZoom * FitZoom, _zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2)));
                _originX = model.X - screen.X / Scale;
                _originY = model.Y - screen.Y / Scale;
                Raise(ZoomChanged);
            }

            Refresh();
            e.Handled = true;
        }

        // =====================================================================
        // 吸い付き
        // =====================================================================

        private void ClearGuides()
        {
            _guidesX.Clear();
            _guidesY.Clear();
            _snapMark = null;
            _snapOwner = null;
        }

        private IEnumerable<Shape> Others(ICollection<Shape> exclude)
        {
            return _shapes.Where(shape => (exclude == null || !exclude.Contains(shape)) && !ReferenceEquals(shape, _textShape));
        }

        /// <summary>横の位置の候補: ほかの図形の枠の左・真ん中・右と、吸い付く点の X。</summary>
        private List<double> CandidatesX(ICollection<Shape> exclude)
        {
            var values = new List<double>();
            foreach (var shape in Others(exclude))
            {
                var r = Geometry.Bounds(shape, Unit);
                values.Add(r.Left);
                values.Add(r.Left + r.Width / 2);
                values.Add(r.Right);
                foreach (var point in Geometry.KeyPoints(shape)) values.Add(point.X);
            }

            return values;
        }

        private List<double> CandidatesY(ICollection<Shape> exclude)
        {
            var values = new List<double>();
            foreach (var shape in Others(exclude))
            {
                var r = Geometry.Bounds(shape, Unit);
                values.Add(r.Top);
                values.Add(r.Top + r.Height / 2);
                values.Add(r.Bottom);
                foreach (var point in Geometry.KeyPoints(shape)) values.Add(point.Y);
            }

            return values;
        }

        private bool SnapEnabled
        {
            get { return (Keyboard.Modifiers & ModifierKeys.Alt) == 0; }
        }

        /// <summary>
        /// カーソルの近く（画面で SnapDistance 以内）にある、図形の端・中点・図形どうしの交点（直線と四角の辺）。
        /// extra があれば、それも候補に入れる（伸ばした線の交点など）。無い・Alt を押しているなら null。
        /// </summary>
        private Point? NearestKeyPoint(Point point, ICollection<Shape> exclude, IEnumerable<Point> extra = null)
        {
            if (!SnapEnabled) return null;

            var tolerance = SnapDistance / Scale;
            Candidate? best = null;
            var bestDistance = tolerance;

            foreach (var candidate in SnapCandidates(exclude, extra))
            {
                var distance = (candidate.Point - point).Length;
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = candidate;
            }

            // 吸い付いた点の持ち主（線をつなぐ相手）も覚えておく。交点などは持ち主が無い。
            _snapOwner = best.HasValue ? best.Value.Owner : null;
            _snapU = best.HasValue ? best.Value.U : 0;
            _snapV = best.HasValue ? best.Value.V : 0;

            return best.HasValue ? best.Value.Point : (Point?)null;
        }

        /// <summary>吸い付く点の候補。持ち主の図形の点なら、その図形と位置（つなぐため）。</summary>
        private struct Candidate
        {
            public Point Point;
            public Shape Owner;
            public double U;
            public double V;
        }

        /// <summary>吸い付く点の候補: 図形の端・中点と、図形どうしの交点（2026-10-18）。</summary>
        private IEnumerable<Candidate> SnapCandidates(ICollection<Shape> exclude, IEnumerable<Point> extra)
        {
            var others = Others(exclude).ToList();
            foreach (var shape in others)
            {
                foreach (var anchor in Geometry.Anchors(shape))
                    yield return new Candidate { Point = anchor.Point, Owner = shape, U = anchor.U, V = anchor.V };
            }

            var segments = others.SelectMany(Geometry.Segments).ToList();
            for (var i = 0; i < segments.Count; i++)
            {
                for (var j = i + 1; j < segments.Count; j++)
                {
                    Point crossing;
                    if (Geometry.Intersect(segments[i].Key, segments[i].Value, segments[j].Key, segments[j].Value, false, out crossing))
                        yield return new Candidate { Point = crossing };
                }
            }

            if (extra == null) yield break;
            foreach (var point in extra) yield return new Candidate { Point = point };
        }

        /// <summary>
        /// start から direction の向きに延ばした直線が、ほかの図形の直線の部分とぶつかる点。
        /// 線を描く・線の端を動かすとき、ほかの線の所まで縮める・伸ばすのに使う（画像のような交点にも吸い付く）。
        /// </summary>
        private List<Point> Crossings(Point start, Vector direction, ICollection<Shape> exclude)
        {
            var points = new List<Point>();
            if (direction.LengthSquared < 1e-12) return points;

            foreach (var segment in Others(exclude).SelectMany(Geometry.Segments))
            {
                Point crossing;
                if (Geometry.Intersect(start, start + direction, segment.Key, segment.Value, true, out crossing)) points.Add(crossing);
            }

            return points;
        }

        /// <summary>
        /// 点を吸い付ける。近くにほかの図形の端・中点があればそこへ（目印の四角）。
        /// 無ければ、縦・横それぞれで、枠の辺・真ん中や点の位置にそろえる（目印の線）。
        /// </summary>
        private Point SnapPoint(Point point, ICollection<Shape> exclude)
        {
            ClearGuides();
            if (!SnapEnabled) return point;

            var best = NearestKeyPoint(point, exclude);
            if (best.HasValue)
            {
                _snapMark = best;
                return best.Value;
            }

            return new Point(SnapAxis(point.X, CandidatesX(exclude), _guidesX), SnapAxis(point.Y, CandidatesY(exclude), _guidesY));
        }

        /// <summary>1つの向きの値を、近い候補にそろえる。そろえたら目印に足す。</summary>
        private double SnapAxis(double value, List<double> candidates, List<double> guides)
        {
            if (!SnapEnabled) return value;

            var tolerance = SnapDistance / Scale;
            var best = value;
            var bestDistance = tolerance;
            var found = false;

            foreach (var candidate in candidates)
            {
                var distance = Math.Abs(candidate - value);
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = candidate;
                found = true;
            }

            if (found) guides.Add(best);
            return best;
        }

        /// <summary>動かしている枠の左・真ん中・右（上・真ん中・下）のどれかが、候補にそろうだけずらす量。</summary>
        private Vector SnapBox(Rect bounds, ICollection<Shape> exclude)
        {
            ClearGuides();
            if (!SnapEnabled) return new Vector();

            var tolerance = SnapDistance / Scale;
            return new Vector(
                SnapEdges(new[] { bounds.Left, bounds.Left + bounds.Width / 2, bounds.Right }, CandidatesX(exclude), tolerance, _guidesX),
                SnapEdges(new[] { bounds.Top, bounds.Top + bounds.Height / 2, bounds.Bottom }, CandidatesY(exclude), tolerance, _guidesY));
        }

        private static double SnapEdges(double[] edges, List<double> candidates, double tolerance, List<double> guides)
        {
            var bestShift = 0.0;
            var bestDistance = tolerance;
            double? guide = null;

            foreach (var edge in edges)
            {
                foreach (var candidate in candidates)
                {
                    var distance = Math.Abs(candidate - edge);
                    if (distance > bestDistance) continue;
                    bestDistance = distance;
                    bestShift = candidate - edge;
                    guide = candidate;
                }
            }

            if (guide.HasValue) guides.Add(guide.Value);
            return bestShift;
        }

        // =====================================================================
        // 文字を打つ
        // =====================================================================

        private void BeginTextEdit(Shape shape, bool isNew)
        {
            _textShape = shape;
            _textIsNew = isNew;
            _textIsLabel = shape.IsBox;
            _textBefore = isNew ? null : Capture();

            _textBox.Text = shape.Text ?? string.Empty;
            _textBox.Visibility = Visibility.Visible;
            PlaceTextBox();

            _textBox.Focus();
            _textBox.CaretIndex = _textBox.Text.Length;
            _surface.InvalidateVisual();
        }

        private void PlaceTextBox()
        {
            if (_textShape == null) return;

            if (_textIsLabel)
            {
                // 四角・丸の中の文字は、枠いっぱいの欄で、真ん中にそろえて折り返す（描いたときと同じ並び）。
                var r = ToScreen(Geometry.Bounds(_textShape, Unit));
                Canvas.SetLeft(_textBox, r.X);
                Canvas.SetTop(_textBox, r.Y);
                _textBox.Width = Math.Max(24, r.Width);
                _textBox.Height = Math.Max(20, r.Height);
                _textBox.Padding = new Thickness(Renderer.LabelPadding * Unit * Scale, 0, Renderer.LabelPadding * Unit * Scale, 0);
                _textBox.TextAlignment = TextAlignment.Center;
                _textBox.TextWrapping = TextWrapping.Wrap;
                _textBox.VerticalContentAlignment = VerticalAlignment.Center;
                _textBox.FontSize = Math.Max(1, Geometry.PtToMm(Renderer.LabelSize(_textShape)) * Unit * Scale);
                return;
            }

            var at = ToScreen(_textShape.Start);
            Canvas.SetLeft(_textBox, at.X - 1);
            Canvas.SetTop(_textBox, at.Y - 1);
            _textBox.Width = double.NaN;
            _textBox.Height = double.NaN;
            _textBox.Padding = new Thickness(0);
            _textBox.TextAlignment = TextAlignment.Left;
            _textBox.TextWrapping = TextWrapping.NoWrap;
            _textBox.VerticalContentAlignment = VerticalAlignment.Top;
            _textBox.FontSize = Math.Max(1, Geometry.PtToMm(_textShape.Size) * Unit * Scale);
        }

        /// <summary>打った文字を決める。空なら、その文字の図形を消す。</summary>
        private void EndTextEdit()
        {
            if (_textShape == null) return;

            var shape = _textShape;
            _textShape = null;

            // Esc・Ctrl+Enter・道具の切り替えで決めたときは、描く面にキーを戻す。
            // ほかの欄（右の寸法の欄など）を押して決まったときは、そちらへ移ったフォーカスを奪わない。
            var refocus = _textBox.IsKeyboardFocusWithin;
            _textBox.Visibility = Visibility.Collapsed;

            var text = _textBox.Text;

            // 四角・丸の中の文字は、空にしたら文字を消すだけ（図形は残す）。
            if (_textIsLabel)
            {
                var label = text.Trim().Length == 0 ? null : text;
                if (label != shape.Text && _textBefore != null) PushSnapshot(_textBefore);
                shape.Text = label;
            }
            else if (text.Trim().Length == 0)
            {
                if (_textIsNew)
                {
                    if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);   // 置いただけで何も打たなかったので、置いたことも無かったことに
                }
                else if (_textBefore != null)
                {
                    PushSnapshot(_textBefore);
                }

                _shapes.Remove(shape);
                _selection.Remove(shape);
                Raise(SelectionChanged);
            }
            else
            {
                if (!_textIsNew && text != shape.Text && _textBefore != null) PushSnapshot(_textBefore);
                shape.Text = text;
            }

            _textBefore = null;
            Changed();
            if (refocus && IsVisible) Focus();
        }

        private void OnTextKeyDown(object sender, KeyEventArgs e)
        {
            // Esc か Ctrl+Enter で決める。Enter は改行（Excel のテキストボックスと同じ）。
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                EndTextEdit();
                e.Handled = true;
            }
        }

        // =====================================================================
        // キー
        // =====================================================================

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_textShape != null) return;   // 文字を打っている間は、欄に任せる

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var handled = true;

            switch (e.Key)
            {
                case Key.Delete:
                case Key.Back:
                    DeleteSelection();
                    break;

                case Key.Escape:
                    if (_gesture != Gesture.None) CancelGesture();
                    else if (_tool != Tool.Select) CurrentTool = Tool.Select;
                    else SetSelection(Enumerable.Empty<Shape>());
                    break;

                case Key.Left: Nudge(-1, 0, ctrl, shift); break;
                case Key.Right: Nudge(1, 0, ctrl, shift); break;
                case Key.Up: Nudge(0, -1, ctrl, shift); break;
                case Key.Down: Nudge(0, 1, ctrl, shift); break;

                case Key.F2:
                    if (_selection.Count == 1 && (_selection[0].Type == Kinds.Text || _selection[0].IsBox)) BeginTextEdit(_selection[0], false);
                    break;

                default:
                    handled = Keyboard.Modifiers == ModifierKeys.None && ChooseToolByKey(e.Key);
                    break;
            }

            if (!handled && ctrl)
            {
                handled = true;
                switch (e.Key)
                {
                    case Key.A: SetSelection(_shapes.ToList()); break;
                    case Key.C: Copy(); break;
                    case Key.X: Copy(); DeleteSelection(); break;
                    case Key.V: Paste(); break;
                    case Key.D: Duplicate(); break;
                    case Key.Z: Undo(); break;
                    case Key.Y: Redo(); break;
                    default: handled = false; break;
                }
            }

            if (handled) e.Handled = true;
        }

        /// <summary>道具を1文字のキーで選ぶ（2026-10-18）。V 選ぶ・L 直線・R 四角・C 丸・T 文字・D 寸法。選んだら true。</summary>
        private bool ChooseToolByKey(Key key)
        {
            switch (key)
            {
                case Key.V: CurrentTool = Tool.Select; return true;
                case Key.L: CurrentTool = Tool.Line; return true;
                case Key.R: CurrentTool = Tool.Rect; return true;
                case Key.C: CurrentTool = Tool.Ellipse; return true;
                case Key.T: CurrentTool = Tool.Text; return true;
                case Key.D: CurrentTool = Tool.Dimension; return true;
                default: return false;
            }
        }

        /// <summary>矢印で動かす。1mm ずつ、Shift で 10mm、Ctrl で 0.1mm。</summary>
        private void Nudge(int x, int y, bool ctrl, bool shift)
        {
            if (_selection.Count == 0) return;

            var step = ctrl ? 0.1 : shift ? 10 : 1;
            PushUndo();
            DetachFromOutside(_selection);
            foreach (var shape in _selection) shape.Move(x * step, y * step);
            Changed();
        }

        /// <summary>つかんでいるのをやめ、つかむ前に戻す（Esc）。</summary>
        private void CancelGesture()
        {
            if (_gesture == Gesture.Move && _moveOriginals != null)
            {
                for (var i = 0; i < _selection.Count && i < _moveOriginals.Count; i++) Copy(_moveOriginals[i], _selection[i]);
                if (_undoPushed && _undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            }
            else if (_gesture == Gesture.Resize && _resizeTarget != null)
            {
                Copy(_resizeOriginal, _resizeTarget);
                if (_undoPushed && _undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            }
            else if (_gesture == Gesture.Board)
            {
                _board = _boardOriginal;
                _unit = _unitOriginal;
                if (_undoPushed && _undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
                Raise(BoardChanged);
            }

            _drawing = null;
            EndGesture(false);
        }

        private static void Copy(Shape from, Shape to)
        {
            to.Assign(from);
        }

        private void DeleteSelection()
        {
            if (_selection.Count == 0) return;

            PushUndo();
            foreach (var shape in _selection) _shapes.Remove(shape);
            SetSelection(Enumerable.Empty<Shape>());
            Changed();
        }

        private void Copy()
        {
            if (_selection.Count == 0) return;

            _clipboard = _selection.OrderBy(shape => _shapes.IndexOf(shape)).Select(shape => shape.Clone()).ToList();
            _pasteCount = 0;

            // Windows のクリップボードにも印を置く。貼り付けのときに、画像（ほかのアプリでコピーしたもの）とどちらが新しいか分かるように（2026-10-25）。
            try
            {
                Clipboard.SetData(ShapesFormat, "shapes");
            }
            catch (Exception)
            {
                // クリップボードがほかのアプリにつかまれている等。図形の貼り付けはできる
            }
        }

        /// <summary>図エディタの図形をコピーしたときに、Windows のクリップボードに置く印の形式。</summary>
        private const string ShapesFormat = "Tsuzuri.DrawingShapes";

        /// <summary>
        /// 貼り付け。Windows のクリップボードが画像（ほかのアプリでコピーしたもの）なら画像を貼る（2026-10-25）。
        /// そうでなければ図形を貼る。貼るたびに紙の上で 5mm ずつずらす（重なって見えなくならないように）。
        /// </summary>
        private void Paste()
        {
            var image = PasteImage();
            if (image == PasteOutcome.Pasted) return;

            // クリップボードが読めない・画像が壊れているときは、何も貼らずに知らせる（2026-10-27）。
            // 前は黙って、前にコピーした図形を貼っていた（画像を貼ったつもりで、違う物が貼られる）。
            if (image == PasteOutcome.Failed)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "クリップボードの画像を読めませんでした（ほかのアプリがクリップボードを使っている・画像が壊れている等）。" + Environment.NewLine
                    + "何も貼り付けていません。もう一度コピーしてから貼り付けてください。",
                    "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                Focus();
                return;
            }

            if (_clipboard.Count == 0) return;

            _pasteCount++;
            PushUndo();
            var pasted = CloneWithNewIds(_clipboard);
            foreach (var shape in pasted)
            {
                shape.Move(5 * Unit * _pasteCount, 5 * Unit * _pasteCount);
                _shapes.Add(shape);
            }

            SetSelection(pasted);
            Changed();
        }

        /// <summary>画像の貼り付けの結果（2026-10-27）。画像でない（図形を貼る）・貼った・画像だが読めなかった（何も貼らない）。</summary>
        private enum PasteOutcome
        {
            NotImage,
            Pasted,
            Failed,
        }

        /// <summary>
        /// Windows のクリップボードの画像を、画像の図形として貼る。画像でない・図エディタの図形をコピーした後なら NotImage。
        /// クリップボードがつかまれている・画像が壊れている等で読めなければ Failed（2026-10-27。前は「画像でない」と同じに扱い、図形を貼っていた）。
        /// 大きさは画像の画素を画面の大きさ（96dpi）で mm にしたもの。ボードの幅・高さの 8 割を超えるなら、比を保って縮める。
        /// 置き場所はボードの真ん中。長い辺が 2400 画素を超える画像は縮めて埋め込む（図のファイルが大きくなりすぎないように）。
        /// </summary>
        private PasteOutcome PasteImage()
        {
            string data;
            int pixelWidth, pixelHeight;
            try
            {
                if (Clipboard.ContainsData(ShapesFormat)) return PasteOutcome.NotImage;
                if (!Clipboard.ContainsImage() && !Clipboard.ContainsData("PNG")) return PasteOutcome.NotImage;

                var bitmap = ReadClipboardImage();
                if (bitmap == null || bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1) return PasteOutcome.Failed;

                // 縮める・PNG にするのも、壊れた画像だと投げることがあるので、この中で（2026-10-27）。
                const int MaxPixels = 2400;
                var longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                if (longest > MaxPixels)
                {
                    var factor = (double)MaxPixels / longest;
                    bitmap = new System.Windows.Media.Imaging.TransformedBitmap(bitmap, new ScaleTransform(factor, factor));
                }

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var stream = new System.IO.MemoryStream())
                {
                    encoder.Save(stream);
                    data = Convert.ToBase64String(stream.ToArray());
                }

                pixelWidth = bitmap.PixelWidth;
                pixelHeight = bitmap.PixelHeight;
            }
            catch (Exception)
            {
                return PasteOutcome.Failed;   // クリップボードがつかまれている（COMException）・画像が壊れている等
            }

            // 大きさ（mm）。画素を 96dpi の画面の大きさにして、ボードに収まらなければ縮める。
            var width = pixelWidth / Geometry.PxPerMm;
            var height = pixelHeight / Geometry.PxPerMm;
            var fit = Math.Min(1, Math.Min(_board.Width * 0.8 / width, _board.Height * 0.8 / height));
            width *= fit;
            height *= fit;

            var left = _board.Left + (_board.Width - width) / 2;
            var top = _board.Top + (_board.Height - height) / 2;

            EndTextEdit();
            PushUndo();
            var shape = new Shape
            {
                Type = Kinds.Image,
                X1 = left,
                Y1 = top,
                X2 = left + width,
                Y2 = top + height,
                Image = data,
                Id = Shape.NewId(),
            };
            _shapes.Add(shape);

            CurrentTool = Tool.Select;
            SetSelection(new[] { shape });
            Changed();
            return PasteOutcome.Pasted;
        }

        /// <summary>
        /// クリップボードの画像。PNG で置かれていればそれを読む（透明の部分や色が崩れない。切り取りツール・ブラウザなど）。
        /// 無いか、PNG が読めなければ Windows の標準の画像（GetImage。2026-10-27。前は PNG が読めないと、そこで諦めていた）。
        /// GetImage は 32 ビットの画像の透明度を 0 のまま返すことがあり、そのままでは全部透明（貼っても見えない）になる。
        /// 透明度がすべて 0 なら、透明度を捨てて不透明にする（2026-10-27）。
        /// CF_DIBV5 を自前で読むことはしない（形式の読み分けが多く、間違えると色が崩れる。PNG で置くアプリが多く、そこで足りる）。
        /// どれも読めなければ null。クリップボードがつかまれていれば投げる（呼ぶ側で受ける）。
        /// </summary>
        private static System.Windows.Media.Imaging.BitmapSource ReadClipboardImage()
        {
            if (Clipboard.ContainsData("PNG"))
            {
                try
                {
                    var stream = Clipboard.GetData("PNG") as System.IO.Stream;
                    if (stream != null)
                    {
                        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(stream,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count > 0) return decoder.Frames[0];
                    }
                }
                catch (Exception)
                {
                    // 壊れた PNG。標準の画像を試す
                }
            }

            if (!Clipboard.ContainsImage()) return null;
            var bitmap = Clipboard.GetImage();
            return bitmap == null ? null : OpaqueIfAllTransparent(bitmap);
        }

        /// <summary>透明度のある画像で、透明度がすべて 0 なら、透明度を捨てた（不透明の）画像にする。そうでなければそのまま。</summary>
        private static System.Windows.Media.Imaging.BitmapSource OpaqueIfAllTransparent(System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32) return bitmap;

            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) return bitmap;   // 透明度を持っている。そのまま
            }

            // 同じ並びのまま Bgr32（4 バイト目を使わない形式）として作り直す。色の値はそのまま（透明度で割り戻さない）。
            return System.Windows.Media.Imaging.BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight,
                bitmap.DpiX > 0 ? bitmap.DpiX : 96, bitmap.DpiY > 0 ? bitmap.DpiY : 96, PixelFormats.Bgr32, null, pixels, stride);
        }

        /// <summary>Ctrl+D。選んでいる図形を、紙の上で 5mm ずらして複製する。</summary>
        private void Duplicate()
        {
            if (_selection.Count == 0) return;

            PushUndo();
            var copies = CloneWithNewIds(_selection.OrderBy(shape => _shapes.IndexOf(shape)));
            foreach (var shape in copies)
            {
                shape.Move(5 * Unit, 5 * Unit);
                _shapes.Add(shape);
            }

            SetSelection(copies);
            Changed();
        }

        // =====================================================================
        // 選ぶ・変わったことを知らせる
        // =====================================================================

        private void SetSelection(IEnumerable<Shape> shapes)
        {
            var list = shapes.ToList();
            _selection.Clear();
            _selection.AddRange(list);
            Raise(SelectionChanged);
            Refresh();
        }

        private void Changed()
        {
            UpdateConnections();
            Refresh();
            Raise(ShapesChanged);
        }

        // =====================================================================
        // 線をつなぐ（2026-10-19）
        //
        // 直線・寸法線の端を図形の点（端・角・辺の中点・丸の上下左右）に吸い付けると、その図形の Id と位置（U・V）を覚える。
        // 図形が動いたり大きさが変わったりしたら、覚えた位置へ端を動かす（線は直線のまま）。
        // =====================================================================

        /// <summary>線の端（start なら始点）を owner の (u, v) につなぐ。owner が null か自分なら、つながりを外す。</summary>
        private static void Attach(Shape line, bool start, Shape owner, double u, double v)
        {
            var id = owner != null && !ReferenceEquals(owner, line) && owner.Type != Kinds.Text ? owner.Id : null;

            if (start)
            {
                line.StartShape = id;
                line.StartU = u;
                line.StartV = v;
            }
            else
            {
                line.EndShape = id;
                line.EndU = u;
                line.EndV = v;
            }
        }

        /// <summary>つないだ線の端を、相手の図形の今の位置へ動かす。相手が無くなっていたら、つながりを外す（線はその場に残す）。</summary>
        private void UpdateConnections()
        {
            var byId = new Dictionary<string, Shape>();
            foreach (var shape in _shapes)
            {
                if (!string.IsNullOrEmpty(shape.Id) && !byId.ContainsKey(shape.Id)) byId[shape.Id] = shape;
            }

            // 線の端に線をつないでいることもあるので、2回まわして追いつかせる。
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var shape in _shapes)
                {
                    if (!shape.IsSegment) continue;

                    Shape target;
                    if (shape.StartShape != null)
                    {
                        if (byId.TryGetValue(shape.StartShape, out target) && !ReferenceEquals(target, shape))
                            shape.Start = Geometry.AnchorPoint(target, shape.StartU, shape.StartV);
                        else
                            shape.StartShape = null;
                    }

                    if (shape.EndShape != null)
                    {
                        if (byId.TryGetValue(shape.EndShape, out target) && !ReferenceEquals(target, shape))
                            shape.End = Geometry.AnchorPoint(target, shape.EndU, shape.EndV);
                        else
                            shape.EndShape = null;
                    }
                }
            }
        }

        /// <summary>動かす線のうち、moving に入っていない図形とのつながりを外す（線そのものを動かしたとき）。</summary>
        private static void DetachFromOutside(ICollection<Shape> moving)
        {
            var ids = new HashSet<string>(moving.Where(shape => shape.Id != null).Select(shape => shape.Id));
            foreach (var shape in moving)
            {
                if (!shape.IsSegment) continue;
                if (shape.StartShape != null && !ids.Contains(shape.StartShape)) shape.StartShape = null;
                if (shape.EndShape != null && !ids.Contains(shape.EndShape)) shape.EndShape = null;
            }
        }

        /// <summary>
        /// 写して新しい Id を付ける（貼り付け・複製）。写した図形どうしのつながりは、新しい Id に付け替える。
        /// 写していない図形とのつながりは外す（元の図形につながったままだと、写した線が元の所へ引き戻される）。
        /// </summary>
        private static List<Shape> CloneWithNewIds(IEnumerable<Shape> source)
        {
            var copies = source.Select(shape => shape.Clone()).ToList();
            var map = new Dictionary<string, string>();

            foreach (var copy in copies)
            {
                var id = Shape.NewId();
                if (copy.Id != null) map[copy.Id] = id;
                copy.Id = id;
            }

            foreach (var copy in copies)
            {
                string mapped;
                copy.StartShape = copy.StartShape != null && map.TryGetValue(copy.StartShape, out mapped) ? mapped : null;
                copy.EndShape = copy.EndShape != null && map.TryGetValue(copy.EndShape, out mapped) ? mapped : null;
            }

            return copies;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // =====================================================================
        // 元に戻す・やり直す（図形をまるごと覚える。図は小さいので、これで足りる）
        // =====================================================================

        private Snapshot Capture()
        {
            return new Snapshot
            {
                Shapes = _shapes.Select(shape => shape.Clone()).ToList(),
                Selected = _selection.Select(shape => _shapes.IndexOf(shape)).Where(index => index >= 0).ToList(),
                Board = _board,
                Unit = _unit,
            };
        }

        /// <summary>書き換える前に呼ぶ。</summary>
        private void PushUndo()
        {
            PushSnapshot(Capture());
        }

        private void PushSnapshot(Snapshot snapshot)
        {
            _undo.Add(snapshot);
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>
        /// つかんで動かす間に1回だけ覚える。originals があれば、選んでいる図形はその形で覚える
        /// （動かし始めてから呼ぶので、選んでいる図形はもう動いていることがある）。
        /// </summary>
        private void PushUndoOnce(List<Shape> originals)
        {
            _undoPushed = true;

            var snapshot = Capture();
            if (originals != null)
            {
                for (var i = 0; i < _selection.Count && i < originals.Count; i++)
                {
                    var index = _shapes.IndexOf(_selection[i]);
                    if (index >= 0) snapshot.Shapes[index] = originals[i].Clone();
                }
            }
            else if (_resizeTarget != null && _resizeOriginal != null)
            {
                var index = _shapes.IndexOf(_resizeTarget);
                if (index >= 0) snapshot.Shapes[index] = _resizeOriginal.Clone();
            }

            PushSnapshot(snapshot);
        }

        private void Undo()
        {
            if (_undo.Count == 0) return;

            _redo.Add(Capture());
            Restore(_undo[_undo.Count - 1]);
            _undo.RemoveAt(_undo.Count - 1);
        }

        private void Redo()
        {
            if (_redo.Count == 0) return;

            _undo.Add(Capture());
            Restore(_redo[_redo.Count - 1]);
            _redo.RemoveAt(_redo.Count - 1);
        }

        private void Restore(Snapshot snapshot)
        {
            _shapes.Clear();
            _shapes.AddRange(snapshot.Shapes.Select(shape => shape.Clone()));
            _board = snapshot.Board;
            _unit = snapshot.Unit > 0 ? snapshot.Unit : _unit;
            Raise(BoardChanged);
            SetSelection(snapshot.Selected.Where(index => index < _shapes.Count).Select(index => _shapes[index]));
            Changed();
        }

        // =====================================================================
        // 小物
        // =====================================================================

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        private static Pen MakeDashed(Brush brush)
        {
            var pen = new Pen(brush, 1);
            pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            pen.Freeze();
            return pen;
        }

        /// <summary>描いている途中の図形の線（太さは書き出す図と同じ。ボードの大きさで変わるので、そのつど作る）。</summary>
        private Pen InkPen()
        {
            var pen = new Pen(Ink, Geometry.LineWidth * Unit);
            pen.StartLineCap = PenLineCap.Round;
            pen.EndLineCap = PenLineCap.Round;
            pen.Freeze();
            return pen;
        }
    }
}
