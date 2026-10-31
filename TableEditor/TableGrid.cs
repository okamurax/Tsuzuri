using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TableEditor
{
    /// <summary>
    /// 表エディタのマス目（SPEC 8.4.2。2026-10-18 に SPREAD から自前の WPF に替えた）。
    ///
    /// 描くのは1枚の面（Surface）にまとめて行う（見出し・セル・罫線・選んだ範囲）。部品をセルの数だけ作らないので、表が大きくても軽い。
    /// 字を打つ欄（TextBox）は1つだけで、いつも今のセルの上に重ねてフォーカスを持たせておく。
    /// ・何も打っていないとき（Ready）: 欄は透明で空。矢印・Delete・Ctrl+C などはここで拾う
    /// ・字が入ったら（日本語の変換の始まりも含む）: その字から打ち直す（Enter。Excel と同じく、元の値を消して打つ）。矢印で確定して動く
    /// ・F2・ダブルクリック: 元の値に書き足す（Edit）。矢印は欄の中のカーソルを動かす
    /// 欄を先に置いておくのは、日本語入力の変換をその場で始められるようにするため（キーを拾ってから欄を出すと、最初の字の変換が欄に入らない）。
    /// </summary>
    internal sealed class TableGrid : Grid
    {
        private const double RowHeight = 20;
        private const double HeaderHeight = 20;
        private const double CellPadding = 4;
        private const double MinColumnWidth = 16;
        private const double MaxFitWidth = 320;
        private const double HandleSize = 6;
        private const int UndoLimit = 200;
        private const int CacheLimit = 5000;

        // 選んだ範囲の枠と、オートフィルのつまみの縁。描くたびに作らない。
        private static readonly Pen SelectionPen = Freeze(new Pen(Palette.Accent, 2));
        private static readonly Pen HandlePen = Freeze(new Pen(Palette.Page, 1));

        private enum Mode
        {
            Ready,
            Enter,
            Edit,
        }

        private enum Hit
        {
            None,
            Corner,
            ColumnHeader,
            ColumnEdge,
            RowHeader,
            Cell,
            Handle,
        }

        private enum Drag
        {
            None,
            Cells,
            Columns,
            Rows,
            Resize,
            Fill,
        }

        private enum FillDirection
        {
            Down,
            Up,
            Right,
            Left,
        }

        private sealed class Snapshot
        {
            public TableData Data;
            public int ActiveRow, ActiveColumn, ExtentRow, ExtentColumn;
        }

        /// <summary>描くだけの面。描き方は TableGrid が持つ。</summary>
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

        private readonly TableData _data;
        private readonly Surface _surface;
        private readonly TextBox _editor;
        private readonly ScrollBar _vertical;
        private readonly ScrollBar _horizontal;
        private readonly DispatcherTimer _marqueeTimer = new DispatcherTimer();
        private readonly DispatcherTimer _dragTimer = new DispatcherTimer();

        // 一番上に見えている行と、一番左に見えている列。
        private int _topRow;
        private int _leftColumn;

        // 選んでいる範囲。Active が今のセル（打つと入る所）、Extent が Shift で広げた反対の角。
        private int _activeRow;
        private int _activeColumn;
        private int _extentRow;
        private int _extentColumn;

        private Mode _mode = Mode.Ready;
        private bool _quiet;       // 欄の字をこちらで書き換えている間は、打ったことにしない
        private bool _scrolling;   // スクロールバーの値をこちらで合わせている間は、動かされたことにしない
        private double _pixelsPerDip = 1;

        private Drag _drag = Drag.None;
        private Point _dragPoint;
        private int _resizeColumn;
        private double _resizeStart;
        private double _resizeWidth;

        // オートフィルで埋める先（選んだ範囲を含めた全体）と向き。
        private bool _fillValid;
        private int _fillTop, _fillLeft, _fillBottom, _fillRight;
        private FillDirection _fillDirection;

        // コピーした範囲の破線（Excel と同じ。Esc・打ち始め・切り取りの貼り付けで消える）。
        private bool _marquee;
        private int _marqueeTop, _marqueeLeft, _marqueeBottom, _marqueeRight;
        private bool _cut;
        private string _clipboardText;
        private double _dash;

        private readonly List<Snapshot> _undo = new List<Snapshot>();
        private readonly List<Snapshot> _redo = new List<Snapshot>();

        // 字のある一番下の行と一番右の列（Normalize が合わせる）。その1つ先が、打ち足せる空きの行・列。
        private int _lastRow = -1;
        private int _lastColumn = -1;

        // 描く字の形。描くたびに作り直すと重いので、同じ字・同じ幅なら使い回す（2026-10-18）。
        private readonly Dictionary<string, FormattedText> _texts = new Dictionary<string, FormattedText>();

        public TableGrid(TableData data)
        {
            _data = data;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _surface = new Surface(Render);
            _surface.Focusable = false;
            RenderOptions.SetEdgeMode(_surface, EdgeMode.Aliased);   // 罫線を 1px でくっきり描く（字には効かない）

            _editor = new TextBox();
            _editor.BorderThickness = new Thickness(0);
            _editor.Padding = new Thickness(CellPadding - 2, 0, CellPadding - 2, 0);
            _editor.VerticalContentAlignment = VerticalAlignment.Center;
            _editor.FontFamily = Palette.Font;
            _editor.FontSize = Palette.FontSize;
            _editor.Foreground = Palette.Text;
            _editor.SelectionBrush = Palette.Accent;
            _editor.FocusVisualStyle = null;
            _editor.AcceptsReturn = false;
            _editor.AcceptsTab = false;
            _editor.TextWrapping = TextWrapping.NoWrap;
            _editor.PreviewKeyDown += OnKeyDown;
            _editor.TextChanged += OnEditorTextChanged;

            var overlay = new Canvas();   // 地が無いので、押しても下の面に届く
            overlay.Children.Add(_editor);

            var host = new Grid();
            host.ClipToBounds = true;
            host.Children.Add(_surface);
            host.Children.Add(overlay);
            Children.Add(host);

            _vertical = new ScrollBar();
            _vertical.Orientation = Orientation.Vertical;
            _vertical.SmallChange = 1;
            _vertical.ValueChanged += delegate
            {
                if (_scrolling) return;
                _topRow = Math.Max(0, (int)Math.Round(_vertical.Value));
                UpdateView();
            };
            SetColumn(_vertical, 1);
            Children.Add(_vertical);

            _horizontal = new ScrollBar();
            _horizontal.Orientation = Orientation.Horizontal;
            _horizontal.SmallChange = 1;
            _horizontal.ValueChanged += delegate
            {
                if (_scrolling) return;
                _leftColumn = Math.Max(0, (int)Math.Round(_horizontal.Value));
                UpdateView();
            };
            SetRow(_horizontal, 1);
            Children.Add(_horizontal);

            var corner = new Border();
            corner.Background = Palette.Chrome;
            SetRow(corner, 1);
            SetColumn(corner, 1);
            Children.Add(corner);

            _surface.MouseLeftButtonDown += OnMouseDown;
            _surface.MouseMove += OnMouseMove;
            _surface.MouseLeftButtonUp += OnMouseUp;
            _surface.LostMouseCapture += delegate { EndDrag(); };
            PreviewMouseWheel += OnMouseWheel;

            _marqueeTimer.Interval = TimeSpan.FromMilliseconds(90);
            _marqueeTimer.Tick += delegate
            {
                _dash = (_dash + 1) % 8;
                _surface.InvalidateVisual();
            };

            // 範囲を選びながら面の外へ出たら、その向きへ送り続ける。
            _dragTimer.Interval = TimeSpan.FromMilliseconds(60);
            _dragTimer.Tick += delegate { AutoScroll(); };

            Loaded += delegate
            {
                _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                _texts.Clear();
                FitColumns();
                UpdateView();
                FocusEditor();
            };

            SizeChanged += delegate { UpdateView(); };
        }

        // =====================================================================
        // 外から使うもの
        // =====================================================================

        /// <summary>受け渡しの形（TableData.ToExchange）。打ちかけの字は含まない（先に CommitEdit）。</summary>
        public string ToExchange()
        {
            return _data.ToExchange();
        }

        /// <summary>打ちかけの字をセルに確定する。</summary>
        public void CommitEdit()
        {
            CommitEdit(false);
        }

        public void FocusEditor()
        {
            _editor.Focus();
            Keyboard.Focus(_editor);
        }

        // =====================================================================
        // 並びの計算
        // =====================================================================

        private double RowHeaderWidth
        {
            get
            {
                var digits = Math.Max(2, _data.RowCount.ToString(CultureInfo.InvariantCulture).Length);
                return 16 + digits * 8;
            }
        }

        /// <summary>最後まで見えている行の数。</summary>
        private int VisibleRows
        {
            get { return Math.Max(1, (int)Math.Floor((_surface.ActualHeight - HeaderHeight) / RowHeight)); }
        }

        /// <summary>最後まで見えている列の数。</summary>
        private int VisibleColumns
        {
            get
            {
                var x = RowHeaderWidth;
                var count = 0;
                for (var c = _leftColumn; ; c++)
                {
                    x += _data.WidthOf(c);
                    if (x > _surface.ActualWidth) break;
                    count++;
                }

                return Math.Max(1, count);
            }
        }

        private double ColumnX(int column)
        {
            var x = RowHeaderWidth;
            if (column >= _leftColumn)
            {
                for (var c = _leftColumn; c < column; c++) x += _data.WidthOf(c);
            }
            else
            {
                for (var c = column; c < _leftColumn; c++) x -= _data.WidthOf(c);
            }

            return x;
        }

        private double RowY(int row)
        {
            return HeaderHeight + (row - _topRow) * RowHeight;
        }

        private int RowAt(double y)
        {
            return _topRow + (int)Math.Floor((y - HeaderHeight) / RowHeight);
        }

        private int ColumnAt(double x)
        {
            var left = RowHeaderWidth;
            if (x < left) return _leftColumn - 1;

            for (var c = _leftColumn; ; c++)
            {
                var width = _data.WidthOf(c);
                if (x < left + width) return c;
                left += width;
            }
        }

        private Rect RangeRect(int top, int left, int bottom, int right)
        {
            var x = ColumnX(left);
            var y = RowY(top);
            var x2 = ColumnX(right) + _data.WidthOf(right);
            var y2 = RowY(bottom) + RowHeight;
            return new Rect(x, y, Math.Max(0, x2 - x), Math.Max(0, y2 - y));
        }

        private void Bounds(out int top, out int left, out int bottom, out int right)
        {
            top = Math.Min(_activeRow, _extentRow);
            bottom = Math.Max(_activeRow, _extentRow);
            left = Math.Min(_activeColumn, _extentColumn);
            right = Math.Max(_activeColumn, _extentColumn);
        }

        private bool IsSelected(int row, int column)
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);
            return row >= top && row <= bottom && column >= left && column <= right;
        }

        /// <summary>選んだ範囲の右下の ■（オートフィルのつまみ）。</summary>
        private Rect HandleRect()
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);
            var range = RangeRect(top, left, bottom, right);
            return new Rect(range.Right - HandleSize / 2 - 1, range.Bottom - HandleSize / 2 - 1, HandleSize, HandleSize);
        }

        // =====================================================================
        // 見た目を合わせる
        // =====================================================================

        /// <summary>
        /// 行と列を「字のある範囲 ＋ 空きの1行・1列」にそろえる（2026-10-18。前は見えている所まで無限に広げていた）。
        /// 書き戻される範囲が、そのまま目に見えるように。空きに打つ・貼る・埋めると表に入り、その先にまた空きができる。
        /// 端の行・列を空にすると縮める。ただし今のセル・選んでいる範囲・埋める先・コピーした範囲までは残す（作業している所が急に消えないように）。
        /// </summary>
        private void Normalize()
        {
            _data.UsedRange(out _lastRow, out _lastColumn);

            var rows = Math.Max(_lastRow + 1, Math.Max(_activeRow, _extentRow));
            var columns = Math.Max(_lastColumn + 1, Math.Max(_activeColumn, _extentColumn));

            if (_drag == Drag.Fill && _fillValid)
            {
                rows = Math.Max(rows, _fillBottom);
                columns = Math.Max(columns, _fillRight);
            }

            if (_marquee)
            {
                rows = Math.Max(rows, _marqueeBottom);
                columns = Math.Max(columns, _marqueeRight);
            }

            _data.EnsureSize(rows + 1, columns + 1);
            _data.TrimTo(rows + 1, columns + 1);
        }

        /// <summary>打ち足せる空きの行（字のある一番下の行の次）。動ける・選べるのはここまで。</summary>
        private int RowLimit
        {
            get { return Math.Max(_lastRow + 1, 0); }
        }

        private int ColumnLimit
        {
            get { return Math.Max(_lastColumn + 1, 0); }
        }

        /// <summary>空きの行・列（表の外）か。少し暗く描く。</summary>
        private bool IsSpare(int row, int column)
        {
            return row > Math.Max(_lastRow, 0) || column > Math.Max(_lastColumn, 0);
        }

        /// <summary>行と列をそろえ、スクロールバー・字の欄を合わせ、描き直す。</summary>
        private void UpdateView()
        {
            Normalize();

            // 表の下・右の端より先へは送らない（端の行・列が見えている所で止める）。
            _topRow = Math.Max(0, Math.Min(_topRow, _data.RowCount - VisibleRows));
            _leftColumn = Math.Max(0, Math.Min(_leftColumn, _data.ColumnCount - 1));

            _scrolling = true;
            _vertical.Minimum = 0;
            _vertical.Maximum = Math.Max(0, _data.RowCount - VisibleRows);
            _vertical.ViewportSize = VisibleRows;
            _vertical.LargeChange = VisibleRows;
            _vertical.Value = Math.Min(_topRow, _vertical.Maximum);

            _horizontal.Minimum = 0;
            _horizontal.Maximum = Math.Max(0, _data.ColumnCount - VisibleColumns);
            _horizontal.ViewportSize = VisibleColumns;
            _horizontal.LargeChange = VisibleColumns;
            _horizontal.Value = Math.Min(_leftColumn, _horizontal.Maximum);
            _scrolling = false;

            PlaceEditor();
            _surface.InvalidateVisual();
        }

        /// <summary>そのセルが見えるように送る。</summary>
        private void EnsureVisible(int row, int column)
        {
            var rows = VisibleRows;
            if (row < _topRow) _topRow = row;
            else if (row >= _topRow + rows) _topRow = row - rows + 1;

            if (column < _leftColumn)
            {
                _leftColumn = column;
            }
            else
            {
                while (column > _leftColumn && ColumnX(column) + _data.WidthOf(column) > _surface.ActualWidth) _leftColumn++;
            }
        }

        /// <summary>字の欄を、今のセルの上に置く。打っていないときは透明で、押しても下の面に届く。</summary>
        private void PlaceEditor()
        {
            var editing = _mode != Mode.Ready;

            _editor.Background = editing ? Palette.Page : Brushes.Transparent;
            _editor.CaretBrush = editing ? Palette.Text : Brushes.Transparent;
            _editor.Foreground = Palette.Text;
            _editor.FontWeight = _activeRow == 0 ? FontWeights.SemiBold : FontWeights.Normal;
            _editor.IsHitTestVisible = editing;

            var rect = RangeRect(_activeRow, _activeColumn, _activeRow, _activeColumn);
            var visible = _activeRow >= _topRow && _activeColumn >= _leftColumn
                && rect.Top < _surface.ActualHeight && rect.Left < _surface.ActualWidth;

            if (!visible)
            {
                // 見えない所に置いても、フォーカスは持たせたままにする（打てば見える所へ送る）。
                Canvas.SetLeft(_editor, -10000);
                Canvas.SetTop(_editor, -10000);
                return;
            }

            Canvas.SetLeft(_editor, rect.X);
            Canvas.SetTop(_editor, rect.Y);
            _editor.MinWidth = Math.Max(1, rect.Width - 1);
            _editor.MaxWidth = Math.Max(rect.Width - 1, _surface.ActualWidth - rect.X - 1);   // 長い字は右へ広げる
            _editor.MinHeight = RowHeight - 1;
        }

        // =====================================================================
        // 描く
        // =====================================================================

        private void Render(DrawingContext dc)
        {
            var width = _surface.ActualWidth;
            var height = _surface.ActualHeight;
            var rowHeader = RowHeaderWidth;

            dc.DrawRectangle(Palette.Outside, null, new Rect(0, 0, width, height));   // 表の外（行・列の無い所）

            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);
            var multi = top != bottom || left != right;

            // 見えている列と行。
            var columns = new List<int>();
            var xs = new List<double>();
            var x = rowHeader;
            for (var c = _leftColumn; c < _data.ColumnCount && x < width; c++)
            {
                columns.Add(c);
                xs.Add(x);
                x += _data.WidthOf(c);
            }
            var gridRight = x;

            var rows = new List<int>();
            var y = HeaderHeight;
            for (var r = _topRow; r < _data.RowCount && y < height; r++)
            {
                rows.Add(r);
                y += RowHeight;
            }
            var gridBottom = y;

            // --- セル（見出しの下・右の範囲だけに描く） ---
            dc.PushClip(new RectangleGeometry(new Rect(rowHeader, HeaderHeight, Math.Max(0, width - rowHeader), Math.Max(0, height - HeaderHeight))));
            dc.DrawRectangle(Palette.Page, null, new Rect(rowHeader, HeaderHeight, Math.Max(0, gridRight - rowHeader), Math.Max(0, gridBottom - HeaderHeight)));

            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var rowTop = HeaderHeight + i * RowHeight;

                for (var j = 0; j < columns.Count; j++)
                {
                    var c = columns[j];
                    var cell = new Rect(xs[j], rowTop, _data.WidthOf(c), RowHeight);

                    // 空きの行・列は表の外なので少し暗く。1行目（見出し行）もほかの行と同じ地で、字をやや太くするだけ（本体の表と同じ。2026-10-18）。
                    if (IsSpare(r, c)) dc.DrawRectangle(Palette.Spare, null, cell);

                    if (multi && IsSelected(r, c) && !(r == _activeRow && c == _activeColumn))
                        dc.DrawRectangle(Palette.Selection, null, cell);

                    if (_mode != Mode.Ready && r == _activeRow && c == _activeColumn) continue;   // 打っている欄が上に重なる

                    var text = _data.Get(r, c);
                    if (text.Length > 0) DrawCellText(dc, text, r == 0, c, cell);
                }
            }

            // 罫線。
            for (var j = 0; j < columns.Count; j++)
                dc.DrawRectangle(Palette.Rule, null, new Rect(xs[j] + _data.WidthOf(columns[j]) - 1, HeaderHeight, 1, gridBottom - HeaderHeight));
            for (var i = 0; i < rows.Count; i++)
                dc.DrawRectangle(Palette.Rule, null, new Rect(rowHeader, HeaderHeight + (i + 1) * RowHeight - 1, gridRight - rowHeader, 1));

            // 選んだ範囲の枠と、オートフィルのつまみ。
            var selection = RangeRect(top, left, bottom, right);
            dc.DrawRectangle(null, SelectionPen, Inset(selection, 1));
            if (_mode == Mode.Ready)
            {
                dc.DrawRectangle(Palette.Accent, HandlePen, HandleRect());
            }

            // オートフィルで埋める先。
            if (_drag == Drag.Fill && _fillValid)
                dc.DrawRectangle(null, DashedPen(0), Inset(RangeRect(_fillTop, _fillLeft, _fillBottom, _fillRight), 1));

            // コピーした範囲。破線を少しずつずらして、動いて見せる。
            if (_marquee)
                dc.DrawRectangle(null, DashedPen(_dash), Inset(RangeRect(_marqueeTop, _marqueeLeft, _marqueeBottom, _marqueeRight), 1));

            dc.Pop();

            // --- 列の見出し（A、B…） ---
            dc.DrawRectangle(Palette.Chrome, null, new Rect(0, 0, width, HeaderHeight));
            for (var j = 0; j < columns.Count; j++)
            {
                var c = columns[j];
                var rect = new Rect(xs[j], 0, _data.WidthOf(c), HeaderHeight);
                var selected = c >= left && c <= right;

                if (selected) dc.DrawRectangle(Palette.HeaderSelected, null, rect);
                DrawCentered(dc, ColumnName(c), rect, c > Math.Max(_lastColumn, 0));
                dc.DrawRectangle(Palette.Rule, null, new Rect(rect.Right - 1, 0, 1, HeaderHeight));
                if (selected) dc.DrawRectangle(Palette.Accent, null, new Rect(rect.X, HeaderHeight - 2, rect.Width, 2));
            }
            dc.DrawRectangle(Palette.Rule, null, new Rect(0, HeaderHeight - 1, gridRight, 1));

            // --- 行の見出し（1、2…） ---
            dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, rowHeader, Math.Max(0, height - HeaderHeight))));
            dc.DrawRectangle(Palette.Chrome, null, new Rect(0, HeaderHeight, rowHeader, height - HeaderHeight));
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var rect = new Rect(0, HeaderHeight + i * RowHeight, rowHeader, RowHeight);
                var selected = r >= top && r <= bottom;

                if (selected) dc.DrawRectangle(Palette.HeaderSelected, null, rect);
                DrawCentered(dc, (r + 1).ToString(CultureInfo.InvariantCulture), rect, r > Math.Max(_lastRow, 0));
                dc.DrawRectangle(Palette.Rule, null, new Rect(0, rect.Bottom - 1, rowHeader, 1));
                if (selected) dc.DrawRectangle(Palette.Accent, null, new Rect(rowHeader - 2, rect.Y, 2, RowHeight));
            }
            dc.DrawRectangle(Palette.Rule, null, new Rect(rowHeader - 1, HeaderHeight, 1, gridBottom - HeaderHeight));
            dc.Pop();

            // --- 左上の角 ---
            dc.DrawRectangle(Palette.Chrome, null, new Rect(0, 0, rowHeader, HeaderHeight));
            dc.DrawRectangle(Palette.Rule, null, new Rect(rowHeader - 1, 0, 1, HeaderHeight));
            dc.DrawRectangle(Palette.Rule, null, new Rect(0, HeaderHeight - 1, rowHeader, 1));
        }

        private static Rect Inset(Rect rect, double amount)
        {
            if (rect.Width <= amount * 2 || rect.Height <= amount * 2) return rect;
            rect.Inflate(-amount, -amount);
            return rect;
        }

        private static Pen DashedPen(double offset)
        {
            var pen = new Pen(Palette.Marquee, 1.5);
            pen.DashStyle = new DashStyle(new double[] { 3, 2 }, offset / 1.5);
            return pen;
        }

        private static Pen Freeze(Pen pen)
        {
            pen.Freeze();
            return pen;
        }

        /// <summary>
        /// 1行に収めた字の形（幅に収まらなければ … で切る）。同じ字・太さ・色・幅なら作った物を使い回す。
        /// 溜まりすぎたら捨てる（中身を書き換えると、前の字の形は使われなくなる）。
        /// </summary>
        private FormattedText CachedText(string text, bool bold, Brush brush, char brushKey, double maxWidth)
        {
            var key = (bold ? "B" : "N") + brushKey + Math.Round(maxWidth).ToString(CultureInfo.InvariantCulture) + "|" + text;

            FormattedText formatted;
            if (_texts.TryGetValue(key, out formatted)) return formatted;

            if (_texts.Count > CacheLimit) _texts.Clear();

            formatted = MakeText(text, bold, brush);
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            _texts[key] = formatted;
            return formatted;
        }

        private FormattedText MakeText(string text, bool bold, Brush brush)
        {
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                bold ? Palette.Bold : Palette.Cell, Palette.FontSize, brush, _pixelsPerDip);
        }

        private void DrawCellText(DrawingContext dc, string text, bool heading, int column, Rect cell)
        {
            // セルの中の改行は、マス目では空白にして1行で見せる（書き戻すときは <br>）。
            var display = text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0
                ? text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')
                : text;

            var formatted = CachedText(display, heading, Palette.Text, 't', cell.Width - CellPadding * 2);

            // 寄せの指定が無い列は左に寄せる。本体の表・Word と同じ（2026-10-18。前は Excel に合わせて数値を右に寄せて見せていたが、
            // 保存すると本体でも Word でも左に寄るので、食い違っていた）。数値を右に寄せたい列は「右寄せ」にする。
            var align = _data.AlignOf(column);

            var x = align == 'R' ? cell.Right - CellPadding - formatted.Width
                : align == 'C' ? cell.X + (cell.Width - formatted.Width) / 2
                : cell.X + CellPadding;

            dc.DrawText(formatted, new Point(x, cell.Y + (RowHeight - formatted.Height) / 2));
        }

        /// <summary>見出しの字（A、B… と 1、2…）。空きの行・列（表の外）の見出しは控えめな色にする。</summary>
        private void DrawCentered(DrawingContext dc, string text, Rect rect, bool spare)
        {
            var formatted = spare
                ? CachedText(text, false, Palette.Muted, 'm', rect.Width - 2)
                : CachedText(text, false, Palette.Text, 't', rect.Width - 2);
            dc.DrawText(formatted, new Point(rect.X + (rect.Width - formatted.Width) / 2, rect.Y + (rect.Height - formatted.Height) / 2));
        }

        /// <summary>A、B、…、Z、AA、AB…</summary>
        private static string ColumnName(int column)
        {
            var name = string.Empty;
            for (var n = column + 1; n > 0; n = (n - 1) / 26)
                name = (char)('A' + (n - 1) % 26) + name;
            return name;
        }

        // =====================================================================
        // 列の幅
        // =====================================================================

        /// <summary>開いたときに、どの列も中身が入る幅にする（既定の幅より狭くはしない。広げるのは MaxFitWidth まで）。</summary>
        private void FitColumns()
        {
            int lastRow, lastColumn;
            _data.UsedRange(out lastRow, out lastColumn);
            for (var c = 0; c <= lastColumn; c++) FitColumn(c, false);
        }

        /// <summary>列の幅を中身に合わせる。shrink なら既定の幅より狭くもする（見出しの境目のダブルクリック）。</summary>
        private void FitColumn(int column, bool shrink)
        {
            double widest = 0;
            var rows = Math.Min(_data.RowCount, 1000);

            for (var r = 0; r < rows; r++)
            {
                var text = _data.Get(r, column);
                if (text.Length == 0) continue;

                var display = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                widest = Math.Max(widest, MakeText(display, r == 0, Palette.Text).WidthIncludingTrailingWhitespace);
            }

            var width = Math.Ceiling(widest + CellPadding * 2 + 2);
            width = Math.Min(MaxFitWidth, Math.Max(shrink ? MinColumnWidth * 2 : TableData.DefaultWidth, width));
            _data.SetWidth(column, width);
        }

        // =====================================================================
        // マウス
        // =====================================================================

        private Hit HitTest(Point point, out int row, out int column)
        {
            row = RowAt(point.Y);
            column = ColumnAt(point.X);

            var rowHeader = RowHeaderWidth;

            if (point.Y < HeaderHeight)
            {
                if (point.X < rowHeader) return Hit.Corner;

                // 列の見出しの境目（右端の前後 4px）は、幅を変えるつまみ。
                var x = ColumnX(column);
                if (column < _data.ColumnCount && x + _data.WidthOf(column) - point.X <= 4) return Hit.ColumnEdge;
                if (point.X - x <= 4 && column > _leftColumn && column - 1 < _data.ColumnCount)
                {
                    column--;
                    return Hit.ColumnEdge;
                }

                return column < _data.ColumnCount ? Hit.ColumnHeader : Hit.None;   // 表の外は何もしない
            }

            if (point.X < rowHeader) return row < _data.RowCount ? Hit.RowHeader : Hit.None;

            if (_mode == Mode.Ready)
            {
                var handle = HandleRect();
                handle.Inflate(3, 3);
                if (handle.Contains(point)) return Hit.Handle;
            }

            // 表の外（行・列の無い所）を押しても、選ばない（押した所まで表が広がらないように）。
            return row < _data.RowCount && column < _data.ColumnCount ? Hit.Cell : Hit.None;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            FocusEditor();
            if (_mode != Mode.Ready) CommitEdit(false);

            var point = e.GetPosition(_surface);
            int row, column;
            var hit = HitTest(point, out row, out column);
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (hit)
            {
                case Hit.Corner:
                    SelectAll();
                    break;

                case Hit.ColumnEdge:
                    if (e.ClickCount == 2)
                    {
                        FitColumn(column, true);   // ダブルクリックで中身に合わせる
                    }
                    else
                    {
                        _drag = Drag.Resize;
                        _resizeColumn = column;
                        _resizeStart = point.X;
                        _resizeWidth = _data.WidthOf(column);
                    }
                    break;

                case Hit.ColumnHeader:
                    if (!shift)
                    {
                        _activeRow = 0;
                        _activeColumn = column;
                    }
                    _extentRow = _data.RowCount - 1;
                    _extentColumn = column;
                    _drag = Drag.Columns;
                    break;

                case Hit.RowHeader:
                    if (!shift)
                    {
                        _activeRow = row;
                        _activeColumn = 0;
                    }
                    _extentRow = row;
                    _extentColumn = _data.ColumnCount - 1;
                    _drag = Drag.Rows;
                    break;

                case Hit.Handle:
                    _drag = Drag.Fill;
                    _fillValid = false;
                    break;

                case Hit.Cell:
                    if (shift)
                    {
                        _extentRow = row;
                        _extentColumn = column;
                    }
                    else
                    {
                        _activeRow = _extentRow = row;
                        _activeColumn = _extentColumn = column;
                    }

                    if (e.ClickCount == 2 && !shift)
                    {
                        BeginEdit(Mode.Edit, _data.Get(row, column));   // ダブルクリックは書き足す
                        e.Handled = true;
                        return;
                    }

                    _drag = Drag.Cells;
                    break;
            }

            if (_drag != Drag.None)
            {
                _dragPoint = point;
                _surface.CaptureMouse();
                if (_drag != Drag.Resize) _dragTimer.Start();
            }

            UpdateView();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var point = e.GetPosition(_surface);

            if (_drag == Drag.None)
            {
                int row, column;
                var hit = HitTest(point, out row, out column);
                _surface.Cursor = hit == Hit.ColumnEdge ? Cursors.SizeWE : hit == Hit.Handle ? Cursors.Cross : null;
                return;
            }

            _dragPoint = point;
            DragTo(point);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_drag == Drag.Fill) ApplyFill();
            _surface.ReleaseMouseCapture();
            EndDrag();
            UpdateView();
        }

        private void EndDrag()
        {
            _drag = Drag.None;
            _fillValid = false;
            _dragTimer.Stop();
        }

        /// <summary>つかんでいる間に、押さえている所まで範囲を広げる（列の幅を変える・埋める先を決める）。</summary>
        private void DragTo(Point point)
        {
            if (_drag == Drag.Resize)
            {
                _data.SetWidth(_resizeColumn, Math.Max(MinColumnWidth, _resizeWidth + point.X - _resizeStart));
                UpdateView();
                return;
            }

            // 面の外を押さえていても、見えている端のセルとして扱う（送るのは AutoScroll）。
            var x = Math.Max(RowHeaderWidth, Math.Min(point.X, _surface.ActualWidth - 1));
            var y = Math.Max(HeaderHeight, Math.Min(point.Y, _surface.ActualHeight - 1));
            var row = Math.Max(0, RowAt(y));
            var column = Math.Max(0, ColumnAt(x));

            switch (_drag)
            {
                case Drag.Cells:
                    _extentRow = Math.Min(row, RowLimit);
                    _extentColumn = Math.Min(column, ColumnLimit);
                    break;

                case Drag.Columns:
                    _extentColumn = Math.Min(column, ColumnLimit);
                    break;

                case Drag.Rows:
                    _extentRow = Math.Min(row, RowLimit);
                    break;

                case Drag.Fill:
                    // 埋める先は、空きの行・列より先まで引っぱれる（引っぱった分だけ表を広げる）。
                    UpdateFillTarget(row, column);
                    break;
            }

            UpdateView();
        }

        private void AutoScroll()
        {
            if (_drag == Drag.None || _drag == Drag.Resize) return;

            // 表の端まで見えていれば、それ以上は送らない。埋める先を引っぱっているときは、表が広がるので送れる。
            var moved = false;
            if (_dragPoint.Y > _surface.ActualHeight && (_topRow + VisibleRows < _data.RowCount || _drag == Drag.Fill))
            {
                _topRow++;
                moved = true;
            }
            else if (_dragPoint.Y < HeaderHeight && _topRow > 0)
            {
                _topRow--;
                moved = true;
            }

            var lastColumn = _data.ColumnCount - 1;
            if (_dragPoint.X > _surface.ActualWidth
                && (ColumnX(lastColumn) + _data.WidthOf(lastColumn) > _surface.ActualWidth || _drag == Drag.Fill))
            {
                _leftColumn++;
                moved = true;
            }
            else if (_dragPoint.X < RowHeaderWidth && _leftColumn > 0)
            {
                _leftColumn--;
                moved = true;
            }

            if (moved) DragTo(_dragPoint);
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                _leftColumn = Math.Max(0, _leftColumn + (e.Delta > 0 ? -1 : 1));
            else
                _topRow = Math.Max(0, _topRow + (e.Delta > 0 ? -3 : 3));

            UpdateView();
            e.Handled = true;
        }

        // =====================================================================
        // キー
        // =====================================================================

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 日本語の変換中のキーは、変換に任せる。
            if (e.Key == Key.ImeProcessed) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = Keyboard.Modifiers;
            var ctrl = (modifiers & ModifierKeys.Control) != 0;
            var shift = (modifiers & ModifierKeys.Shift) != 0;
            var alt = (modifiers & ModifierKeys.Alt) != 0;

            e.Handled = _mode == Mode.Ready ? ReadyKey(key, ctrl, shift, alt) : EditingKey(key, ctrl, shift, alt);
        }

        /// <summary>打っていないときのキー。拾ったら true。拾わない字は欄に入り、打ち始めになる（OnEditorTextChanged）。</summary>
        private bool ReadyKey(Key key, bool ctrl, bool shift, bool alt)
        {
            if (alt) return false;   // Alt+S・Alt+X はウィンドウのボタンへ

            switch (key)
            {
                case Key.Up: Move(-1, 0, shift, ctrl); return true;
                case Key.Down: Move(1, 0, shift, ctrl); return true;
                case Key.Left: Move(0, -1, shift, ctrl); return true;
                case Key.Right: Move(0, 1, shift, ctrl); return true;
                case Key.PageUp: Move(-VisibleRows, 0, shift, false); return true;
                case Key.PageDown: Move(VisibleRows, 0, shift, false); return true;

                case Key.Enter: Move(shift ? -1 : 1, 0, false, false); return true;
                case Key.Tab: Move(0, shift ? -1 : 1, false, false); return true;

                case Key.Home:
                    if (ctrl) MoveTo(0, 0);
                    else MoveTo(_activeRow, 0);
                    return true;

                case Key.End:
                    if (ctrl)
                    {
                        int lastRow, lastColumn;
                        _data.UsedRange(out lastRow, out lastColumn);
                        MoveTo(Math.Max(0, lastRow), Math.Max(0, lastColumn));
                    }
                    return true;

                case Key.F2:
                    BeginEdit(Mode.Edit, _data.Get(_activeRow, _activeColumn));
                    return true;

                case Key.Delete:
                    ClearSelection();
                    return true;

                case Key.Back:
                    BeginEdit(Mode.Enter, string.Empty);   // 消して打ち直す（Excel と同じ）
                    return true;

                case Key.Escape:
                    ClearMarquee();
                    return true;

            }

            if (!ctrl) return false;

            switch (key)
            {
                case Key.C: Copy(false); return true;
                case Key.X: Copy(true); return true;
                case Key.V: Paste(); return true;
                case Key.Z: Undo(); return true;
                case Key.Y: Redo(); return true;
                case Key.A: SelectAll(); return true;
                case Key.D: FillDown(); return true;
                case Key.R: FillRight(); return true;
                case Key.Enter: return true;
            }

            return false;
        }

        /// <summary>打っている間のキー。拾ったら true。</summary>
        private bool EditingKey(Key key, bool ctrl, bool shift, bool alt)
        {
            switch (key)
            {
                case Key.Enter:
                    if (alt)
                    {
                        // セルの中の改行（Excel と同じ Alt+Enter）。書き戻すときは <br> になる。
                        _editor.SelectedText = "\n";
                        _editor.CaretIndex = _editor.SelectionStart + _editor.SelectionLength;
                        _editor.SelectionLength = 0;
                        return true;
                    }

                    if (ctrl)
                    {
                        CommitEdit(true);   // 選んでいる範囲のすべてのセルに同じ値を入れる（Excel と同じ）
                        return true;
                    }

                    CommitEdit(false);
                    Move(shift ? -1 : 1, 0, false, false);
                    return true;

                case Key.Tab:
                    CommitEdit(false);
                    Move(0, shift ? -1 : 1, false, false);
                    return true;

                case Key.Escape:
                    CancelEdit();
                    return true;

                case Key.F2:
                    _mode = _mode == Mode.Enter ? Mode.Edit : Mode.Enter;   // 矢印で動くか、欄の中を動くかを切り替える
                    return true;

                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                    if (_mode == Mode.Edit) return false;   // F2・ダブルクリックで始めたときは、欄の中のカーソルを動かす

                    CommitEdit(false);
                    Move(key == Key.Up ? -1 : key == Key.Down ? 1 : 0, key == Key.Left ? -1 : key == Key.Right ? 1 : 0, shift, ctrl);
                    return true;
            }

            return false;
        }

        /// <summary>打っていないときに欄へ字が入った（日本語の変換の始まりも含む）。そこから打ち始めにする。</summary>
        private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_quiet || _mode != Mode.Ready || _editor.Text.Length == 0) return;

            _mode = Mode.Enter;
            ClearMarquee();
            EnsureVisible(_activeRow, _activeColumn);
            UpdateView();
        }

        // =====================================================================
        // 動く・選ぶ
        // =====================================================================

        private void Move(int rows, int columns, bool extend, bool jump)
        {
            var row = extend ? _extentRow : _activeRow;
            var column = extend ? _extentColumn : _activeColumn;

            if (jump)
            {
                if (rows != 0) row = JumpRow(row, column, Math.Sign(rows));
                if (columns != 0) column = JumpColumn(row, column, Math.Sign(columns));
            }
            else
            {
                row += rows;
                column += columns;
            }

            // 動けるのは空きの行・列まで（その先は表の外）。空きに打つと表に入り、その先にまた空きができる（Normalize）。
            row = Math.Max(0, Math.Min(row, RowLimit));
            column = Math.Max(0, Math.Min(column, ColumnLimit));

            if (extend)
            {
                _extentRow = row;
                _extentColumn = column;
            }
            else
            {
                _activeRow = _extentRow = row;
                _activeColumn = _extentColumn = column;
            }

            EnsureVisible(row, column);
            UpdateView();
        }

        private void MoveTo(int row, int column)
        {
            _activeRow = _extentRow = row;
            _activeColumn = _extentColumn = column;
            EnsureVisible(row, column);
            UpdateView();
        }

        /// <summary>Ctrl+矢印（Excel と同じ）。字の続く所の端へ。今が字の続きの端なら、次に字のある所へ。無ければ表の端へ。</summary>
        private int JumpRow(int row, int column, int step)
        {
            Func<int, bool> filled = r => _data.Get(r, column).Length > 0;
            var next = row + step;
            if (next < 0) return 0;

            if (filled(row) && filled(next))
            {
                while (next + step >= 0 && next + step < _data.RowCount && filled(next + step)) next += step;
                return next;
            }

            for (var r = next; r >= 0 && r < _data.RowCount; r += step)
            {
                if (filled(r)) return r;
            }

            return step < 0 ? 0 : _data.RowCount - 1;
        }

        private int JumpColumn(int row, int column, int step)
        {
            Func<int, bool> filled = c => _data.Get(row, c).Length > 0;
            var next = column + step;
            if (next < 0) return 0;

            if (filled(column) && filled(next))
            {
                while (next + step >= 0 && next + step < _data.ColumnCount && filled(next + step)) next += step;
                return next;
            }

            for (var c = next; c >= 0 && c < _data.ColumnCount; c += step)
            {
                if (filled(c)) return c;
            }

            return step < 0 ? 0 : _data.ColumnCount - 1;
        }

        /// <summary>字のある範囲を選ぶ（Ctrl+A・左上の角）。字が無ければ今のセルだけ。</summary>
        private void SelectAll()
        {
            int lastRow, lastColumn;
            _data.UsedRange(out lastRow, out lastColumn);

            _activeRow = 0;
            _activeColumn = 0;
            _extentRow = Math.Max(0, lastRow);
            _extentColumn = Math.Max(0, lastColumn);
            UpdateView();
        }

        // =====================================================================
        // 打つ
        // =====================================================================

        private void BeginEdit(Mode mode, string text)
        {
            _mode = mode;
            ClearMarquee();

            _quiet = true;
            _editor.Text = text;
            _editor.CaretIndex = text.Length;
            _quiet = false;

            EnsureVisible(_activeRow, _activeColumn);
            UpdateView();
        }

        /// <summary>打った字をセルに入れる。toSelection なら、選んでいる範囲のすべてのセルに入れる（Ctrl+Enter）。</summary>
        private void CommitEdit(bool toSelection)
        {
            if (_mode == Mode.Ready) return;

            var value = _editor.Text;

            int top, left, bottom, right;
            if (toSelection) Bounds(out top, out left, out bottom, out right);
            else
            {
                top = bottom = _activeRow;
                left = right = _activeColumn;
            }

            if (AnyDifferent(top, left, bottom, right, value))
            {
                PushUndo();
                for (var r = top; r <= bottom; r++)
                {
                    for (var c = left; c <= right; c++) _data.Set(r, c, value);
                }
            }

            ResetEditor();
        }

        private bool AnyDifferent(int top, int left, int bottom, int right, string value)
        {
            for (var r = top; r <= bottom; r++)
            {
                for (var c = left; c <= right; c++)
                {
                    if (_data.Get(r, c) != value) return true;
                }
            }

            return false;
        }

        private void CancelEdit()
        {
            ResetEditor();
        }

        private void ResetEditor()
        {
            _mode = Mode.Ready;

            _quiet = true;
            _editor.Text = string.Empty;
            _quiet = false;

            UpdateView();
        }

        // =====================================================================
        // 消す・埋める
        // =====================================================================

        /// <summary>選んでいる範囲の値を消す（Delete。書式にあたる列の寄せ・幅は残す）。</summary>
        private void ClearSelection()
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);
            bottom = Math.Min(bottom, _data.RowCount - 1);
            right = Math.Min(right, _data.ColumnCount - 1);

            if (!AnyDifferent(top, left, bottom, right, string.Empty)) return;

            PushUndo();
            for (var r = top; r <= bottom; r++)
            {
                for (var c = left; c <= right; c++) _data.Set(r, c, string.Empty);
            }

            ClearMarquee();
            UpdateView();
        }

        /// <summary>Ctrl+D。選んだ範囲の一番上の行を、下の行へ写す。1行だけなら、上の行から写す（Excel と同じ）。</summary>
        private void FillDown()
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            var from = top == bottom ? top - 1 : top;
            if (from < 0) return;

            PushUndo();
            for (var c = left; c <= right; c++)
            {
                var value = _data.Get(from, c);
                for (var r = from + 1; r <= bottom; r++) _data.Set(r, c, value);
            }

            ClearMarquee();
            UpdateView();
        }

        /// <summary>Ctrl+R。選んだ範囲の一番左の列を、右の列へ写す。1列だけなら、左の列から写す（Excel と同じ）。</summary>
        private void FillRight()
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            var from = left == right ? left - 1 : left;
            if (from < 0) return;

            PushUndo();
            for (var r = top; r <= bottom; r++)
            {
                var value = _data.Get(r, from);
                for (var c = from + 1; c <= right; c++) _data.Set(r, c, value);
            }

            ClearMarquee();
            UpdateView();
        }

        /// <summary>オートフィルのつまみを押さえている所から、埋める先と向きを決める。上下・左右のうち、離れているほうへ埋める。</summary>
        private void UpdateFillTarget(int row, int column)
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            var down = row > bottom ? row - bottom : row < top ? row - top : 0;
            var across = column > right ? column - right : column < left ? column - left : 0;

            _fillTop = top;
            _fillLeft = left;
            _fillBottom = bottom;
            _fillRight = right;
            _fillValid = down != 0 || across != 0;
            if (!_fillValid) return;

            if (Math.Abs(down) >= Math.Abs(across))
            {
                _fillDirection = down > 0 ? FillDirection.Down : FillDirection.Up;
                if (down > 0) _fillBottom = row;
                else _fillTop = row;
            }
            else
            {
                _fillDirection = across > 0 ? FillDirection.Right : FillDirection.Left;
                if (across > 0) _fillRight = column;
                else _fillLeft = column;
            }
        }

        /// <summary>オートフィルで埋める（FillSeries の続け方で）。埋めたあとは、埋めた所まで含めて選ぶ。</summary>
        private void ApplyFill()
        {
            if (!_fillValid) return;

            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            PushUndo();

            switch (_fillDirection)
            {
                case FillDirection.Down:
                case FillDirection.Up:
                    for (var c = left; c <= right; c++)
                    {
                        var source = new List<string>();
                        for (var r = top; r <= bottom; r++) source.Add(_data.Get(r, c));

                        if (_fillDirection == FillDirection.Down)
                        {
                            var values = FillSeries.Extend(source, _fillBottom - bottom, false);
                            for (var k = 0; k < values.Count; k++) _data.Set(bottom + 1 + k, c, values[k]);
                        }
                        else
                        {
                            var values = FillSeries.Extend(source, top - _fillTop, true);
                            for (var k = 0; k < values.Count; k++) _data.Set(top - 1 - k, c, values[k]);
                        }
                    }
                    break;

                default:
                    for (var r = top; r <= bottom; r++)
                    {
                        var source = new List<string>();
                        for (var c = left; c <= right; c++) source.Add(_data.Get(r, c));

                        if (_fillDirection == FillDirection.Right)
                        {
                            var values = FillSeries.Extend(source, _fillRight - right, false);
                            for (var k = 0; k < values.Count; k++) _data.Set(r, right + 1 + k, values[k]);
                        }
                        else
                        {
                            var values = FillSeries.Extend(source, left - _fillLeft, true);
                            for (var k = 0; k < values.Count; k++) _data.Set(r, left - 1 - k, values[k]);
                        }
                    }
                    break;
            }

            // 今のセルは元の範囲の角のまま、埋めた所の端までを選ぶ。
            if (_fillDirection == FillDirection.Down || _fillDirection == FillDirection.Right)
            {
                _activeRow = top;
                _activeColumn = left;
                _extentRow = _fillBottom;
                _extentColumn = _fillRight;
            }
            else
            {
                _activeRow = bottom;
                _activeColumn = right;
                _extentRow = _fillTop;
                _extentColumn = _fillLeft;
            }

            ClearMarquee();
        }

        // =====================================================================
        // コピー・切り取り・貼り付け（Excel とタブ区切りでやり取りする）
        // =====================================================================

        private void Copy(bool cut)
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            // 列・行をまるごと選んでいても、字のある所までにする。
            int lastRow, lastColumn;
            _data.UsedRange(out lastRow, out lastColumn);
            bottom = Math.Min(bottom, Math.Max(top, lastRow));
            right = Math.Min(right, Math.Max(left, lastColumn));

            var rows = new List<List<string>>();
            for (var r = top; r <= bottom; r++)
            {
                var row = new List<string>();
                for (var c = left; c <= right; c++) row.Add(_data.Get(r, c));
                rows.Add(row);
            }

            var text = Tsv.Write(rows);
            if (!SetClipboard(text)) return;

            _marquee = true;
            _marqueeTop = top;
            _marqueeLeft = left;
            _marqueeBottom = bottom;
            _marqueeRight = right;
            _cut = cut;
            _clipboardText = text;
            _marqueeTimer.Start();
            UpdateView();
        }

        private static bool SetClipboard(string text)
        {
            // ほかのアプリがクリップボードを開いていると失敗する。少し待って試し直す。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            MessageBox.Show("クリップボードに入れられませんでした。もう一度試してください。", "表エディタ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void Paste()
        {
            string text;
            try
            {
                if (!Clipboard.ContainsText()) return;
                text = Clipboard.GetText();
            }
            catch (Exception)
            {
                return;
            }

            var rows = Tsv.Read(text);
            if (rows.Count == 0) return;

            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            PushUndo();

            // このエディタで切り取った範囲なら、元の所を空にしてから入れる（動かす）。
            if (_marquee && _cut && text == _clipboardText)
            {
                for (var r = _marqueeTop; r <= _marqueeBottom; r++)
                {
                    for (var c = _marqueeLeft; c <= _marqueeRight; c++) _data.Set(r, c, string.Empty);
                }

                ClearMarquee();
            }
            else if (rows.Count == 1 && rows[0].Count == 1 && (top != bottom || left != right))
            {
                // 1つの値を、選んでいる範囲のすべてに入れる（Excel と同じ）。
                for (var r = top; r <= Math.Min(bottom, _data.RowCount - 1); r++)
                {
                    for (var c = left; c <= Math.Min(right, _data.ColumnCount - 1); c++) _data.Set(r, c, rows[0][0]);
                }

                UpdateView();
                return;
            }

            var width = 1;
            for (var i = 0; i < rows.Count; i++)
            {
                width = Math.Max(width, rows[i].Count);
                for (var j = 0; j < rows[i].Count; j++) _data.Set(top + i, left + j, rows[i][j]);
            }

            _activeRow = top;
            _activeColumn = left;
            _extentRow = top + rows.Count - 1;
            _extentColumn = left + width - 1;

            EnsureVisible(_activeRow, _activeColumn);
            UpdateView();
        }

        private void ClearMarquee()
        {
            if (!_marquee) return;

            _marquee = false;
            _cut = false;
            _marqueeTimer.Stop();
            _surface.InvalidateVisual();
        }

        // =====================================================================
        // 列の寄せ（ウィンドウの「寄せ」ボタンから。Markdown の表の寄せは列ごとなので、選んでいる列すべてに入れる）
        // 右クリックのメニューは持たない（2026-10-18。切り取り・コピー・貼り付けは Ctrl+X・C・V だけ）
        // =====================================================================

        /// <summary>選んでいる列の寄せ（N・L・C・R）。列によって違えば '?'。</summary>
        public char CurrentAlign()
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            var current = _data.AlignOf(left);
            for (var c = left + 1; c <= right; c++)
            {
                if (_data.AlignOf(c) != current) return '?';
            }

            return current;
        }

        /// <summary>選んでいる列すべての寄せを変える（N・L・C・R）。打っている途中なら先に確定する。</summary>
        public void SetAlign(char align)
        {
            CommitEdit(false);
            ApplyAlign(align);
        }

        private void ApplyAlign(char align)
        {
            int top, left, bottom, right;
            Bounds(out top, out left, out bottom, out right);

            PushUndo();
            for (var c = left; c <= right; c++) _data.SetAlign(c, align);
            UpdateView();
        }

        // =====================================================================
        // 元に戻す・やり直す（中身をまるごと覚える。Markdown の表は小さいので、これで足りる）
        // =====================================================================

        private Snapshot Capture()
        {
            return new Snapshot
            {
                Data = _data.Clone(),
                ActiveRow = _activeRow,
                ActiveColumn = _activeColumn,
                ExtentRow = _extentRow,
                ExtentColumn = _extentColumn,
            };
        }

        /// <summary>書き換える前に呼ぶ。</summary>
        private void PushUndo()
        {
            _undo.Add(Capture());
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
            _redo.Clear();
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
            _data.CopyFrom(snapshot.Data);
            _activeRow = snapshot.ActiveRow;
            _activeColumn = snapshot.ActiveColumn;
            _extentRow = snapshot.ExtentRow;
            _extentColumn = snapshot.ExtentColumn;

            ClearMarquee();
            EnsureVisible(_activeRow, _activeColumn);
            UpdateView();
        }
    }
}
