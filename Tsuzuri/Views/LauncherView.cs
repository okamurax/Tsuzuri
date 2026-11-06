using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Tsuzuri.Views
{
    /// <summary>
    /// ランチャー（SPEC 4章）。前のブックマーク（縦1列）を置き換える、起動の起点。
    ///
    /// マス目に、ファイル・フォルダ・ショートカット（.lnk）・バッチなどを登録する。
    /// 落としたマスに入るので、場所で用途を分けられる（CLaunch と同じ考え方）。
    /// Mac のアプリは、Parallels が Windows に作る共有ショートカット（.lnk）を登録すれば、ほかと同じく起動できる。
    ///
    /// マス目に決まった大きさは無い。登録のある一番右・一番下の外側に、いつも空きの列と段を1つずつ出す。
    /// そこへ落とせば広がるので、いくらでも足せる。画面より小さいうちは、空きマスで画面を埋める。
    /// 空きマスは描かない。落とす先だけ青い枠で示す。
    /// ウィンドウの幅が変わっても回り込ませない（場所で覚えている用途の並びが崩れるため）。はみ出したらスクロール。
    ///
    /// ファイラーと一緒の画面の左側に置く（MainWindow の LauncherHost）。その画面は、本文の場所の上半分に重ねて出す（2026-10-31）。
    /// 下半分のエディタは見えたままで、ファイラーからドラッグできる。
    /// </summary>
    public sealed class LauncherView : DockPanel
    {
        // アイコン（32px）と2行までの名前が収まる大きさ。間は詰める（2026-10-11）。
        private const double TileWidth = 88;
        private const double TileHeight = 80;   // 名前を2行まで出す（2026-10-12 に3行から戻した）

        // 名前の1行の高さ。行の高さを決めておき、ちょうど2行で切る。
        private const double NameLineHeight = 15;
        private const int NameLines = 2;
        private const double Gap = 4;
        private const double Margin0 = 16;

        // スクロールバーの分の空き。画面にマス目がいくつ収まるかを見るときに引く（Rebuild の fitColumns・fitRows）。
        private const double ScrollAllowance = 12;

        private const double EdgeZone = 40;
        private const double EdgeStep = 20;

        private readonly AppSettings _settings;
        private readonly Action<string> _open;
        private readonly Action<string> _status;
        private readonly Action<string> _showInFiler;

        private readonly Canvas _canvas = new Canvas();
        private readonly ScrollViewer _scroll = new ScrollViewer();

        // 登録のあるマスだけ部品を作る。空きマスは何も描かない（枠も出さない）。
        // マス目はいくらでも広がるので、空きマスまで部品にすると、広げるほど重くなる。
        private readonly Dictionary<Cell, Border> _cells = new Dictionary<Cell, Border>();

        /// <summary>落とす先に出す青い枠。1つだけ用意して、その時のマスへ動かす。</summary>
        private readonly Border _target = new Border();
        private int _columns;
        private int _rows;


        // つかんで動かしているマス。
        private Cell? _dragFrom;
        private Point _dragStart;
        private bool _dragMoved;
        private bool _released;
        private Cell? _dropOn;

        // 外からファイルを落とそうとしているマス。
        private Cell? _hoverDrop;

        // つかんだまま（または外から落とす途中で）画面の端に寄せている間、少しずつスクロールする。
        private readonly DispatcherTimer _autoScroll = new DispatcherTimer();

        /// <summary>マスの位置。</summary>
        private struct Cell : IEquatable<Cell>
        {
            public readonly int Column;
            public readonly int Row;

            public Cell(int column, int row)
            {
                Column = column;
                Row = row;
            }

            public bool Equals(Cell other)
            {
                return Column == other.Column && Row == other.Row;
            }

            public override bool Equals(object obj)
            {
                return obj is Cell && Equals((Cell)obj);
            }

            public override int GetHashCode()
            {
                return Column * 1009 + Row;
            }
        }

        /// <param name="showInFiler">そのフォルダを右のファイラーで開く。</param>
        public LauncherView(AppSettings settings, Action<string> open, Action<string> status, Action<string> showInFiler)
        {
            _settings = settings;
            _open = open;
            _status = status;
            _showInFiler = showInFiler;

            LastChildFill = true;
            Background = Resource("SidebarBackground");   // アウトラインと同じ地（SPEC 4章）

            // 見出しの段は持たない。ツールバーの「ランチャー」をもう一度押すか、外を押すと閉じる（SPEC 4章。
            // 前はページの上端の段の右端に「ランチャー」があった）。

            _canvas.HorizontalAlignment = HorizontalAlignment.Left;
            _canvas.VerticalAlignment = VerticalAlignment.Top;
            _canvas.Background = Brushes.Transparent;
            _canvas.AllowDrop = true;
            _canvas.DragOver += OnDragOver;
            _canvas.DragLeave += delegate
            {
                ShowDropTarget(null);
                _autoScroll.Stop();
            };
            _canvas.Drop += OnDrop;

            // 空いているマスの右クリックで、ダイアログから追加する（登録のあるマスは自分のメニューを出す）。
            _canvas.MouseRightButtonUp += OnEmptyRightClick;

            _scroll.Content = _canvas;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // 画面が広がったら、空きマスで埋め直す。
            _scroll.SizeChanged += delegate { if (_dragFrom == null) Rebuild(); };
            Children.Add(_scroll);

            _autoScroll.Interval = TimeSpan.FromMilliseconds(30);
            _autoScroll.Tick += delegate { AutoScroll(); };

            _target.Width = TileWidth;
            _target.Height = TileHeight;
            _target.CornerRadius = new CornerRadius(6);
            _target.BorderThickness = new Thickness(1);
            _target.BorderBrush = Resource("AccentBrush");
            _target.Background = Resource("AccentFill");
            _target.Opacity = 0.85;
            _target.IsHitTestVisible = false;
            _target.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(_target, 2);

            Rebuild();
        }

        /// <summary>ランチャーを閉じる。つかんでいる最中なら放す。</summary>
        public void Close()
        {
            _autoScroll.Stop();

            if (_dragFrom != null)
            {
                _dragFrom = null;
                Mouse.Capture(null);
            }
        }

        // =====================================================================
        // マス目
        // =====================================================================

        private static Point Position(Cell cell)
        {
            return new Point(Margin0 + cell.Column * (TileWidth + Gap), Margin0 + cell.Row * (TileHeight + Gap));
        }

        /// <summary>置き場の座標から、その下のマス。並べている範囲の外なら null。</summary>
        private Cell? CellAt(Point at)
        {
            var column = (int)Math.Floor((at.X - Margin0 + Gap / 2) / (TileWidth + Gap));
            var row = (int)Math.Floor((at.Y - Margin0 + Gap / 2) / (TileHeight + Gap));

            if (column < 0 || column >= _columns || row < 0 || row >= _rows) return null;
            return new Cell(column, row);
        }

        /// <summary>登録が外で変わったとき（ファイラーで名前を変えて、登録のパスを直したとき）に、マス目を作り直す。</summary>
        public void Refresh()
        {
            Rebuild();
        }

        /// <summary>
        /// 並べる範囲を決めて、マスを作り直す。
        /// 登録のある一番右・一番下の外側に空きを1つずつ。画面より狭ければ、画面いっぱいまで空きで埋める。
        /// </summary>
        private void Rebuild()
        {
            // 名前を打っている途中なら、打ちかけで決めずにやめる。作り直すと欄が外れてフォーカスを失い、
            // 「外を押したら決める」で打ちかけの名前に変わってしまうため（ウィンドウの幅が変わったときなど）。
            var cancel = _cancelRename;
            _cancelRename = null;
            if (cancel != null) cancel();

            var usedColumns = _settings.Tiles.Count == 0 ? 0 : _settings.Tiles.Max(tile => tile.Column) + 1;
            var usedRows = _settings.Tiles.Count == 0 ? 0 : _settings.Tiles.Max(tile => tile.Row) + 1;

            var fitColumns = (int)Math.Floor((Math.Max(0, _scroll.ActualWidth - ScrollAllowance) - Margin0 * 2 + Gap) / (TileWidth + Gap));
            var fitRows = (int)Math.Floor((Math.Max(0, _scroll.ActualHeight - ScrollAllowance) - Margin0 * 2 + Gap) / (TileHeight + Gap));

            _columns = Math.Min(LauncherTile.Limit + 1, Math.Max(Math.Max(usedColumns + 1, fitColumns), 1));
            _rows = Math.Min(LauncherTile.Limit + 1, Math.Max(Math.Max(usedRows + 1, fitRows), 1));

            _canvas.Children.Clear();
            _cells.Clear();

            _canvas.Children.Add(_target);

            foreach (var tile in _settings.Tiles)
            {
                var cell = new Cell(tile.Column, tile.Row);
                var border = CreateCell(cell);
                _cells[cell] = border;
                _canvas.Children.Add(border);
                Fill(cell);
            }

            ShowTarget();

            _canvas.Width = Margin0 * 2 + _columns * TileWidth + (_columns - 1) * Gap;
            _canvas.Height = Margin0 * 2 + _rows * TileHeight + (_rows - 1) * Gap;
        }

        private Border CreateCell(Cell cell)
        {
            var border = new Border();
            border.Width = TileWidth;
            border.Height = TileHeight;
            border.CornerRadius = new CornerRadius(6);
            border.BorderThickness = new Thickness(1);

            var at = Position(cell);
            Canvas.SetLeft(border, at.X);
            Canvas.SetTop(border, at.Y);

            border.MouseEnter += delegate { if (_dragFrom == null) Paint(cell); };
            border.MouseLeave += delegate { if (_dragFrom == null) Paint(cell); };
            border.MouseLeftButtonDown += (sender, e) => Cell_MouseDown(cell, border, e);
            border.MouseMove += (sender, e) => Cell_MouseMove(e);
            border.MouseLeftButtonUp += (sender, e) => Cell_MouseUp(border, e);
            border.LostMouseCapture += delegate { EndDrag(); };
            border.MouseRightButtonUp += (sender, e) => ShowMenu(cell, border, e);

            return border;
        }

        private void Fill(Cell cell)
        {
            Border border;
            if (!_cells.TryGetValue(cell, out border)) return;

            var tile = _settings.TileAt(cell.Column, cell.Row);

            if (tile == null) return;   // 登録の無いマスに部品は無い

            var exists = Exists(tile.Path);

            var icon = new Image();
            icon.Width = 32;
            icon.Height = 32;
            icon.Source = IconOf(tile);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 8, 0, 4);
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            var name = new TextBlock();
            name.Text = tile.DisplayName;
            name.Foreground = Resource("SidebarText");
            name.FontSize = 12;
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.LineHeight = NameLineHeight;
            name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            name.MaxHeight = NameLineHeight * NameLines;   // 2行まで。入れた改行はそのまま効く
            name.Margin = new Thickness(4, 0, 4, 0);

            var stack = new StackPanel();
            stack.Children.Add(icon);
            stack.Children.Add(name);

            border.Child = stack;
            border.Cursor = Cursors.Hand;
            border.Opacity = exists ? 1 : 0.45;
            border.ToolTip = exists ? tile.Path : "見つかりません: " + tile.Path;
            Paint(cell);
        }

        /// <summary>
        /// 登録のあるマスの地。マウスを載せると明るく。
        /// 右クリックのメニューを出している間は、どのマスのメニューか分かるよう青い枠で示す（落とす先と同じ形）。
        /// </summary>
        private void Paint(Cell cell)
        {
            Border border;
            if (!_cells.TryGetValue(cell, out border)) return;

            if (_menuCell.HasValue && _menuCell.Value.Equals(cell))
            {
                border.Background = Resource("AccentFill");
                border.BorderBrush = Resource("AccentBrush");
                return;
            }

            border.Background = border.IsMouseOver && _dragFrom == null ? Resource("HoverFill") : Brushes.Transparent;
            border.BorderBrush = Brushes.Transparent;
        }

        // 右クリックのメニューを出している、登録のあるマス。
        private Cell? _menuCell;

        /// <summary>落とす先（つかんで動かしている先か、外から落とそうとしている先）に青い枠を出す。</summary>
        private void ShowTarget()
        {
            var at = _hoverDrop ?? (_dragMoved ? _dropOn : null);

            if (!at.HasValue)
            {
                _target.Visibility = Visibility.Collapsed;
                return;
            }

            var position = Position(at.Value);
            Canvas.SetLeft(_target, position.X);
            Canvas.SetTop(_target, position.Y);
            _target.Visibility = Visibility.Visible;
        }

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // =====================================================================
        // つかんで動かす・クリックで起動する
        // =====================================================================

        private void Cell_MouseDown(Cell cell, Border border, MouseButtonEventArgs e)
        {
            if (_settings.TileAt(cell.Column, cell.Row) == null) return;

            e.Handled = true;

            _dragFrom = cell;
            _dragStart = e.GetPosition(_canvas);
            _dragMoved = false;
            _released = false;
            _dropOn = null;

            border.CaptureMouse();
        }

        private void Cell_MouseMove(MouseEventArgs e)
        {
            if (_dragFrom == null || e.LeftButton != MouseButtonState.Pressed) return;

            DragTo(e.GetPosition(_canvas));

            if (_dragMoved && !_autoScroll.IsEnabled && EdgeDirection() != new Vector(0, 0)) _autoScroll.Start();
        }

        private void DragTo(Point now)
        {
            // 押しただけで少し動いてしまうのを、動かしたことにしない（クリックで起動するため）。
            if (!_dragMoved && Math.Abs(now.X - _dragStart.X) < 6 && Math.Abs(now.Y - _dragStart.Y) < 6) return;

            if (!_dragMoved)
            {
                _dragMoved = true;
                Border from;
                if (_cells.TryGetValue(_dragFrom.Value, out from)) from.Opacity = 0.5;
            }

            var target = CellAt(now);
            if (Nullable.Equals(target, _dropOn)) return;

            _dropOn = target;
            ShowTarget();
        }

        private void Cell_MouseUp(Border border, MouseButtonEventArgs e)
        {
            if (_dragFrom == null) return;

            e.Handled = true;
            _released = true;
            border.ReleaseMouseCapture();   // LostMouseCapture から EndDrag が呼ばれる
        }

        /// <summary>
        /// 放したとき。動かしていなければクリックとして起動する。動かしていれば、落としたマスへ移す（埋まっていれば入れ替え）。
        /// ボタンを離さずにつかみが外れた（別のウィンドウへ移った等）なら、何もしない。
        /// </summary>
        private void EndDrag()
        {
            if (_dragFrom == null) return;

            var from = _dragFrom.Value;
            var to = _dropOn;
            var moved = _dragMoved;
            var released = _released;

            _autoScroll.Stop();
            _dragFrom = null;
            _dropOn = null;
            _dragMoved = false;
            _released = false;

            if (!released || !moved || !to.HasValue || to.Value.Equals(from))
            {
                Fill(from);
                ShowTarget();

                if (released && !moved)
                {
                    var tile = _settings.TileAt(from.Column, from.Row);
                    if (tile != null) Launch(tile);
                }

                return;
            }

            _settings.SwapTiles(from.Column, from.Row, to.Value.Column, to.Value.Row);
            _settings.Save();

            // 端の空きに置いたら広がり、端から抜いたら縮むので、並べ直す。
            Rebuild();
        }

        private void Launch(LauncherTile tile)
        {
            if (!Exists(tile.Path))
            {
                // 利用者が登録したものなので、見つからなくても勝手に外さない（一時的に見えないだけかもしれない）。
                _status("見つかりません: " + tile.Path);
                return;
            }

            _open(tile.Path);
        }

        /// <summary>マウスが画面の端のどちら寄りか。端でなければ 0。</summary>
        private Vector EdgeDirection()
        {
            var at = Mouse.GetPosition(_scroll);
            return EdgeDirection(at);
        }

        private Vector EdgeDirection(Point at)
        {
            var x = at.X < EdgeZone ? -1 : at.X > _scroll.ViewportWidth - EdgeZone ? 1 : 0;
            var y = at.Y < EdgeZone ? -1 : at.Y > _scroll.ViewportHeight - EdgeZone ? 1 : 0;
            return new Vector(x, y);
        }

        private void AutoScroll()
        {
            if (_dragFrom == null || !_dragMoved)
            {
                _autoScroll.Stop();
                return;
            }

            var direction = EdgeDirection();
            if (direction == new Vector(0, 0))
            {
                _autoScroll.Stop();
                return;
            }

            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + direction.X * EdgeStep);
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + direction.Y * EdgeStep);
            _scroll.UpdateLayout();

            DragTo(Mouse.GetPosition(_canvas));
        }

        // =====================================================================
        // 外から落として登録する
        // =====================================================================

        private void OnDragOver(object sender, DragEventArgs e)
        {
            var paths = DroppedPaths(e);
            var cell = CellAt(e.GetPosition(_canvas));

            e.Effects = paths.Length > 0 && cell.HasValue ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;

            ShowDropTarget(paths.Length > 0 ? cell : null);

            // 外から運んでいる間は MouseMove が来ないので、DragOver のたびに端なら少し送る。
            var direction = EdgeDirection(e.GetPosition(_scroll));
            if (direction != new Vector(0, 0))
            {
                _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + direction.X * EdgeStep / 2);
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + direction.Y * EdgeStep / 2);
            }
        }

        private void ShowDropTarget(Cell? cell)
        {
            if (Nullable.Equals(cell, _hoverDrop)) return;

            _hoverDrop = cell;
            ShowTarget();
        }

        /// <summary>
        /// 外から落とされたファイルを、落としたマスから右へ順に空いているマスへ登録する。
        /// 埋まっているマスは上書きしない（登録を黙って消さないため）。
        /// </summary>
        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            var cell = CellAt(e.GetPosition(_canvas));
            ShowDropTarget(null);

            var paths = DroppedPaths(e);
            if (paths.Length == 0 || !cell.HasValue) return;

            if (paths.Length == 1 && _settings.TileAt(cell.Value.Column, cell.Value.Row) != null)
            {
                _status("そのマスには登録があります。空いているマスに落としてください（入れ替えはマスをつかんで動かす）。");
                return;
            }

            AddAt(cell.Value, paths);
        }

        /// <summary>
        /// start のマスから同じ段を右へ、空いているマスに順に登録する。埋まっているマスは上書きしない。
        /// 落としたときも、ダイアログで選んだときも、ここを通す。
        /// </summary>
        private void AddAt(Cell start, string[] paths)
        {
            // マス目に右端は無いので、上限に届かない限り全部入る。
            var column = start.Column;
            var added = 0;
            foreach (var path in paths)
            {
                while (column <= LauncherTile.Limit && _settings.TileAt(column, start.Row) != null) column++;
                if (column > LauncherTile.Limit) break;

                _settings.SetTile(column, start.Row, path, null);
                added++;
                column++;
            }

            if (added > 0)
            {
                _settings.Save();
                Rebuild();
            }

            _status(added == paths.Length
                ? (added == 1 ? "ランチャーに登録しました: " + paths[0] : added + " 件をランチャーに登録しました。")
                : added + " 件を登録しました。残り " + (paths.Length - added) + " 件は登録していません。");
        }

        /// <summary>落とされたもののうち、ファイルかフォルダとして在るもの。</summary>
        private static string[] DroppedPaths(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new string[0];

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            return paths == null ? new string[0] : paths.Where(Exists).ToArray();
        }

        // =====================================================================
        // 右クリックのメニュー
        // =====================================================================

        private void ShowMenu(Cell cell, Border border, MouseButtonEventArgs e)
        {
            var tile = _settings.TileAt(cell.Column, cell.Row);
            if (tile == null) return;

            e.Handled = true;

            // 並びと名前はファイラーの右クリックとそろえる（SPEC 4章・9章）。
            // 開く（メモ帳・場所）→ 名前・アイコンの変更 → ランチャーから外す
            var menu = new ContextMenu();

            // どのマスのメニューか分かるよう、先頭にその名前を押せない見出しとして出す（ファイラーと同じ形）。
            menu.Items.Add(FilerView.MenuTitle(tile.DisplayName));
            menu.Items.Add(new Separator());

            // ファイルの中身を手早く直すため（バッチ・設定ファイルなど）。中身が文字でないものとフォルダには出さない。
            if (ShellActions.CanOpenInNotepad(tile.Path))
            {
                var notepad = new MenuItem { Header = "メモ帳で開く" };
                notepad.Click += delegate { ShellActions.OpenInNotepad(tile.Path, false, _status); };
                menu.Items.Add(notepad);

                var elevated = new MenuItem { Header = "メモ帳で開く（管理者）" };
                elevated.Click += delegate { ShellActions.OpenInNotepad(tile.Path, true, _status); };
                menu.Items.Add(elevated);
            }

            // 「場所」は、そのファイル（フォルダ）が入っているフォルダ。
            // フォルダのマスには出さない。クリックすればそのフォルダがファイラーで開くので、それで足りる。
            if (!Directory.Exists(tile.Path))
            {
                var inFiler = new MenuItem { Header = "ファイラーで場所を開く" };
                inFiler.Click += delegate { RevealInFiler(tile.Path); };
                menu.Items.Add(inFiler);
            }

            var reveal = new MenuItem { Header = "エクスプローラーで場所を開く" };
            reveal.Click += delegate { ShellActions.RevealInExplorer(tile.Path, _status); };
            menu.Items.Add(reveal);

            menu.Items.Add(new Separator());

            var rename = new MenuItem { Header = "表示名を変更…", ToolTip = "マスに出す名前だけを変える（ファイルの名前は変えない。ファイルの名前はファイラーで変える）" };
            rename.Click += delegate { BeginRename(cell); };
            menu.Items.Add(rename);

            // 項目を押すとメニューが先に閉じて枠も消えるので、アイコンを選ぶダイアログの間はもう一度付けておく。
            var icon = new MenuItem { Header = "アイコンを変更…" };
            icon.Click += delegate
            {
                _menuCell = cell;
                Paint(cell);
                try
                {
                    ChangeIcon(cell);
                }
                finally
                {
                    _menuCell = null;
                    Paint(cell);
                }
            };
            menu.Items.Add(icon);

            if (!string.IsNullOrEmpty(tile.IconPath))
            {
                var reset = new MenuItem { Header = "アイコンを元に戻す" };
                reset.Click += delegate
                {
                    var current = _settings.TileAt(cell.Column, cell.Row);
                    if (current == null) return;

                    current.IconPath = null;
                    current.IconIndex = 0;
                    _settings.Save();
                    Fill(cell);
                };
                menu.Items.Add(reset);
            }

            menu.Items.Add(new Separator());

            // ファイルは消さない。ランチャーの登録を外すだけ（ファイラーの「削除…」とは違う）。
            var remove = new MenuItem { Header = "ランチャーから外す" };
            remove.Click += delegate
            {
                _settings.SetTile(cell.Column, cell.Row, null, null);
                _settings.Save();
                Rebuild();   // 端のマスを外したら縮む
                _status("ランチャーから外しました: " + tile.DisplayName);
            };
            menu.Items.Add(remove);

            // メニューを出している間は、マスを青い枠で示しておく。
            _menuCell = cell;
            Paint(cell);
            menu.Closed += delegate
            {
                _menuCell = null;
                Paint(cell);
            };

            menu.PlacementTarget = border;
            menu.IsOpen = true;
        }

        // =====================================================================
        // ダイアログから追加する
        // =====================================================================

        /// <summary>空いているマスの右クリック。ダイアログで選んだファイル・フォルダを、そのマスに登録する。</summary>
        private void OnEmptyRightClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled || _dragFrom != null) return;

            var cell = CellAt(e.GetPosition(_canvas));
            if (!cell.HasValue || _settings.TileAt(cell.Value.Column, cell.Value.Row) != null) return;

            e.Handled = true;
            var at = cell.Value;

            var menu = new ContextMenu();

            // 項目を押すとメニューが先に閉じて枠も消えるので、選ぶダイアログの間はもう一度出しておく。
            var file = new MenuItem { Header = "ファイルを追加…" };
            file.Click += delegate { WithTarget(at, () => AddFilesFromDialog(at)); };
            menu.Items.Add(file);

            var folder = new MenuItem { Header = "フォルダを追加…" };
            folder.Click += delegate { WithTarget(at, () => AddFolderFromDialog(at)); };
            menu.Items.Add(folder);

            // どのマスに入るか分かるよう、メニューを出している間は青い枠を出す。
            ShowDropTarget(at);
            menu.Closed += delegate { ShowDropTarget(null); };

            menu.PlacementTarget = _canvas;
            menu.IsOpen = true;
        }

        /// <summary>入る先のマスに青い枠を出したまま action を行い、終わったら消す。</summary>
        private void WithTarget(Cell at, Action action)
        {
            ShowDropTarget(at);
            try
            {
                action();
            }
            finally
            {
                ShowDropTarget(null);
            }
        }

        /// <summary>
        /// ファイルを選んで登録する。複数選べる。
        /// ショートカット（.lnk）はリンク先に置き換えず、そのまま登録する。既定のままだとリンク先になってしまい、
        /// スタートメニューにある Mac アプリの共有ショートカット（Parallels）が登録できないため。
        /// </summary>
        private void AddFilesFromDialog(Cell at)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "ランチャーに追加するファイル";
            dialog.Filter = "すべてのファイル (*.*)|*.*";
            dialog.Multiselect = true;
            dialog.DereferenceLinks = false;

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            var paths = dialog.FileNames.Where(Exists).ToArray();
            if (paths.Length > 0) AddAt(at, paths);
        }

        private void AddFolderFromDialog(Cell at)
        {
            var folder = PickFolder(Window.GetWindow(this), "ランチャーに追加するフォルダ");
            if (folder != null) AddAt(at, new[] { folder });
        }

        // .NET Framework の FolderBrowserDialog は古いツリー型なので、エクスプローラーと同じ形の
        // Windows のファイル選択画面（IFileOpenDialog）を、フォルダを選ぶ設定で呼び出す。

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        private const uint FosPickFolders = 0x20;
        private const uint FosForceFileSystem = 0x40;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        /// <summary>フォルダを選ぶ。やめたら null。選べなかったら知らせて null。</summary>
        private string PickFolder(Window owner, string title)
        {
            IFileDialog dialog = null;

            try
            {
                dialog = (IFileDialog)new FileOpenDialogCom();

                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem);
                dialog.SetTitle(title);

                var handle = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                var result = dialog.Show(handle);
                if (result == ErrorCancelled) return null;
                if (result != 0) Marshal.ThrowExceptionForHR(result);

                IShellItem item;
                dialog.GetResult(out item);

                string path;
                item.GetDisplayName(SigdnFileSysPath, out path);
                Marshal.ReleaseComObject(item);

                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception ex)
            {
                _status("フォルダを選べませんでした: " + ex.Message);
                return null;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        /// <summary>右のファイラーで、その項目が入っているフォルダを開く。</summary>
        private void RevealInFiler(string path)
        {
            string folder = null;
            try
            {
                folder = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                _status("場所が見つかりません: " + path);
                return;
            }

            _showInFiler(folder);
        }

        // 名前を変えている途中なら、やめる処理。マス目を作り直すときに呼ぶ。
        private Action _cancelRename;

        /// <summary>
        /// マスの名前の欄をその場で書き換える。Enter か外を押すと決める、Esc でやめる（入力欄そのものの振る舞い）。
        /// 空にするとファイル名に戻る。
        /// </summary>
        private void BeginRename(Cell cell)
        {
            var tile = _settings.TileAt(cell.Column, cell.Row);
            if (tile == null) return;

            Border border;
            if (!_cells.TryGetValue(cell, out border)) return;

            var stack = border.Child as StackPanel;
            if (stack == null || stack.Children.Count < 2) return;

            var box = new TextBox();
            box.Text = tile.DisplayName;
            box.FontSize = 12;
            box.Margin = new Thickness(6, 0, 6, 0);
            box.Background = Resource("SidebarBackground");
            box.Foreground = Resource("TextPrimary");
            box.CaretBrush = Resource("TextPrimary");
            box.BorderBrush = Resource("AccentBrush");

            var done = false;
            Action<bool> finish = null;
            finish = commit =>
            {
                if (done) return;
                done = true;
                _cancelRename = null;

                if (commit)
                {
                    // 前後の空白と改行は落とす。中の改行は残す（そこで折り返して出す）。
                    var name = box.Text.Replace("\r\n", "\n").Trim();
                    var current = _settings.TileAt(cell.Column, cell.Row);
                    if (current != null && current.Path == tile.Path)
                    {
                        var fallback = new LauncherTile { Path = tile.Path }.DisplayName;
                        current.Name = name.Length == 0 || name == fallback ? null : name;
                        _settings.Save();
                    }
                }

                Fill(cell);
            };

            // Enter で決める。Shift+Enter は改行（名前の中で意図して折り返すため）。Esc でやめる。
            // 入力欄そのものの振る舞いなので、アプリ全体のショートカットとは別（SPEC 11章）。
            box.TextWrapping = TextWrapping.Wrap;
            box.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    e.Handled = true;
                    var start = box.SelectionStart;
                    box.SelectedText = "\n";   // 選んでいればその範囲を改行に置き換える
                    box.Select(start + 1, 0);
                }
                else if (e.Key == Key.Enter) { e.Handled = true; finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; finish(false); }
            };
            // 外を押したら決める（エクスプローラーの名前の変更と同じ）。やめるのは Esc だけ。
            // 外でやめにすると、改行を入れてから外を押したとき、日本語入力の Enter（変換の確定）のあと外を押したときに、
            // 打った名前が黙って捨てられる。
            box.LostKeyboardFocus += delegate { finish(true); };

            stack.Children.RemoveAt(1);
            stack.Children.Add(box);
            _cancelRename = () => finish(false);

            box.Focus();
            box.SelectAll();
        }

        // =====================================================================
        // アイコン —— エクスプローラーと同じものを Windows に尋ねる
        // =====================================================================

        [DllImport("shell32.dll", EntryPoint = "#62", CharSet = CharSet.Unicode)]
        private static extern int PickIconDlg(IntPtr owner, System.Text.StringBuilder path, uint size, ref int index);

        /// <summary>マスのアイコン。自分で選んだアイコンがあればそれ、取れなければ登録したものから決まるアイコン。</summary>
        private static ImageSource IconOf(LauncherTile tile)
        {
            if (!string.IsNullOrEmpty(tile.IconPath))
            {
                var chosen = ShellIcons.FromResource(tile.IconPath, tile.IconIndex);
                if (chosen != null) return chosen;
            }

            return ShellIcons.ForFile(tile.Path);
        }

        /// <summary>
        /// Windows の「アイコンの変更」ダイアログで選ぶ（ショートカットのプロパティと同じ画面）。
        /// .dll・.exe・.ico を選び、その中のアイコンを選べる。最初は、今のアイコンか Windows の標準のアイコン集（imageres.dll）。
        /// </summary>
        private void ChangeIcon(Cell cell)
        {
            var tile = _settings.TileAt(cell.Column, cell.Row);
            if (tile == null) return;

            var path = new System.Text.StringBuilder(
                string.IsNullOrEmpty(tile.IconPath) ? @"%SystemRoot%\System32\imageres.dll" : tile.IconPath, 1024);
            var index = string.IsNullOrEmpty(tile.IconPath) ? 0 : tile.IconIndex;

            var window = Window.GetWindow(this);
            var owner = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

            try
            {
                if (PickIconDlg(owner, path, (uint)path.Capacity, ref index) == 0) return;   // やめた
            }
            catch (Exception ex)
            {
                _status("アイコンを選ぶ画面を開けませんでした: " + ex.Message);
                return;
            }

            var chosen = path.ToString();
            if (ShellIcons.FromResource(chosen, index) == null)
            {
                _status("そのアイコンを読めませんでした: " + chosen);
                return;
            }

            // 選んだ間に外れたり入れ替わったりしていないか、確かめてから書く。
            var current = _settings.TileAt(cell.Column, cell.Row);
            if (current == null || current.Path != tile.Path) return;

            current.IconPath = chosen;
            current.IconIndex = index;
            _settings.Save();
            Fill(cell);
        }

        private static Brush Resource(string key)
        {
            return (Brush)Application.Current.FindResource(key);
        }
    }
}
