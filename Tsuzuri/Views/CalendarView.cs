using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// カレンダー（SPEC 22章。2026-11-20）。予定を月か2週で並べ、ここで直接打って直す。保存先の .md（ScheduleFile）は裏に置くだけ。
    ///
    /// ・時間の枠は持たない。予定は日に付き、時間は名前に書く（利用者の書き方）。何日かにまたがる予定は帯で出す
    /// ・予定とタスクは分けない。済んだら消す。繰り返しは持たない（Google カレンダーに残す）
    /// ・日を2度押す・何日かをなぞると、その日（その間）の予定を足す。予定を2度押すか Enter で直す。予定をつかんで別の日へ動かす。Delete で消す。Ctrl+Z で戻す
    /// ・直すたびにすぐ保存する（保存の操作は無い）。外で書き換わっていたら、直す前に読み直す
    /// 本文の場所に重ねて出す（MainWindow.Calendar.cs）。ランチャーと同じく、出すたびに作り、閉じたら捨てる。
    /// </summary>
    public sealed class CalendarView : Grid
    {
        private const double TopBarHeight = 40;
        private const double DayHeaderHeight = 24;
        private const double DayNumberHeight = 22;
        private const double LaneHeight = 22;
        private const int UndoLimit = 50;

        private static readonly Brush SundayText = Freeze(Color.FromRgb(0xE0, 0x7A, 0x7A));
        private static readonly Brush SaturdayText = Freeze(Color.FromRgb(0x72, 0xA4, 0xE0));
        private static readonly Brush SingleFill = Freeze(Color.FromRgb(0x24, 0x31, 0x4C));
        private static readonly Brush MultiFill = Freeze(Color.FromRgb(0x2C, 0x4A, 0x86));

        private readonly string _path;
        private readonly AppSettings _settings;
        private readonly Action<string> _status;

        private List<ScheduleItem> _items = new List<ScheduleItem>();
        private DateTime _loadedStamp;
        private readonly List<List<ScheduleItem>> _undo = new List<List<ScheduleItem>>();

        /// <summary>出している期間の基準。月なら 1 日、2週なら最初の週の日曜。</summary>
        private DateTime _anchor;

        private readonly TextBlock _title = new TextBlock();
        private readonly Button _monthButton;
        private readonly Button _twoWeeksButton;
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly StackPanel _weeks = new StackPanel();

        // 組んだ週ごとの、始まりの日と行（押した場所の日を求める）。日ごとのマス（なぞったところを塗る）
        private readonly List<KeyValuePair<DateTime, Grid>> _rows = new List<KeyValuePair<DateTime, Grid>>();
        private readonly Dictionary<DateTime, Border> _cells = new Dictionary<DateTime, Border>();

        private ScheduleItem _selected;

        // なぞって日を選んでいる（予定を足す）
        private DateTime? _selectFrom;
        private DateTime? _selectTo;

        // 予定をつかんで動かしている
        private ScheduleItem _dragItem;
        private DateTime _dragFrom;
        private int _dragDelta;
        private bool _dragMoved;

        // 予定を足す・直す欄
        private readonly Grid _editorLayer = new Grid();
        private readonly TextBox _editTitle;
        private readonly TextBox _editStart;
        private readonly TextBox _editEnd;
        private readonly TextBox _editMemo;
        private readonly TextBlock _editError = new TextBlock();
        private readonly Button _editDelete;
        private ScheduleItem _editing;   // 直している予定。足すときは null
        private bool _editorOpen;

        public CalendarView(string path, AppSettings settings, Action<string> status)
        {
            _path = path;
            _settings = settings;
            _status = status;

            Focusable = true;
            FocusVisualStyle = null;
            Background = Resource("SidebarBackground");

            var today = DateTime.Today;
            _anchor = _settings.CalendarTwoWeeks ? WeekStart(today) : new DateTime(today.Year, today.Month, 1);

            // 上の段: ◀ 今日 ▶ 題 …… 月 2週
            var bar = new DockPanel { Height = TopBarHeight, LastChildFill = false, Margin = new Thickness(12, 0, 12, 0) };
            var prev = ToolButton("◀", "前へ（PageUp）");
            prev.Click += delegate { Move(-1); };
            var todayButton = ToolButton("今日", "今日を含む期間へ（Home）");
            todayButton.Click += delegate { GoToday(); };
            var next = ToolButton("▶", "次へ（PageDown）");
            next.Click += delegate { Move(1); };
            _title.FontSize = 16;
            _title.Margin = new Thickness(12, 0, 0, 0);
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Foreground = Resource("TextPrimary");
            foreach (var element in new UIElement[] { prev, todayButton, next, _title })
            {
                DockPanel.SetDock(element, Dock.Left);
                bar.Children.Add(element);
            }

            _twoWeeksButton = ToolButton("2週", "今週と来週を出す");
            _twoWeeksButton.Click += delegate { SetTwoWeeks(true); };
            _monthButton = ToolButton("月", "1か月を出す");
            _monthButton.Click += delegate { SetTwoWeeks(false); };
            DockPanel.SetDock(_twoWeeksButton, Dock.Right);
            DockPanel.SetDock(_monthButton, Dock.Right);
            bar.Children.Add(_twoWeeksButton);
            bar.Children.Add(_monthButton);

            var barBorder = new Border
            {
                Child = bar,
                BorderBrush = Resource("BorderBrushSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            DockPanel.SetDock(barBorder, Dock.Top);

            // 曜日の段（日曜から。Google カレンダーと同じ）
            var header = new Grid { Height = DayHeaderHeight };
            for (var d = 0; d < 7; d++)
            {
                header.ColumnDefinitions.Add(new ColumnDefinition());
                var name = new TextBlock
                {
                    Text = CultureInfo.GetCultureInfo("ja-JP").DateTimeFormat.GetShortestDayName((DayOfWeek)d),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = d == 0 ? SundayText : d == 6 ? SaturdayText : Resource("TextMuted"),
                };
                Grid.SetColumn(name, d);
                header.Children.Add(name);
            }
            DockPanel.SetDock(header, Dock.Top);

            _weeks.Background = Brushes.Transparent;   // 空いているところも押せるように
            _weeks.MouseLeftButtonDown += OnWeeksDown;
            _weeks.MouseMove += OnWeeksMove;
            _weeks.MouseLeftButtonUp += OnWeeksUp;
            _weeks.LostMouseCapture += delegate { EndGestures(false); };

            _scroll.Content = _weeks;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.SizeChanged += delegate { FitRows(); };

            var main = new DockPanel { LastChildFill = true };
            main.Children.Add(barBorder);
            main.Children.Add(header);
            main.Children.Add(_scroll);
            Children.Add(main);

            // 予定を足す・直す欄（真ん中に重ねる）
            _editTitle = Field(false);
            _editStart = Field(false);
            _editEnd = Field(false);
            _editMemo = Field(true);
            _editDelete = ToolButton("削除", "この予定を消す（Ctrl+Z で戻せる）");
            Children.Add(BuildEditor());

            KeyDown += OnKey;

            Load();
            Rebuild();
        }

        // ---------------------------------------------------------------
        // 読み書き
        // ---------------------------------------------------------------

        private void Load()
        {
            try
            {
                _items = File.Exists(_path) ? ScheduleFile.Parse(File.ReadAllText(_path, Encoding.UTF8)) : new List<ScheduleItem>();
                _loadedStamp = Stamp();
            }
            catch (Exception ex)
            {
                _items = new List<ScheduleItem>();
                _loadedStamp = Stamp();
                _status("予定を読めませんでした: " + ex.Message);
            }
            if (_selected != null && !_items.Contains(_selected)) _selected = null;
        }

        private DateTime Stamp()
        {
            try
            {
                return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// 直す前に呼ぶ。外（Claude・ほかの画面のカレンダー）で書き換わっていたら読み直して false（今の操作はやめる。読み直した中身で、もう一度してもらう）。
        /// </summary>
        private bool Fresh()
        {
            if (Stamp() == _loadedStamp) return true;

            Load();
            _undo.Clear();
            Rebuild();
            _status("予定が外で書き換えられていたので、読み直しました。もう一度操作してください");
            return false;
        }

        /// <summary>直す前の中身を覚える（Ctrl+Z）。</summary>
        private void Remember()
        {
            _undo.Add(_items.Select(item => item.Clone()).ToList());
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        }

        private void Save()
        {
            var temp = _path + ".saving";
            try
            {
                var folder = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(temp, ScheduleFile.Serialize(_items), new UTF8Encoding(false));
                if (File.Exists(_path)) PageDocument.ReplaceWith(temp, _path);
                else File.Move(temp, _path);
                _loadedStamp = Stamp();
            }
            catch (Exception ex)
            {
                _status("予定を保存できませんでした: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 期間
        // ---------------------------------------------------------------

        private static DateTime WeekStart(DateTime date)
        {
            return date.Date.AddDays(-(int)date.DayOfWeek);
        }

        private void Move(int step)
        {
            _anchor = _settings.CalendarTwoWeeks ? _anchor.AddDays(7 * step) : _anchor.AddMonths(step);
            Rebuild();
        }

        private void GoToday()
        {
            var today = DateTime.Today;
            _anchor = _settings.CalendarTwoWeeks ? WeekStart(today) : new DateTime(today.Year, today.Month, 1);
            Rebuild();
        }

        private void SetTwoWeeks(bool twoWeeks)
        {
            if (_settings.CalendarTwoWeeks == twoWeeks) return;

            // 今見ている所を引き継ぐ（月→2週は、今日がその月なら今週、違えばその月の最初の週。2週→月は、最初の週の月）
            var today = DateTime.Today;
            if (twoWeeks)
                _anchor = today.Year == _anchor.Year && today.Month == _anchor.Month ? WeekStart(today) : WeekStart(_anchor);
            else
            {
                var middle = _anchor.AddDays(3);
                _anchor = new DateTime(middle.Year, middle.Month, 1);
            }

            _settings.CalendarTwoWeeks = twoWeeks;
            _settings.Save();
            Rebuild();
        }

        // ---------------------------------------------------------------
        // 組む
        // ---------------------------------------------------------------

        private void Rebuild()
        {
            var twoWeeks = _settings.CalendarTwoWeeks;
            DateTime first;
            int weekCount;
            if (twoWeeks)
            {
                first = _anchor;
                weekCount = 2;
                var last = first.AddDays(13);
                _title.Text = first.ToString("yyyy年M月d日", CultureInfo.InvariantCulture) + " 〜 " +
                              (last.Year == first.Year ? last.ToString("M月d日", CultureInfo.InvariantCulture) : last.ToString("yyyy年M月d日", CultureInfo.InvariantCulture));
            }
            else
            {
                first = WeekStart(_anchor);
                var days = (int)_anchor.DayOfWeek + DateTime.DaysInMonth(_anchor.Year, _anchor.Month);
                weekCount = (days + 6) / 7;
                _title.Text = _anchor.ToString("yyyy年M月", CultureInfo.InvariantCulture);
            }

            Paint(_monthButton, !twoWeeks);
            Paint(_twoWeeksButton, twoWeeks);

            _weeks.Children.Clear();
            _rows.Clear();
            _cells.Clear();
            for (var w = 0; w < weekCount; w++)
            {
                var weekStart = first.AddDays(7 * w);
                var row = BuildWeek(weekStart, twoWeeks);
                _rows.Add(new KeyValuePair<DateTime, Grid>(weekStart, row));
                _weeks.Children.Add(row);
            }

            FitRows();
            PaintSelection();
        }

        /// <summary>週の行を画面の高さで等分する（予定が多い週は伸び、全体がスクロールする）。</summary>
        private void FitRows()
        {
            if (_rows.Count == 0) return;
            var height = Math.Max(0, _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight);
            var each = Math.Floor(height / _rows.Count);
            foreach (var row in _rows) row.Value.MinHeight = Math.Max(DayNumberHeight + LaneHeight * 2, each);
        }

        private Grid BuildWeek(DateTime weekStart, bool twoWeeks)
        {
            var weekEnd = weekStart.AddDays(6);
            var today = DateTime.Today;

            // 予定を段に割り振る（始まりの早い順、同じなら長い順。空いている一番上の段へ）
            var shown = _items.Select(item => Shifted(item))
                              .Where(pair => pair.Value.Start <= weekEnd && pair.Value.End >= weekStart)
                              .OrderBy(pair => pair.Value.Start).ThenByDescending(pair => pair.Value.Days)
                              .ToList();
            var laneEnds = new List<int>();
            var placed = new List<Tuple<ScheduleItem, ScheduleItem, int, int, int>>();   // 元・出す形・段・始まりの列・終わりの列
            foreach (var pair in shown)
            {
                var from = Math.Max(0, (int)(pair.Value.Start - weekStart).TotalDays);
                var to = Math.Min(6, (int)(pair.Value.End - weekStart).TotalDays);
                var lane = laneEnds.FindIndex(end => end < from);
                if (lane < 0)
                {
                    lane = laneEnds.Count;
                    laneEnds.Add(to);
                }
                else laneEnds[lane] = to;
                placed.Add(Tuple.Create(pair.Key, pair.Value, lane, from, to));
            }

            var row = new Grid();
            for (var d = 0; d < 7; d++) row.ColumnDefinitions.Add(new ColumnDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayNumberHeight) });
            for (var i = 0; i < laneEnds.Count; i++) row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LaneHeight) });
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 6 });
            var rowCount = row.RowDefinitions.Count;

            for (var d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                var outside = !twoWeeks && date.Month != _anchor.Month;

                var cell = new Border
                {
                    BorderBrush = Resource("BorderBrushSoft"),
                    BorderThickness = new Thickness(d == 0 ? 0 : 1, 0, 0, 1),
                    Background = date == today ? Resource("AccentFill") : Brushes.Transparent,
                };
                Grid.SetColumn(cell, d);
                Grid.SetRowSpan(cell, rowCount);
                row.Children.Add(cell);
                _cells[date] = cell;

                var number = new TextBlock
                {
                    Text = date.Day == 1 || (d == 0 && twoWeeks && date == _anchor) ? date.ToString("M/d", CultureInfo.InvariantCulture) : date.Day.ToString(CultureInfo.InvariantCulture),
                    Margin = new Thickness(6, 3, 6, 0),
                    FontWeight = date == today ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = date == today ? Resource("AccentText")
                        : date.DayOfWeek == DayOfWeek.Sunday ? SundayText
                        : date.DayOfWeek == DayOfWeek.Saturday ? SaturdayText
                        : Resource("TextPrimary"),
                    Opacity = outside ? 0.45 : 1,
                    IsHitTestVisible = false,
                };
                Grid.SetColumn(number, d);
                row.Children.Add(number);
            }

            foreach (var entry in placed)
            {
                var bar = BuildBar(entry.Item1, entry.Item2, weekStart, weekEnd);
                Grid.SetRow(bar, entry.Item3 + 1);
                Grid.SetColumn(bar, entry.Item4);
                Grid.SetColumnSpan(bar, entry.Item5 - entry.Item4 + 1);
                row.Children.Add(bar);
            }

            return row;
        }

        /// <summary>つかんで動かしている予定は、動かした先の形で出す。ほかはそのまま。</summary>
        private KeyValuePair<ScheduleItem, ScheduleItem> Shifted(ScheduleItem item)
        {
            if (!ReferenceEquals(item, _dragItem) || _dragDelta == 0) return new KeyValuePair<ScheduleItem, ScheduleItem>(item, item);

            var moved = item.Clone();
            moved.Start = item.Start.AddDays(_dragDelta);
            moved.End = item.End.AddDays(_dragDelta);
            return new KeyValuePair<ScheduleItem, ScheduleItem>(item, moved);
        }

        private Border BuildBar(ScheduleItem item, ScheduleItem shown, DateTime weekStart, DateTime weekEnd)
        {
            var startsHere = shown.Start >= weekStart;
            var endsHere = shown.End <= weekEnd;
            var selected = ReferenceEquals(item, _selected);

            var text = new TextBlock
            {
                Text = (item.Title.Length > 0 ? item.Title : "（名前なし）") + (item.Memo.Trim().Length > 0 ? "  ≡" : string.Empty),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Resource("TextPrimary"),
                Opacity = item.Title.Length > 0 ? 1 : 0.6,
            };

            var bar = new Border
            {
                Child = text,
                Tag = item,
                Margin = new Thickness(startsHere ? 3 : 0, 1, endsHere ? 3 : 0, 1),
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(startsHere ? 3 : 0, endsHere ? 3 : 0, endsHere ? 3 : 0, startsHere ? 3 : 0),
                Background = item.Days > 1 ? MultiFill : SingleFill,
                BorderBrush = selected ? Resource("AccentBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Opacity = ReferenceEquals(item, _dragItem) && _dragMoved ? 0.7 : 1,
                ToolTip = Tip(item),
            };
            bar.MouseLeftButtonDown += (sender, e) => OnBarDown(item, e);
            return bar;
        }

        private static string Tip(ScheduleItem item)
        {
            var dates = item.Start.ToString("M/d (ddd)", CultureInfo.GetCultureInfo("ja-JP"));
            if (item.End > item.Start) dates += " 〜 " + item.End.ToString("M/d (ddd)", CultureInfo.GetCultureInfo("ja-JP"));
            var tip = dates + "  " + item.Title;
            var memo = item.Memo.Trim();
            if (memo.Length > 0) tip += "\n\n" + (memo.Length > 600 ? memo.Substring(0, 600) + "…" : memo);
            return tip;
        }

        /// <summary>なぞっている日を塗る（組み直さずに、マスの地だけ変える）。</summary>
        private void PaintSelection()
        {
            var today = DateTime.Today;
            DateTime low = DateTime.MaxValue, high = DateTime.MinValue;
            if (_selectFrom.HasValue && _selectTo.HasValue)
            {
                low = _selectFrom.Value < _selectTo.Value ? _selectFrom.Value : _selectTo.Value;
                high = _selectFrom.Value < _selectTo.Value ? _selectTo.Value : _selectFrom.Value;
            }

            foreach (var pair in _cells)
            {
                pair.Value.Background = pair.Key >= low && pair.Key <= high ? Resource("SelectionFill")
                    : pair.Key == today ? Resource("AccentFill")
                    : Brushes.Transparent;
            }
        }

        // ---------------------------------------------------------------
        // マウス
        // ---------------------------------------------------------------

        /// <summary>押した場所の日（_weeks の中の位置）。週の行の外なら null。</summary>
        private DateTime? DateAt(Point point)
        {
            foreach (var pair in _rows)
            {
                var row = pair.Value;
                var top = row.TranslatePoint(new Point(0, 0), _weeks).Y;
                if (point.Y < top || point.Y >= top + row.ActualHeight) continue;
                if (row.ActualWidth <= 0) return null;

                var column = (int)Math.Floor(point.X / (row.ActualWidth / 7));
                column = Math.Max(0, Math.Min(6, column));
                return pair.Key.AddDays(column);
            }

            // 上下の外へ出たら、端の週に寄せる（つかんで動かすとき、端の週の外でも止まらないように）
            if (_rows.Count == 0) return null;
            var edge = point.Y < 0 ? _rows[0] : _rows[_rows.Count - 1];
            var width = edge.Value.ActualWidth;
            if (width <= 0) return null;
            return edge.Key.AddDays(Math.Max(0, Math.Min(6, (int)Math.Floor(point.X / (width / 7)))));
        }

        private void OnBarDown(ScheduleItem item, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Focus();
            if (_editorOpen) return;

            if (e.ClickCount == 2)
            {
                OpenEditor(item, item.Start, item.End);
                return;
            }

            SetSelected(item);
            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;

            _dragItem = item;
            _dragFrom = date.Value;
            _dragDelta = 0;
            _dragMoved = false;
            _weeks.CaptureMouse();
        }

        private void OnWeeksDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            if (_editorOpen) return;

            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;
            e.Handled = true;

            if (e.ClickCount == 2)
            {
                EndGestures(false);
                OpenEditor(null, date.Value, date.Value);
                return;
            }

            SetSelected(null);
            _selectFrom = _selectTo = date;
            PaintSelection();
            _weeks.CaptureMouse();
        }

        private void OnWeeksMove(object sender, MouseEventArgs e)
        {
            if (!_weeks.IsMouseCaptured) return;
            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;

            if (_dragItem != null)
            {
                var delta = (int)(date.Value - _dragFrom).TotalDays;
                if (delta == _dragDelta) return;
                _dragDelta = delta;
                _dragMoved = true;
                Rebuild();
                return;
            }

            if (_selectFrom != null && _selectTo != date)
            {
                _selectTo = date;
                PaintSelection();
            }
        }

        private void OnWeeksUp(object sender, MouseButtonEventArgs e)
        {
            if (!_weeks.IsMouseCaptured) return;
            EndGestures(true);
        }

        /// <summary>つかむ・なぞるを終える。commit なら、動かした予定を保存し、なぞった何日かで予定を足す。</summary>
        private void EndGestures(bool commit)
        {
            var dragItem = _dragItem;
            var delta = _dragDelta;
            var from = _selectFrom;
            var to = _selectTo;

            _dragItem = null;
            _dragDelta = 0;
            _dragMoved = false;
            _selectFrom = _selectTo = null;
            if (_weeks.IsMouseCaptured) _weeks.ReleaseMouseCapture();

            if (dragItem != null)
            {
                if (commit && delta != 0 && Fresh())
                {
                    var target = _items.FirstOrDefault(item => ReferenceEquals(item, dragItem));
                    if (target != null)
                    {
                        Remember();
                        target.Start = target.Start.AddDays(delta);
                        target.End = target.End.AddDays(delta);
                        Save();
                    }
                }
                Rebuild();
                return;
            }

            PaintSelection();
            if (commit && from.HasValue && to.HasValue && from.Value != to.Value)
            {
                var low = from.Value < to.Value ? from.Value : to.Value;
                var high = from.Value < to.Value ? to.Value : from.Value;
                OpenEditor(null, low, high);
            }
        }

        private void SetSelected(ScheduleItem item)
        {
            if (ReferenceEquals(_selected, item)) return;
            _selected = item;
            Rebuild();
        }

        // ---------------------------------------------------------------
        // キー
        // ---------------------------------------------------------------

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_editorOpen)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseEditor();
                }
                return;
            }

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.Delete:
                    if (_selected == null) return;
                    e.Handled = true;
                    Delete(_selected);
                    break;
                case Key.Enter:
                    if (_selected == null) return;
                    e.Handled = true;
                    OpenEditor(_selected, _selected.Start, _selected.End);
                    break;
                case Key.Z:
                    if (!ctrl) return;
                    e.Handled = true;
                    Undo();
                    break;
                case Key.PageUp:
                    e.Handled = true;
                    Move(-1);
                    break;
                case Key.PageDown:
                    e.Handled = true;
                    Move(1);
                    break;
                case Key.Home:
                    e.Handled = true;
                    GoToday();
                    break;
                case Key.Escape:
                    // 選んでいる予定があれば、選ぶのをやめるだけ。無ければ外（カレンダーを閉じる）へ回す
                    if (_selected == null) return;
                    e.Handled = true;
                    SetSelected(null);
                    break;
            }
        }

        private void Delete(ScheduleItem item)
        {
            if (!Fresh()) return;
            var target = _items.FirstOrDefault(other => ReferenceEquals(other, item));
            if (target == null) return;

            Remember();
            _items.Remove(target);
            Save();
            if (ReferenceEquals(_selected, target)) _selected = null;
            Rebuild();
            _status("予定を消しました（Ctrl+Z で戻せます）: " + target.Title);
        }

        private void Undo()
        {
            if (_undo.Count == 0)
            {
                _status("戻せる操作がありません");
                return;
            }
            if (!Fresh()) return;

            _items = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _selected = null;
            Save();
            Rebuild();
            _status("戻しました");
        }

        // ---------------------------------------------------------------
        // 予定を足す・直す欄
        // ---------------------------------------------------------------

        private UIElement BuildEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
            _editorLayer.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));

            var panel = new StackPanel();
            panel.Children.Add(Label("名前（時間もここに書く）"));
            panel.Children.Add(_editTitle);

            var dates = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            dates.ColumnDefinitions.Add(new ColumnDefinition());
            dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            dates.ColumnDefinitions.Add(new ColumnDefinition());
            var startPanel = new StackPanel();
            startPanel.Children.Add(Label("始まり"));
            startPanel.Children.Add(_editStart);
            var endPanel = new StackPanel();
            endPanel.Children.Add(Label("終わり（1日なら同じ日）"));
            endPanel.Children.Add(_editEnd);
            Grid.SetColumn(endPanel, 2);
            dates.Children.Add(startPanel);
            dates.Children.Add(endPanel);
            panel.Children.Add(dates);

            var memoLabel = Label("メモ（自由に。Ctrl+Enter で保存）");
            memoLabel.Margin = new Thickness(0, 10, 0, 4);
            panel.Children.Add(memoLabel);
            panel.Children.Add(_editMemo);

            _editError.Foreground = SundayText;
            _editError.Margin = new Thickness(0, 8, 0, 0);
            _editError.TextWrapping = TextWrapping.Wrap;
            _editError.Visibility = Visibility.Collapsed;
            panel.Children.Add(_editError);

            var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
            _editDelete.Click += delegate
            {
                var item = _editing;
                CloseEditor();
                if (item != null) Delete(item);
            };
            DockPanel.SetDock(_editDelete, Dock.Left);
            buttons.Children.Add(_editDelete);

            var save = new Button { Content = "保存", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 72 };
            save.Click += delegate { CommitEdit(); };
            var cancel = ToolButton("やめる", "直さずに閉じる（Esc）");
            cancel.Click += delegate { CloseEditor(); };
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            // 名前・日付の Enter で保存。メモは Ctrl+Enter
            foreach (var box in new[] { _editTitle, _editStart, _editEnd })
                box.KeyDown += (sender, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    e.Handled = true;
                    CommitEdit();
                };
            _editMemo.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                e.Handled = true;
                CommitEdit();
            };

            var box0 = new Border
            {
                Child = panel,
                Width = 480,
                Padding = new Thickness(18),
                CornerRadius = new CornerRadius(6),
                Background = Resource("ChromeBackground"),
                BorderBrush = Resource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _editorLayer.Children.Add(box0);
            return _editorLayer;
        }

        private void OpenEditor(ScheduleItem item, DateTime start, DateTime end)
        {
            _editing = item;
            _editTitle.Text = item != null ? item.Title : string.Empty;
            _editStart.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _editEnd.Text = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _editMemo.Text = item != null ? item.Memo.Replace("\n", Environment.NewLine) : string.Empty;
            _editError.Visibility = Visibility.Collapsed;
            _editDelete.Visibility = item != null ? Visibility.Visible : Visibility.Collapsed;
            _editorLayer.Visibility = Visibility.Visible;
            _editorOpen = true;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                _editTitle.Focus();
                _editTitle.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CloseEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
            _editorOpen = false;
            _editing = null;
            Focus();
        }

        /// <summary>欄が開いているか（閉じる前に、保存するか見る。MainWindow.Calendar.cs）。</summary>
        public bool IsEditing
        {
            get { return _editorOpen; }
        }

        /// <summary>
        /// 欄の中身を保存して閉じる。読めない日付などで保存できなければ、欄に知らせて false（欄は開いたまま）。
        /// 欄が開いていなければ何もせず true。名前もメモも空なら、足すのはやめる（直していた予定はそのまま）。
        /// </summary>
        public bool CommitEdit()
        {
            if (!_editorOpen) return true;

            var reference = _editing != null ? _editing.Start : DateTime.Today;
            DateTime start, end;
            if (!ScheduleFile.TryReadDate(_editStart.Text, reference, out start)) return EditError("始まりの日付を読めません。2026-11-21 か 11/21 の形で書いてください");
            if (!ScheduleFile.TryReadDate(_editEnd.Text, start, out end)) return EditError("終わりの日付を読めません。2026-11-21 か 11/21 の形で書いてください");
            if (end < start) return EditError("終わりが始まりより前です");

            var title = ScheduleFile.OneLine(_editTitle.Text);
            var memo = (_editMemo.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
            var editing = _editing;

            if (editing == null && title.Length == 0 && memo.Trim().Length == 0)
            {
                CloseEditor();
                return true;
            }

            CloseEditor();
            if (!Fresh()) return true;

            Remember();
            if (editing == null)
            {
                var item = new ScheduleItem { Start = start, End = end, Title = title, Memo = memo };
                _items.Add(item);
                _selected = item;
            }
            else
            {
                var target = _items.FirstOrDefault(other => ReferenceEquals(other, editing));
                if (target == null) return true;
                target.Start = start;
                target.End = end;
                target.Title = title;
                target.Memo = memo;
                _selected = target;
            }

            Save();
            Rebuild();
            return true;
        }

        private bool EditError(string message)
        {
            _editError.Text = message;
            _editError.Visibility = Visibility.Visible;
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
                ToolTip = tip,
                Style = (Style)Application.Current.FindResource("ToolButton"),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
        }

        private static void Paint(Button button, bool on)
        {
            button.Background = on ? Resource("AccentFill") : Brushes.Transparent;
            button.Foreground = Resource(on ? "AccentText" : "TextPrimary");
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Resource("TextMuted"),
                Margin = new Thickness(0, 0, 0, 4),
            };
        }

        private static TextBox Field(bool multiLine)
        {
            var box = new TextBox
            {
                Background = Resource("PageBackground"),
                Foreground = Resource("TextPrimary"),
                CaretBrush = Resource("TextPrimary"),
                BorderBrush = Resource("InputBorder"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 14,
            };
            if (multiLine)
            {
                box.AcceptsReturn = true;
                box.AcceptsTab = false;
                box.TextWrapping = TextWrapping.Wrap;
                box.Height = 200;
                box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            return box;
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
