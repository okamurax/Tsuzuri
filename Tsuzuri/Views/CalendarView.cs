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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// カレンダー（SPEC 22章。2026-11-20）。予定を週の行で下へずっと並べ、ここで直接打って直す。保存先の .md（ScheduleFile）は裏に置くだけ。
    ///
    /// ・時間の枠は持たない。予定は日に付き、時間は名前に書く（利用者の書き方）。何日かにまたがる予定は帯で出す
    /// ・月・2週の切り替えは持たない。週の行が上下にずっと続き、スクロールした先を足していく（利用者の考え）
    /// ・日を2度押すと、その日のマスで名前を打つ（1日の予定）。帯の端を引っぱって延ばすと何日かになる。帯をつかんで別の日へ動かす
    /// ・予定を2度押すと、名前を直す欄（日付は欄では直さない。帯で直す。メモの欄は持たない。長い中身はアウトラインの見出しと結ぶ。2026-11-21）。Delete で消す。Ctrl+Z で戻す
    /// ・アウトラインの見出しを日へドラッグすると、その見出しと結んだ予定になる（2026-11-20）。2度押すと、リンク先と「見出しへ飛ぶ」の欄。
    ///   見出しの名前が変わっても追わない。見つからなければ印を付けるだけ（利用者の判断）
    /// ・週の下の境を引っぱると、その週だけ高さを広げられる（2026-11-21。考える週だけ広げる。2度押すと戻す）。広げた高さは app.json に覚える
    /// ・保存先を見張り、外（Claude・ほかの画面・同期）で書き換わったら、すぐ出し直す。増えた・変わった予定は少しの間光らせる（2026-11-21）
    /// ・直すたびにすぐ保存する（保存の操作は無い）。外で書き換わっていたら、直す前に読み直す
    /// 本文の場所に重ねて出す（MainWindow.Calendar.cs）。ランチャーと同じく、出すたびに作り、閉じたら捨てる。
    /// </summary>
    public sealed partial class CalendarView : Grid
    {
        private const double TopBarHeight = 32;     // 下の線を含む。左のアウトライン・右の AI の欄の上の段（32）とそろえる（2026-11-21）
        private const double DayHeaderHeight = 24;
        private const double DayNumberHeight = 22;
        private const double LaneHeight = 22;
        private const double RowMinHeight = 112;   // 週の高さの元（戻すアイコンで戻る値）
        private const double RowHeightStep = 56;   // 上の段の＋－で一律に変える量
        private const double RowHeightMax = 560;
        private const double EdgeGrip = 7;          // 帯の端のこの幅をつかむと延ばす・縮める
        private const int WeeksBefore = 8;          // 初めに出す、今週より前の週
        private const int WeeksAfter = 26;          // 初めに出す、今週より後の週
        private const int WeeksPerExtend = 12;      // 端へスクロールしたら足す週
        private const double ExtendMargin = 600;    // 端からこれだけ手前まで来たら足す
        private const double FlashSeconds = 2.5;    // 外で増えた・変わった予定を光らせる時間
        private const double NoticeSeconds = 8;     // 上の段の「外で変わりました」を出しておく時間
        private const double WeekGrip = 6;          // 週の下の境のこの高さをつかむと、週の高さを変える

        private static readonly Brush SundayText = Freeze(Color.FromRgb(0xE0, 0x7A, 0x7A));
        private static readonly Brush SaturdayText = Freeze(Color.FromRgb(0x72, 0xA4, 0xE0));
        private static readonly Brush SingleFill = Freeze(Color.FromRgb(0x24, 0x31, 0x4C));
        private static readonly Brush MultiFill = Freeze(Color.FromRgb(0x2C, 0x4A, 0x86));
        private static readonly Brush EvenMonthFill = Freeze(Color.FromRgb(0x1A, 0x22, 0x37));   // 月の境が分かるよう、偶数月の地を少し変える
        private static readonly Brush TodayFill = Freeze(Color.FromRgb(0x1F, 0x30, 0x55));       // 今日のマス（偶数月の地と見分けが付くように濃く）
        private static readonly Brush TodayEdge = Freeze(Color.FromRgb(0x5D, 0x89, 0xEA));       // 今日のマスの枠と、日の数字の地（AccentText と同じ色）
        private static readonly Brush DropFill = Freeze(Color.FromArgb(0x60, 0x5D, 0x89, 0xEA));    // アウトラインから落とす先の日
        private static readonly Color FlashColor = Color.FromRgb(0x5D, 0x89, 0xEA);
        private static readonly Brush GripHover = Freeze(Color.FromArgb(0x90, 0x5D, 0x89, 0xEA));

        private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");


        private readonly string _path;
        private readonly Action<string> _status;
        private readonly Func<string, HashSet<string>> _headingsOf;
        private readonly Action<ScheduleItem> _openLink;

        // 高さを広げた週（週の始まりの日 → 高さ）。持ち主は MainWindow（app.json に覚える）。変えたら _weekHeightsChanged
        private readonly Dictionary<DateTime, double> _weekHeights;
        private readonly Action _weekHeightsChanged;
        // 全部の週の高さ（上の段の＋－で一律に変える。2026-11-21）。広げた週は、これとその週の高さの大きいほう
        private double _rowHeight;
        private readonly Action<double> _rowHeightChanged;
        private readonly Button _resetHeights;
        private readonly Button _shorter;
        private readonly Button _taller;

        // 週の下の境を引っぱっている
        private Grid _sizingRow;
        private DateTime _sizingWeek;
        private double _sizingFromY;
        private double _sizingFromHeight;

        // 組み直しのたびに、結んだ文書の見出しを文書ごとに1回だけ集める（集めるのは MainWindow。閉じている文書はファイルの更新日時で覚えている）。
        // 前は帯ごとに尋ねていて、開いている文書は帯の数だけ全ページを解析し直した（ドラッグで1日動かすたびに組み直すので重かった）
        private readonly Dictionary<string, HashSet<string>> _found = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // 日のマス（アウトラインから落とす先を塗る）と、落とす先の日
        private readonly Dictionary<DateTime, Border> _cells = new Dictionary<DateTime, Border>();
        private DateTime? _dropOn;

        private readonly ScheduleBook _book = new ScheduleBook();   // 予定の一覧と、直す操作（Markdown/ScheduleBook.cs）
        private DateTime _loadedStamp;   // 読んだ・書いたときの更新日時（見張りのきっかけだけに使う。変わったかは中身で決める）
        private string _loadedText;      // 読んだ・書いたときの中身。null なら、まだ読めていない（直さない）
        private DateTime _builtDay;      // 組んだときの今日（日付が変わったら組み直す）

        private readonly TextBlock _title = new TextBlock();

        // 保存先の見張り（2026-11-21）。FileSystemWatcher が届かない場所（共有フォルダなど）のために、時々更新日時も見る
        private FileSystemWatcher _watcher;
        private readonly DispatcherTimer _reloadTimer;
        private readonly DispatcherTimer _pollTimer;

        // 外で増えた・変わった予定（光らせる）と、光らせ始めた時刻
        private HashSet<ScheduleItem> _flash = new HashSet<ScheduleItem>();
        private DateTime _flashStart;

        // 上の段の「外で変わりました」（押すと、変わった予定のある週へ）
        private readonly Button _notice;
        private readonly DispatcherTimer _noticeTimer;
        private DateTime? _noticeDate;
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly StackPanel _weeks = new StackPanel();

        // 出している週（_firstWeek から _weekCount 週）と、その行（押した場所の日を求める）
        private DateTime _firstWeek;
        private int _weekCount;
        private readonly List<KeyValuePair<DateTime, Grid>> _rows = new List<KeyValuePair<DateTime, Grid>>();
        private bool _extending;

        private ScheduleItem _selected;
        private DateTime? _selectedOriginal;   // 繰り返しの予定なら、選んでいる回（決まりどおりの日）

        // 選んでいる日（押した日から、なぞった・Shift で押した日まで）。AI に渡す（2026-11-21。日付の話をするため）
        private DateTime? _dayAnchor;
        private DateTime? _dayEnd;
        private bool _selectingDays;

        // 帯をつかんでいる（動かす・延ばす）
        private ScheduleGesture _gesture;
        private ScheduleItem _dragItem;
        private DateTime? _dragOriginal;   // 繰り返しの予定なら、つかんでいる回（決まりどおりの日）
        private DateTime _dragShownStart;  // つかんだときに出ていた、その回の始まりの日
        private DateTime _dragFrom;
        private int _dragDelta;
        private bool _askingScope;         // 繰り返しの回を動かした範囲を、メニューで尋ねている（つかんだままの扱い）

        // その日のマスで名前を打っている（足す）
        private DateTime? _newOn;
        private TextBox _newBox;

        // 名前と繰り返しを直す欄
        private readonly Grid _editorLayer = new Grid();
        private readonly TextBox _editTitle;
        private readonly TextBlock _editLabel = new TextBlock();
        private Button _editJump;
        private readonly TextBlock _editDates = new TextBlock();
        private readonly Button _editDelete;
        private readonly Button _editDeleteAll;
        private readonly ComboBox _editRepeat = new ComboBox();
        private readonly StackPanel _editIntervalRow = new StackPanel();
        private readonly TextBox _editInterval;
        private readonly TextBlock _editIntervalUnit = new TextBlock();
        private readonly StackPanel _editWeekDays = new StackPanel();
        private readonly Border[] _editDayChips = new Border[7];
        private readonly List<DayOfWeek> _editDays = new List<DayOfWeek>();
        private TextBlock _editUntilLabel;
        private readonly TextBox _editUntil;
        private readonly TextBox _editNotify;
        private ScheduleItem _editing;
        private DateTime? _editingOriginal;   // 繰り返しの予定なら、欄を開いた回（決まりどおりの日）
        private bool _editorOpen;

        /// <param name="headingsOf">文書の見出しの文字（全部のページ）。文書が無い・読めなければ null。</param>
        /// <param name="openLink">その見出しへ飛ぶ（カレンダーは閉じる）。</param>
        /// <param name="weekHeights">高さを広げた週。ここで書き換え、そのたびに weekHeightsChanged を呼ぶ。</param>
        /// <param name="rowHeight">全部の週の高さ（0 なら元の高さ）。上の段の＋－で変えるたびに rowHeightChanged を呼ぶ（元の高さなら 0）。</param>
        public CalendarView(string path, Action<string> status, Func<string, HashSet<string>> headingsOf, Action<ScheduleItem> openLink,
                            Dictionary<DateTime, double> weekHeights, Action weekHeightsChanged, double rowHeight, Action<double> rowHeightChanged)
        {
            _path = path;
            _status = status;
            _headingsOf = headingsOf;
            _openLink = openLink;
            _weekHeights = weekHeights ?? new Dictionary<DateTime, double>();
            _weekHeightsChanged = weekHeightsChanged ?? delegate { };
            _rowHeight = rowHeight > 0 ? Math.Max(RowMinHeight, Math.Min(RowHeightMax, rowHeight)) : RowMinHeight;
            _rowHeightChanged = rowHeightChanged ?? delegate { };

            Focusable = true;
            FocusVisualStyle = null;
            Background = Resource("SidebarBackground");

            // 上の段: 見えている月 …… 今日
            var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(16, 0, 12, 0) };
            _title.FontSize = 14;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Foreground = Resource("TextPrimary");
            DockPanel.SetDock(_title, Dock.Left);
            bar.Children.Add(_title);
            _notice = ToolButton(string.Empty, "変わった予定のある週へ");
            _notice.Margin = new Thickness(16, 0, 0, 0);
            _notice.Foreground = Resource("AccentText");
            _notice.Visibility = Visibility.Collapsed;
            _notice.Click += delegate
            {
                if (_noticeDate.HasValue) ScrollToDate(_noticeDate.Value);
                _notice.Visibility = Visibility.Collapsed;
            };
            DockPanel.SetDock(_notice, Dock.Left);
            bar.Children.Add(_notice);
            var todayButton = ToolButton("今日", "今日の週へ（Home）");
            todayButton.Click += delegate { ScrollToToday(); };
            DockPanel.SetDock(todayButton, Dock.Right);
            bar.Children.Add(todayButton);

            // 全部の週の高さを戻す（2026-11-21）。＋－で変えた高さも、引っぱって広げた週も。どちらも元のままなら押せない
            _resetHeights = IconButton("\uE72C", "全部の週を元の高さに戻す（＋－で変えた高さも、引っぱって広げた週も）");
            _resetHeights.Margin = new Thickness(0, 0, 8, 0);
            _resetHeights.Click += delegate { ResetWeekHeights(); };
            DockPanel.SetDock(_resetHeights, Dock.Right);
            bar.Children.Add(_resetHeights);

            // 全部の週を一律に高く・低くする（2026-11-21。引っぱって1週ずつ広げるのとは別に）
            _taller = IconButton("\uE710", "全部の週を高くする");
            _taller.Margin = new Thickness(0, 0, 2, 0);
            _taller.Click += delegate { SetRowHeight(_rowHeight + RowHeightStep); };
            DockPanel.SetDock(_taller, Dock.Right);
            bar.Children.Add(_taller);
            _shorter = IconButton("\uE738", "全部の週を低くする");
            _shorter.Margin = new Thickness(0, 0, 2, 0);
            _shorter.Click += delegate { SetRowHeight(_rowHeight - RowHeightStep); };
            DockPanel.SetDock(_shorter, Dock.Right);
            bar.Children.Add(_shorter);
            UpdateResetHeights();

            var barBorder = new Border
            {
                Child = bar,
                Height = TopBarHeight,
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
                    Text = Japanese.DateTimeFormat.GetShortestDayName((DayOfWeek)d),
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
            _weeks.LostMouseCapture += delegate { EndGesture(false); };

            _scroll.Content = _weeks;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.ScrollChanged += OnScrollChanged;

            var main = new DockPanel { LastChildFill = true };
            main.Children.Add(barBorder);
            main.Children.Add(header);
            main.Children.Add(_scroll);
            Children.Add(main);

            _editTitle = Field();
            _editInterval = Field();
            _editUntil = Field();
            _editNotify = Field();
            _editDelete = ToolButton("削除", "この予定を消す（Ctrl+Z で戻せる）");
            _editDelete.Margin = new Thickness(0, 0, 6, 0);
            _editDeleteAll = ToolButton("全部消す", "繰り返しの予定を全部の回ごと消す（Ctrl+Z で戻せる）");
            Children.Add(BuildEditor());

            KeyDown += OnKey;

            // 見張り。知らせはまとめて来る（置き換えで消す・作る・名前を変える）ので、少し待ってから1回読む
            _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _reloadTimer.Tick += delegate
            {
                _reloadTimer.Stop();
                if (!ReloadIfChanged()) _reloadTimer.Start();   // 打っている・つかんでいる最中なら、終わるまで待って読む
            };
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += delegate
            {
                if ((Stamp() != _loadedStamp || _loadedText == null) && !_reloadTimer.IsEnabled) _reloadTimer.Start();
                if (DateTime.Today != _builtDay && !Busy) Rebuild();   // 開いたまま日付が変わったら、今日の印を移す
            };
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(NoticeSeconds) };
            _noticeTimer.Tick += delegate
            {
                _noticeTimer.Stop();
                _notice.Visibility = Visibility.Collapsed;
            };
            Loaded += delegate { StartWatching(); };
            Unloaded += delegate { StopWatching(); };   // 閉じたら捨てる（MainWindow が CalendarHost から外す）

            Load();
            _firstWeek = WeekStart(DateTime.Today).AddDays(-7 * WeeksBefore);
            _weekCount = WeeksBefore + 1 + WeeksAfter;
            Rebuild();

            // 並んでから、今週を上から2行目あたりに出す
            Loaded += delegate { Dispatcher.BeginInvoke(new Action(ScrollToToday), DispatcherPriority.Loaded); };
        }

        // ---------------------------------------------------------------
        // 並べる（週の行を下へずっと）
        // ---------------------------------------------------------------

        private static DateTime WeekStart(DateTime date)
        {
            return date.Date.AddDays(-(int)date.DayOfWeek);
        }

        /// <summary>出している週を全部組み直す（予定が変わったとき）。スクロールの位置は保つ。</summary>
        private void Rebuild()
        {
            _builtDay = DateTime.Today;
            var offset = _scroll.VerticalOffset;
            _weeks.Children.Clear();
            _rows.Clear();
            _cells.Clear();
            _found.Clear();
            for (var w = 0; w < _weekCount; w++)
            {
                var weekStart = _firstWeek.AddDays(7 * w);
                var row = BuildWeek(weekStart);
                _rows.Add(new KeyValuePair<DateTime, Grid>(weekStart, row));
                _weeks.Children.Add(row);
            }
            _scroll.ScrollToVerticalOffset(offset);
            UpdateTitle();

            if (_newBox != null)
            {
                var box = _newBox;
                Dispatcher.BeginInvoke(new Action(delegate { if (ReferenceEquals(box, _newBox)) box.Focus(); }), DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// 見えている週（と前後1週）だけを組み直す。帯をつかんでいる間に使う（1日動かすたびに、スクロールで溜まった何百週も組み直すと重いため）。
        /// 見えていない週は、つかむのを終えたときの Rebuild で直る。
        /// </summary>
        private void RebuildVisible()
        {
            DateTime first, last;
            VisibleRange(out first, out last);
            for (var i = 0; i < _rows.Count; i++)
            {
                var week = _rows[i].Key;
                if (week < first.AddDays(-7) || week > last.AddDays(7)) continue;
                var row = BuildWeek(week);
                _rows[i] = new KeyValuePair<DateTime, Grid>(week, row);
                _weeks.Children.RemoveAt(i);
                _weeks.Children.Insert(i, row);
            }
        }

        /// <summary>端へスクロールしたら、その先の週を足す。上に足したときは、見えている所が動かないようにずらす。</summary>
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateTitle();
            if (_extending || _rows.Count == 0 || _scroll.ExtentHeight <= 0) return;

            if (_scroll.VerticalOffset + _scroll.ViewportHeight > _scroll.ExtentHeight - ExtendMargin)
            {
                _extending = true;
                for (var i = 0; i < WeeksPerExtend; i++)
                {
                    var weekStart = _firstWeek.AddDays(7 * _weekCount);
                    var row = BuildWeek(weekStart);
                    _rows.Add(new KeyValuePair<DateTime, Grid>(weekStart, row));
                    _weeks.Children.Add(row);
                    _weekCount++;
                }
                _extending = false;
            }
            else if (_scroll.VerticalOffset < ExtendMargin)
            {
                _extending = true;
                var before = _scroll.ExtentHeight;
                var offset = _scroll.VerticalOffset;
                for (var i = 0; i < WeeksPerExtend; i++)
                {
                    _firstWeek = _firstWeek.AddDays(-7);
                    var row = BuildWeek(_firstWeek);
                    _rows.Insert(0, new KeyValuePair<DateTime, Grid>(_firstWeek, row));
                    _weeks.Children.Insert(0, row);
                    _weekCount++;
                }
                _weeks.UpdateLayout();
                _scroll.ScrollToVerticalOffset(offset + (_weeks.ActualHeight - before));
                _extending = false;
            }
        }

        /// <summary>上の段に、いちばん上に見えている週の月を出す。</summary>
        private void UpdateTitle()
        {
            var shown = _rows.Count > 0 ? _rows[0].Key : WeekStart(DateTime.Today);
            var top = 0.0;
            foreach (var pair in _rows)
            {
                var height = pair.Value.ActualHeight;
                if (top + height > _scroll.VerticalOffset + 1)
                {
                    shown = pair.Key;
                    break;
                }
                top += height;
            }
            var middle = shown.AddDays(3);
            _title.Text = middle.ToString("yyyy年M月", CultureInfo.InvariantCulture);
        }

        private void ScrollToToday()
        {
            ScrollToDate(DateTime.Today);
        }

        /// <summary>その日の週を上から2行目あたりに出す。出していない週なら、その週のまわりを組み直す。</summary>
        private void ScrollToDate(DateTime date)
        {
            var week = WeekStart(date);
            if (week < _firstWeek || week >= _firstWeek.AddDays(7 * _weekCount))
            {
                _firstWeek = week.AddDays(-7 * WeeksBefore);
                _weekCount = WeeksBefore + 1 + WeeksAfter;
                Rebuild();
                _weeks.UpdateLayout();
            }

            var top = 0.0;
            foreach (var pair in _rows)
            {
                if (pair.Key == week) break;
                top += pair.Value.ActualHeight;
            }
            _scroll.ScrollToVerticalOffset(Math.Max(0, top - RowMinHeight / 2));
        }

        private Grid BuildWeek(DateTime weekStart)
        {
            var weekEnd = weekStart.AddDays(6);
            var today = DateTime.Today;

            // この週に掛かる回（繰り返しの予定は、この週の分だけ数える）を、段に割り振る（始まりの早い順、同じなら長い順。空いている一番上の段へ）
            var shown = ShownIn(weekStart, weekEnd)
                              .OrderBy(occ => occ.Start).ThenByDescending(occ => occ.Days)
                              .ToList();
            var laneEnds = new List<int>();
            var placed = new List<Tuple<ScheduleOccurrence, int, int, int>>();   // 回・段・始まりの列・終わりの列
            foreach (var occ in shown)
            {
                var from = Math.Max(0, (int)(occ.Start - weekStart).TotalDays);
                var to = Math.Min(6, (int)(occ.End - weekStart).TotalDays);
                var lane = laneEnds.FindIndex(end => end < from);
                if (lane < 0)
                {
                    lane = laneEnds.Count;
                    laneEnds.Add(to);
                }
                else laneEnds[lane] = to;
                placed.Add(Tuple.Create(occ, lane, from, to));
            }

            // 名前を打っている日がこの週なら、その日の段の下に打つ欄を出す
            var newColumn = _newOn.HasValue && _newOn.Value >= weekStart && _newOn.Value <= weekEnd ? (int)(_newOn.Value - weekStart).TotalDays : -1;
            var lanes = laneEnds.Count + (newColumn >= 0 ? 1 : 0);

            double tall;
            var row = new Grid { MinHeight = _weekHeights.TryGetValue(weekStart, out tall) ? Math.Max(_rowHeight, tall) : _rowHeight };
            for (var d = 0; d < 7; d++) row.ColumnDefinitions.Add(new ColumnDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayNumberHeight) });
            for (var i = 0; i < lanes; i++) row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LaneHeight) });
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 6 });
            var rowCount = row.RowDefinitions.Count;

            for (var d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);

                var cell = new Border
                {
                    BorderBrush = Resource("BorderBrushSoft"),
                    BorderThickness = new Thickness(d == 0 ? 0 : 1, 0, 0, 1),
                    Background = CellFill(date),
                };
                Grid.SetColumn(cell, d);
                Grid.SetRowSpan(cell, rowCount);
                row.Children.Add(cell);
                _cells[date] = cell;

                // 月の初めの日は「12月1日」と出す（下へ続けて並べるので、どこから次の月かを分かるように）
                var first = date.Day == 1;
                var isToday = date == today;
                var number = new TextBlock
                {
                    Text = first ? date.ToString("M月d日", CultureInfo.InvariantCulture) : date.Day.ToString(CultureInfo.InvariantCulture),
                    Margin = isToday ? new Thickness(6, 0, 6, 0) : new Thickness(6, 3, 6, 0),
                    FontWeight = isToday || first ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.White
                        : date.DayOfWeek == DayOfWeek.Sunday ? SundayText
                        : date.DayOfWeek == DayOfWeek.Saturday ? SaturdayText
                        : Resource("TextPrimary"),
                    IsHitTestVisible = false,
                };

                if (isToday)
                {
                    // 今日は目立たせる（2026-11-21。利用者の希望）: マスを太い枠で囲み、日の数字を色の地に白で出し、「今日」と添える
                    var edge = new Border
                    {
                        BorderBrush = TodayEdge,
                        BorderThickness = new Thickness(2),
                        IsHitTestVisible = false,
                    };
                    Grid.SetColumn(edge, d);
                    Grid.SetRowSpan(edge, rowCount);
                    row.Children.Add(edge);

                    var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 0), IsHitTestVisible = false };
                    label.Children.Add(new Border
                    {
                        Background = TodayEdge,
                        CornerRadius = new CornerRadius(8),
                        MinWidth = 22,
                        Child = number,
                        VerticalAlignment = VerticalAlignment.Top,
                    });
                    number.HorizontalAlignment = HorizontalAlignment.Center;
                    label.Children.Add(new TextBlock
                    {
                        Text = "今日",
                        Margin = new Thickness(6, 1, 0, 0),
                        FontWeight = FontWeights.Bold,
                        Foreground = TodayEdge,
                    });
                    Grid.SetColumn(label, d);
                    row.Children.Add(label);
                }
                else
                {
                    Grid.SetColumn(number, d);
                    row.Children.Add(number);
                }
            }

            foreach (var entry in placed)
            {
                var bar = BuildBar(entry.Item1, weekStart, weekEnd);
                Grid.SetRow(bar, entry.Item2 + 1);
                Grid.SetColumn(bar, entry.Item3);
                Grid.SetColumnSpan(bar, entry.Item4 - entry.Item3 + 1);
                row.Children.Add(bar);
            }

            if (newColumn >= 0)
            {
                var box = NewBox();
                Grid.SetRow(box, laneEnds.Count + 1);
                Grid.SetColumn(box, newColumn);
                row.Children.Add(box);
            }

            row.Children.Add(WeekGripFor(row, weekStart, rowCount));
            return row;
        }

        /// <summary>日のマスの地。落とす先・選んでいる日・今日・偶数月の順に決める。</summary>
        private Brush CellFill(DateTime date)
        {
            if (date == _dropOn) return DropFill;
            if (IsDaySelected(date)) return Resource("SelectionFill");
            return BaseFill(date);
        }

        private bool IsDaySelected(DateTime date)
        {
            return SelectedFrom.HasValue && date >= SelectedFrom.Value && date <= SelectedTo.Value;
        }

        /// <summary>出している日のマスを塗り直す（選ぶ日・落とす先が変わったとき。組み直さない）。</summary>
        private void PaintCells()
        {
            foreach (var pair in _cells) pair.Value.Background = CellFill(pair.Key);
        }

        private void ClearDays()
        {
            if (!_dayAnchor.HasValue) return;
            _dayAnchor = null;
            _dayEnd = null;
            PaintCells();
        }

        private static Brush BaseFill(DateTime date)
        {
            return date == DateTime.Today ? TodayFill : date.Month % 2 == 0 ? EvenMonthFill : Brushes.Transparent;
        }

        // ---------------------------------------------------------------
        // 週の高さ（下の境を引っぱる。その週だけ広げる）
        // ---------------------------------------------------------------

        private void WeekHeightsChanged()
        {
            _weekHeightsChanged();
            UpdateResetHeights();
        }

        private void UpdateResetHeights()
        {
            _resetHeights.IsEnabled = _weekHeights.Count > 0 || _rowHeight != RowMinHeight;
            _shorter.IsEnabled = _rowHeight > RowMinHeight;
            _taller.IsEnabled = _rowHeight < RowHeightMax;
        }

        private void ResetWeekHeights()
        {
            if (_weekHeights.Count == 0 && _rowHeight == RowMinHeight) return;
            KeepTopWeek(delegate
            {
                _weekHeights.Clear();
                _rowHeight = RowMinHeight;
                _rowHeightChanged(0);
                WeekHeightsChanged();
                foreach (var pair in _rows) pair.Value.MinHeight = RowMinHeight;
            });
        }

        /// <summary>全部の週の高さを変える（広げた週は、その週の高さより低くはしない）。</summary>
        private void SetRowHeight(double height)
        {
            height = Math.Max(RowMinHeight, Math.Min(RowHeightMax, height));
            if (height == _rowHeight) return;
            KeepTopWeek(delegate
            {
                _rowHeight = height;
                _rowHeightChanged(height == RowMinHeight ? 0 : height);
                UpdateResetHeights();
                foreach (var pair in _rows)
                {
                    double tall;
                    pair.Value.MinHeight = _weekHeights.TryGetValue(pair.Key, out tall) ? Math.Max(_rowHeight, tall) : _rowHeight;
                }
            });
        }

        /// <summary>高さを変えても、いちばん上に見えている週の同じ所が上に出たままになるようにする。</summary>
        private void KeepTopWeek(Action change)
        {
            DateTime? anchor = null;
            var ratio = 0.0;
            var top = 0.0;
            foreach (var pair in _rows)
            {
                var height = pair.Value.ActualHeight;
                if (height > 0 && top + height > _scroll.VerticalOffset)
                {
                    anchor = pair.Key;
                    ratio = (_scroll.VerticalOffset - top) / height;
                    break;
                }
                top += height;
            }

            change();
            if (!anchor.HasValue) return;

            _weeks.UpdateLayout();
            top = 0.0;
            foreach (var pair in _rows)
            {
                if (pair.Key == anchor.Value)
                {
                    _scroll.ScrollToVerticalOffset(Math.Max(0, top + ratio * pair.Value.ActualHeight));
                    return;
                }
                top += pair.Value.ActualHeight;
            }
        }

        private Border WeekGripFor(Grid row, DateTime weekStart, int rowCount)
        {
            var grip = new Border
            {
                Height = WeekGrip,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeNS,
                ToolTip = "引っぱって、この週の高さを変える（2度押すと元の高さ）",
            };
            Grid.SetRow(grip, rowCount - 1);
            Grid.SetColumnSpan(grip, 7);

            grip.MouseEnter += delegate { grip.Background = GripHover; };
            grip.MouseLeave += delegate { if (!grip.IsMouseCaptured) grip.Background = Brushes.Transparent; };

            grip.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;   // 日を2度押したこと（予定を足す）にしない
                if (e.ClickCount >= 2)
                {
                    if (_weekHeights.Remove(weekStart)) WeekHeightsChanged();
                    row.MinHeight = _rowHeight;
                    return;
                }
                _sizingRow = row;
                _sizingWeek = weekStart;
                _sizingFromY = e.GetPosition(_weeks).Y;
                _sizingFromHeight = row.ActualHeight;
                grip.CaptureMouse();
            };

            grip.MouseMove += (sender, e) =>
            {
                if (!grip.IsMouseCaptured || _sizingRow != row) return;

                // 下の端まで来たら送る（見えている所より高くできるように）
                var inView = e.GetPosition(_scroll).Y;
                if (inView > _scroll.ViewportHeight - 16) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 12);

                row.MinHeight = Math.Max(_rowHeight, _sizingFromHeight + e.GetPosition(_weeks).Y - _sizingFromY);
            };

            grip.MouseLeftButtonUp += (sender, e) =>
            {
                e.Handled = true;
                if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
            };

            grip.LostMouseCapture += delegate
            {
                grip.Background = grip.IsMouseOver ? GripHover : Brushes.Transparent;
                if (_sizingRow != row) return;
                _sizingRow = null;

                // 全部の週の高さと変わらないなら覚えない（広げた週だけを持つ）
                if (row.MinHeight <= _rowHeight + 4)
                {
                    row.MinHeight = _rowHeight;
                    if (_weekHeights.Remove(_sizingWeek)) WeekHeightsChanged();
                }
                else
                {
                    _weekHeights[_sizingWeek] = Math.Round(row.MinHeight);
                    WeekHeightsChanged();
                }
            };

            return grip;
        }

        /// <summary>結んだ見出しがあるか（この組み直しの間は覚えておく）。直接打った予定はいつも true。</summary>
        private bool Found(ScheduleItem item)
        {
            if (!item.IsLinked) return true;
            HashSet<string> headings;
            if (!_found.TryGetValue(item.LinkFile, out headings))
            {
                try
                {
                    headings = _headingsOf(item.LinkFile);
                }
                catch (Exception)
                {
                    headings = null;   // 読めない文書（閉じていないフェンス・共有フォルダが切れた等）は、見つからないものとする
                }
                _found[item.LinkFile] = headings;
            }
            return headings != null && headings.Contains(item.LinkHeading);
        }

        /// <summary>
        /// from〜to に掛かる回。つかんでいる帯は、動かした・延ばした先の形で出す（繰り返しの予定を動かすときは、その回だけ。延ばすときは全部の回）。
        /// 回の Item は、いつも _book.Items の中の予定（選ぶ・つかむ・消すの相手）。
        /// </summary>
        private List<ScheduleOccurrence> ShownIn(DateTime from, DateTime to)
        {
            var list = new List<ScheduleOccurrence>();
            var spread = Math.Abs(_dragDelta);
            foreach (var item in _book.Items)
            {
                var dragging = ReferenceEquals(item, _dragItem) && _dragDelta != 0;
                var moveOne = dragging && item.IsRepeat && _gesture == ScheduleGesture.Move;
                var source = dragging && !moveOne ? ScheduleBook.Reshaped(item, _gesture, _dragDelta) : item;
                foreach (var occ in ScheduleRepeat.Occurrences(source, from.AddDays(-spread), to.AddDays(spread)))
                {
                    occ.Item = item;
                    if (moveOne && occ.Original == _dragOriginal)
                    {
                        occ.Start = occ.Start.AddDays(_dragDelta);
                        occ.End = occ.End.AddDays(_dragDelta);
                    }
                    if (occ.Start <= to && occ.End >= from) list.Add(occ);
                }
            }
            return list;
        }

        /// <summary>選んでいる帯か（繰り返しの予定なら、選んだ回だけ）。</summary>
        private bool IsSelected(ScheduleOccurrence occ)
        {
            return ReferenceEquals(occ.Item, _selected) && (!occ.IsRepeat || occ.Original == _selectedOriginal);
        }

        private Border BuildBar(ScheduleOccurrence occ, DateTime weekStart, DateTime weekEnd)
        {
            var item = occ.Item;
            var startsHere = occ.Start >= weekStart;
            var endsHere = occ.End <= weekEnd;
            var selected = IsSelected(occ);
            var startGrip = startsHere && !occ.IsRepeat;   // 繰り返しの予定は、右の端（全部の回の長さ）だけ引っぱれる

            var found = Found(item);
            var text = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Resource("TextPrimary"),
                Opacity = item.Title.Length > 0 && found ? 1 : 0.6,
            };
            // 見出しと結んだ予定は頭にリンクの印、見出しが見つからなければ警告の印（Segoe のアイコン）
            if (item.IsLinked)
                text.Inlines.Add(new System.Windows.Documents.Run(found ? "\uE71B " : "\uE7BA ")
                {
                    FontFamily = IconFont,
                    FontSize = 11,
                    Foreground = found ? Resource("AccentText") : SundayText,
                });
            // 通知のある予定は頭にベル（2026-11-21）
            if (item.Notify.HasValue)
                text.Inlines.Add(new System.Windows.Documents.Run("\uEA8F ")
                {
                    FontFamily = IconFont,
                    FontSize = 11,
                    Foreground = Resource("TextMuted"),
                });
            // 繰り返しの予定は頭に繰り返しの印
            if (item.IsRepeat)
                text.Inlines.Add(new System.Windows.Documents.Run("\uE8EE ")
                {
                    FontFamily = IconFont,
                    FontSize = 11,
                    Foreground = Resource("TextMuted"),
                });
            text.Inlines.Add(new System.Windows.Documents.Run((item.Title.Length > 0 ? item.Title : "（名前なし）") + (item.Memo.Trim().Length > 0 ? "  ≡" : string.Empty)));

            var bar = new Border
            {
                Child = text,
                Margin = new Thickness(startsHere ? 3 : 0, 1, endsHere ? 3 : 0, 1),
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(startsHere ? 3 : 0, endsHere ? 3 : 0, endsHere ? 3 : 0, startsHere ? 3 : 0),
                Background = FlashFill(item, occ.Days > 1 ? MultiFill : SingleFill),
                BorderBrush = selected ? Resource("AccentBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Opacity = ReferenceEquals(item, _dragItem) && _dragDelta != 0 && (!occ.IsRepeat || _gesture != ScheduleGesture.Move || occ.Original == _dragOriginal) ? 0.75 : 1,
                ToolTip = Tip(occ, found),
            };

            // 端に載せたら、延ばせる印（左右の矢印）にする。端は、その週で始まる・終わるときだけ
            bar.MouseMove += (sender, e) =>
            {
                if (_gesture != ScheduleGesture.None) return;
                bar.Cursor = EdgeOf(bar, e, startGrip, endsHere) != ScheduleGesture.Move ? Cursors.SizeWE : Cursors.Hand;
            };
            bar.MouseLeftButtonDown += (sender, e) => OnBarDown(occ, bar, e, startGrip, endsHere);
            return bar;
        }

        /// <summary>外で増えた・変わった予定は、光の色から元の色へ戻していく（組み直しても、光らせ始めた時刻から続ける）。</summary>
        private Brush FlashFill(ScheduleItem item, Brush normal)
        {
            if (!_flash.Contains(item)) return normal;
            var left = FlashSeconds - (DateTime.UtcNow - _flashStart).TotalSeconds;
            if (left <= 0) return normal;

            var to = ((SolidColorBrush)normal).Color;
            var brush = new SolidColorBrush(FlashColor);
            var animation = new ColorAnimation
            {
                From = Lerp(to, FlashColor, left / FlashSeconds),
                To = to,
                Duration = TimeSpan.FromSeconds(left),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            return brush;
        }

        private static Color Lerp(Color from, Color to, double t)
        {
            return Color.FromRgb((byte)(from.R + (to.R - from.R) * t), (byte)(from.G + (to.G - from.G) * t), (byte)(from.B + (to.B - from.B) * t));
        }

        private static ScheduleGesture EdgeOf(Border bar, MouseEventArgs e, bool startsHere, bool endsHere)
        {
            var x = e.GetPosition(bar).X;
            if (endsHere && x >= bar.ActualWidth - EdgeGrip) return ScheduleGesture.ResizeEnd;
            if (startsHere && x <= EdgeGrip) return ScheduleGesture.ResizeStart;
            return ScheduleGesture.Move;
        }

        private static string Tip(ScheduleOccurrence occ, bool found)
        {
            var item = occ.Item;
            var tip = Dates(occ.Start, occ.End) + "  " + item.Title;
            if (item.IsRepeat)
            {
                var rule = ScheduleRepeat.Parse(item.Repeat);
                tip += "\n繰り返し: " + (rule != null ? rule.Describe(item.Start) : item.Repeat + "（Tsuzuri で読めない決まり。1回目だけ出す）")
                       + (occ.Original.HasValue && occ.Original.Value != occ.Start ? "（この回は " + occ.Original.Value.ToString("M/d", CultureInfo.InvariantCulture) + " から動かした）" : string.Empty);
            }
            if (item.Notify.HasValue)
                tip += "\n通知: " + (item.IsRepeat ? "毎回" : item.Days > 1 ? "始まりの日" : "その日") + "の " + ScheduleFile.NotifyText(item.Notify.Value) + " に Windows の通知（Tsuzuri が動いている間）。Google カレンダーに送ればメールも";
            if (item.IsLinked)
                tip += "\n" + (found ? "見出し: " : "見出しが見つかりません（名前を変えた・消した）: ")
                       + Path.GetFileName(item.LinkFile) + " > " + item.LinkHeading
                       + (found ? "\n2度押すと、リンク先・見出しへ飛ぶ・削除の欄" : "\n直すときは、この予定を消してアウトラインからドラッグし直す");
            var memo = item.Memo.Trim();
            if (memo.Length > 0) tip += "\n\n" + (memo.Length > 600 ? memo.Substring(0, 600) + "…" : memo);
            return tip;
        }

        private static string Dates(DateTime start, DateTime end)
        {
            var dates = start.ToString("yyyy/M/d (ddd)", Japanese);
            if (end > start) dates += " 〜 " + end.ToString("M/d (ddd)", Japanese) + "（" + ((int)(end - start).TotalDays + 1) + " 日）";
            return dates;
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

        /// <summary>上の段の、アイコンだけのボタン（週の高さの －・＋・戻す）。</summary>
        private static Button IconButton(string glyph, string tip)
        {
            var button = ToolButton(glyph, tip);
            button.FontFamily = IconFont;
            button.FontSize = 12;
            button.Padding = new Thickness(7, 5, 7, 5);
            return button;
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

        private static TextBox Field()
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
