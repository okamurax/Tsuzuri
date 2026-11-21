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
    public sealed class CalendarView : Grid
    {
        private const double TopBarHeight = 32;     // 下の線を含む。左のアウトライン・右の AI の欄の上の段（32）とそろえる（2026-11-21）
        private const double DayHeaderHeight = 24;
        private const double DayNumberHeight = 22;
        private const double LaneHeight = 22;
        private const double RowMinHeight = 112;
        private const double EdgeGrip = 7;          // 帯の端のこの幅をつかむと延ばす・縮める
        private const int WeeksBefore = 8;          // 初めに出す、今週より前の週
        private const int WeeksAfter = 26;          // 初めに出す、今週より後の週
        private const int WeeksPerExtend = 12;      // 端へスクロールしたら足す週
        private const double ExtendMargin = 600;    // 端からこれだけ手前まで来たら足す
        private const int UndoLimit = 50;
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

        private enum Gesture { None, Move, ResizeStart, ResizeEnd }

        private readonly string _path;
        private readonly Action<string> _status;
        private readonly Func<ScheduleItem, bool> _linkFound;
        private readonly Action<ScheduleItem> _openLink;

        // 高さを広げた週（週の始まりの日 → 高さ）。持ち主は MainWindow（app.json に覚える）。変えたら _weekHeightsChanged
        private readonly Dictionary<DateTime, double> _weekHeights;
        private readonly Action _weekHeightsChanged;
        private readonly Button _resetHeights;

        // 週の下の境を引っぱっている
        private Grid _sizingRow;
        private DateTime _sizingWeek;
        private double _sizingFromY;
        private double _sizingFromHeight;

        // 組み直しのたびに、結んだ見出しがあるかを1回だけ見る（見るのは MainWindow。ファイルの更新日時で覚えている）
        private readonly Dictionary<ScheduleItem, bool> _found = new Dictionary<ScheduleItem, bool>();

        // 日のマス（アウトラインから落とす先を塗る）と、落とす先の日
        private readonly Dictionary<DateTime, Border> _cells = new Dictionary<DateTime, Border>();
        private DateTime? _dropOn;

        private List<ScheduleItem> _items = new List<ScheduleItem>();
        private DateTime _loadedStamp;
        private readonly List<List<ScheduleItem>> _undo = new List<List<ScheduleItem>>();

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
        private Gesture _gesture;
        private ScheduleItem _dragItem;
        private DateTime? _dragOriginal;   // 繰り返しの予定なら、つかんでいる回（決まりどおりの日）
        private DateTime _dragShownStart;  // つかんだときに出ていた、その回の始まりの日
        private DateTime _dragFrom;
        private int _dragDelta;

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
        private ScheduleItem _editing;
        private DateTime? _editingOriginal;   // 繰り返しの予定なら、欄を開いた回（決まりどおりの日）
        private bool _editorOpen;

        /// <param name="linkFound">アウトラインから登録した予定の見出しが、今もあるか。</param>
        /// <param name="openLink">その見出しへ飛ぶ（カレンダーは閉じる）。</param>
        /// <param name="weekHeights">高さを広げた週。ここで書き換え、そのたびに weekHeightsChanged を呼ぶ。</param>
        public CalendarView(string path, Action<string> status, Func<ScheduleItem, bool> linkFound, Action<ScheduleItem> openLink,
                            Dictionary<DateTime, double> weekHeights, Action weekHeightsChanged)
        {
            _path = path;
            _status = status;
            _linkFound = linkFound;
            _openLink = openLink;
            _weekHeights = weekHeights ?? new Dictionary<DateTime, double>();
            _weekHeightsChanged = weekHeightsChanged ?? delegate { };

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

            // 広げた週を全部元の高さに戻す（2026-11-21）。広げた週が無ければ押せない
            _resetHeights = ToolButton("高さを戻す", "広げた週を全部、元の高さに戻す");
            _resetHeights.Margin = new Thickness(0, 0, 6, 0);
            _resetHeights.Click += delegate { ResetWeekHeights(); };
            DockPanel.SetDock(_resetHeights, Dock.Right);
            bar.Children.Add(_resetHeights);
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

            _editTitle = Field(false);
            _editInterval = Field(false);
            _editUntil = Field(false);
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
            _pollTimer.Tick += delegate { if (Stamp() != _loadedStamp && !_reloadTimer.IsEnabled) _reloadTimer.Start(); };
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
        // 読み書き
        // ---------------------------------------------------------------

        private void Load()
        {
            try
            {
                _items = File.Exists(_path) ? ScheduleFile.Parse(File.ReadAllText(_path, Encoding.UTF8)) : new List<ScheduleItem>();
            }
            catch (Exception ex)
            {
                _items = new List<ScheduleItem>();
                _status("予定を読めませんでした: " + ex.Message);
            }
            _loadedStamp = Stamp();
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

            ReloadKeepingSelection();
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
        // 見張る（外で書き換わったら、すぐ出し直す。2026-11-21）
        // ---------------------------------------------------------------

        private void StartWatching()
        {
            _pollTimer.Start();
            if (_watcher != null) return;
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                var watcher = new FileSystemWatcher(folder, Path.GetFileName(_path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (sender, e) => Poke();
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (sender, e) => Poke();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception)
            {
                // 見張れない場所（共有フォルダなど）は、2秒ごとに更新日時を見るだけにする
            }
        }

        private void StopWatching()
        {
            _pollTimer.Stop();
            _reloadTimer.Stop();
            _noticeTimer.Stop();
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        /// <summary>見張りの知らせ（見張りのスレッドから）。画面のスレッドで、少し待ってから読む。</summary>
        private void Poke()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }));
        }

        /// <summary>予定を見分ける形（日・名前・結んだ見出し・メモ）。読み直したときに、増えた・変わった予定を探す。</summary>
        private static string KeyOf(ScheduleItem item)
        {
            return item.Start.Ticks + "|" + item.End.Ticks + "|" + item.Title + "|" + item.LinkFile + "|" + item.LinkPage + "|" + item.LinkHeading + "|" + item.Memo
                   + "|" + item.Repeat + "|" + string.Join(",", item.Skips.Select(date => date.Ticks))
                   + "|" + string.Join(",", item.Moves.Select(pair => pair.Key.Ticks + ">" + pair.Value.Ticks));
        }

        /// <summary>外で書き換わって読み直したとき。増えた・変わった予定を光らせ、上の段に何件変わったかを出す。</summary>
        private void MarkChanges(List<ScheduleItem> before)
        {
            var oldKeys = new Dictionary<string, int>();
            foreach (var item in before)
            {
                var key = KeyOf(item);
                oldKeys[key] = oldKeys.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            var added = new List<ScheduleItem>();
            foreach (var item in _items)
            {
                var key = KeyOf(item);
                int n;
                if (oldKeys.TryGetValue(key, out n) && n > 0) oldKeys[key] = n - 1;
                else added.Add(item);
            }
            var removed = oldKeys.Values.Sum();
            var changed = Math.Min(added.Count, removed);   // 変わった予定は「消えて増えた」に見える。少ない方を「変わった」と数える

            _flash = new HashSet<ScheduleItem>(added);
            _flashStart = DateTime.UtcNow;
            if (added.Count == 0 && removed == 0) return;

            var parts = new List<string>();
            if (added.Count - changed > 0) parts.Add("増えた " + (added.Count - changed));
            if (changed > 0) parts.Add("変わった " + changed);
            if (removed - changed > 0) parts.Add("消えた " + (removed - changed));
            var message = "外で変わりました: " + string.Join("・", parts);

            // 見えていない週で変わった予定があれば、押すとそこへ
            DateTime first, last;
            VisibleRange(out first, out last);
            // 繰り返しの予定は、見えている週に回が無ければ、次の回（無ければ1回目）
            DateTime? outside = null;
            foreach (var item in added)
            {
                if (ScheduleRepeat.Occurrences(item, first, last).Count > 0) continue;
                var next = ScheduleRepeat.Occurrences(item, last.AddDays(1), last.AddDays(800)).Select(occ => (DateTime?)occ.Start).Min() ?? item.Start;
                if (!outside.HasValue || next < outside.Value) outside = next;
            }
            _noticeDate = outside;
            if (outside.HasValue) message += "（" + outside.Value.ToString("M/d", CultureInfo.InvariantCulture) + " へ ›）";

            _notice.Content = message;
            _notice.IsHitTestVisible = outside.HasValue;
            _notice.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            _noticeTimer.Start();
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
            var row = new Grid { MinHeight = _weekHeights.TryGetValue(weekStart, out tall) ? Math.Max(RowMinHeight, tall) : RowMinHeight };
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
            _resetHeights.IsEnabled = _weekHeights.Count > 0;
        }

        private void ResetWeekHeights()
        {
            if (_weekHeights.Count == 0) return;
            _weekHeights.Clear();
            WeekHeightsChanged();
            foreach (var pair in _rows) pair.Value.MinHeight = RowMinHeight;
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
                    row.MinHeight = RowMinHeight;
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

                row.MinHeight = Math.Max(RowMinHeight, _sizingFromHeight + e.GetPosition(_weeks).Y - _sizingFromY);
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

                // 元の高さと変わらないなら覚えない（広げた週だけを持つ）
                if (row.MinHeight <= RowMinHeight + 4)
                {
                    row.MinHeight = RowMinHeight;
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
            bool found;
            if (_found.TryGetValue(item, out found)) return found;
            try
            {
                found = _linkFound(item);
            }
            catch (Exception)
            {
                found = false;
            }
            _found[item] = found;
            return found;
        }

        /// <summary>
        /// from〜to に掛かる回。つかんでいる帯は、動かした・延ばした先の形で出す（繰り返しの予定を動かすときは、その回だけ。延ばすときは全部の回）。
        /// 回の Item は、いつも _items の中の予定（選ぶ・つかむ・消すの相手）。
        /// </summary>
        private List<ScheduleOccurrence> ShownIn(DateTime from, DateTime to)
        {
            var list = new List<ScheduleOccurrence>();
            var spread = Math.Abs(_dragDelta);
            foreach (var item in _items)
            {
                var dragging = ReferenceEquals(item, _dragItem) && _dragDelta != 0;
                var moveOne = dragging && item.IsRepeat && _gesture == Gesture.Move;
                var source = dragging && !moveOne ? Apply(item, _gesture, _dragDelta) : item;
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

        private static ScheduleItem Apply(ScheduleItem item, Gesture gesture, int delta)
        {
            var moved = item.Clone();
            switch (gesture)
            {
                case Gesture.Move:
                    moved.Start = item.Start.AddDays(delta);
                    moved.End = item.End.AddDays(delta);
                    break;
                case Gesture.ResizeEnd:
                    moved.End = item.End.AddDays(delta) < item.Start ? item.Start : item.End.AddDays(delta);
                    break;
                case Gesture.ResizeStart:
                    moved.Start = item.Start.AddDays(delta) > item.End ? item.End : item.Start.AddDays(delta);
                    break;
            }
            return moved;
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
                Opacity = ReferenceEquals(item, _dragItem) && _dragDelta != 0 && (!occ.IsRepeat || _gesture != Gesture.Move || occ.Original == _dragOriginal) ? 0.75 : 1,
                ToolTip = Tip(occ, found),
            };

            // 端に載せたら、延ばせる印（左右の矢印）にする。端は、その週で始まる・終わるときだけ
            bar.MouseMove += (sender, e) =>
            {
                if (_gesture != Gesture.None) return;
                bar.Cursor = EdgeOf(bar, e, startGrip, endsHere) != Gesture.Move ? Cursors.SizeWE : Cursors.Hand;
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

        private static Gesture EdgeOf(Border bar, MouseEventArgs e, bool startsHere, bool endsHere)
        {
            var x = e.GetPosition(bar).X;
            if (endsHere && x >= bar.ActualWidth - EdgeGrip) return Gesture.ResizeEnd;
            if (startsHere && x <= EdgeGrip) return Gesture.ResizeStart;
            return Gesture.Move;
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
        // マウス
        // ---------------------------------------------------------------

        /// <summary>押した場所の日（_weeks の中の位置）。上下の外なら端の週に寄せる。</summary>
        private DateTime? DateAt(Point point)
        {
            if (_rows.Count == 0) return null;

            var top = 0.0;
            var hit = _rows[_rows.Count - 1];
            if (point.Y < 0) hit = _rows[0];
            else
            {
                foreach (var pair in _rows)
                {
                    var height = pair.Value.ActualHeight;
                    if (point.Y < top + height)
                    {
                        hit = pair;
                        break;
                    }
                    top += height;
                }
            }

            var width = hit.Value.ActualWidth;
            if (width <= 0) return null;
            var column = Math.Max(0, Math.Min(6, (int)Math.Floor(point.X / (width / 7))));
            return hit.Key.AddDays(column);
        }

        private void OnBarDown(ScheduleOccurrence occ, Border bar, MouseButtonEventArgs e, bool startsHere, bool endsHere)
        {
            var item = occ.Item;
            e.Handled = true;
            if (_editorOpen) return;

            // 押した端と日は、組み直す前に読む（マスで打っていた名前を足すと組み直し、この帯は画面から外れる）
            var gesture = EdgeOf(bar, e, startsHere, endsHere);
            var date = DateAt(e.GetPosition(_weeks));
            CommitNew();
            Focus();

            if (e.ClickCount == 2)
            {
                // どの予定も欄を出す。見出しと結んだ予定は、名前の代わりにリンク先と「見出しへ飛ぶ」（2026-11-21。前は2度押すとすぐ飛び、消す入口が無かった）
                OpenEditor(item, occ.Original, occ.Start, occ.End);
                return;
            }
            if (date == null) return;

            ClearDays();   // 予定を選んだら、日を選ぶのはやめる
            if (!IsSelected(occ))
            {
                _selected = item;
                _selectedOriginal = occ.Original;
                Rebuild();
            }

            _gesture = gesture;
            _dragItem = item;
            _dragOriginal = occ.Original;
            _dragShownStart = occ.Start;
            _dragFrom = date.Value;
            _dragDelta = 0;
            _weeks.CaptureMouse();
        }

        private void OnWeeksDown(object sender, MouseButtonEventArgs e)
        {
            if (_editorOpen) return;
            if (_newBox != null && _newBox.IsMouseOver) return;   // 打っている欄の中を押した

            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;
            e.Handled = true;

            CommitNew();
            Focus();

            if (e.ClickCount == 2)
            {
                // その日のマスで名前を打つ（1日の予定。何日かにするのは、あとで帯の端を引っぱる）
                _selected = null;
                _newOn = date.Value;
                Rebuild();
                return;
            }

            if (_selected != null)
            {
                _selected = null;
                Rebuild();
            }

            // 日を選ぶ。Shift で押すと、選び始めた日からその日まで。押したままなぞっても延ばせる
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _dayAnchor.HasValue) _dayEnd = date.Value;
            else
            {
                _dayAnchor = date.Value;
                _dayEnd = date.Value;
                _selectingDays = true;
                _weeks.CaptureMouse();
            }
            PaintCells();
        }

        private void OnWeeksMove(object sender, MouseEventArgs e)
        {
            if (!_weeks.IsMouseCaptured || (_dragItem == null && !_selectingDays)) return;

            // 上下の端へ寄せたら、少しずつスクロールする（見えていない週まで延ばす・動かす・選ぶため）
            var inView = e.GetPosition(_scroll).Y;
            if (inView < 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 16);
            else if (inView > _scroll.ViewportHeight - 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 16);

            var date = DateAt(e.GetPosition(_weeks));
            if (date == null) return;

            if (_selectingDays)
            {
                if (date != _dayEnd)
                {
                    _dayEnd = date;
                    PaintCells();
                }
                return;
            }
            var delta = (int)(date.Value - _dragFrom).TotalDays;
            if (delta == _dragDelta) return;
            _dragDelta = delta;
            Rebuild();
        }

        private void OnWeeksUp(object sender, MouseButtonEventArgs e)
        {
            if (!_weeks.IsMouseCaptured) return;
            EndGesture(true);
        }

        /// <summary>帯をつかむのを終える。commit なら、動かした・延ばした形を保存する。</summary>
        private void EndGesture(bool commit)
        {
            var item = _dragItem;
            var gesture = _gesture;
            var delta = _dragDelta;

            _dragItem = null;
            _gesture = Gesture.None;
            _dragDelta = 0;
            _selectingDays = false;
            if (_weeks.IsMouseCaptured) _weeks.ReleaseMouseCapture();
            if (item == null) return;

            var original = _dragOriginal;
            var shownStart = _dragShownStart;
            _dragOriginal = null;
            if (commit && delta != 0 && Fresh())
            {
                var target = _items.FirstOrDefault(other => ReferenceEquals(other, item));
                if (target != null && target.IsRepeat && original.HasValue && gesture == Gesture.Move)
                {
                    // 繰り返しの予定を動かすと、その回だけ動く。元の日へ戻したら、動かした記録を消す
                    var to = shownStart.AddDays(delta);
                    Remember();
                    if (to == original.Value) target.Moves.Remove(original.Value);
                    else target.Moves[original.Value] = to;
                    Save();
                }
                else if (target != null)
                {
                    var changed = Apply(target, gesture, delta);
                    if (changed.Start != target.Start || changed.End != target.End)
                    {
                        Remember();
                        target.Start = changed.Start;
                        target.End = changed.End;
                        Save();
                    }
                }
            }
            Rebuild();
        }

        // ---------------------------------------------------------------
        // その日のマスで名前を打つ（足す）
        // ---------------------------------------------------------------

        private TextBox NewBox()
        {
            if (_newBox != null) return Detach(_newBox);

            var box = new TextBox
            {
                Margin = new Thickness(3, 1, 3, 1),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Resource("PageBackground"),
                Foreground = Resource("TextPrimary"),
                CaretBrush = Resource("TextPrimary"),
                BorderBrush = Resource("AccentBrush"),
                BorderThickness = new Thickness(1),
                ToolTip = "予定の名前（時間もここに）。Enter で足す、Esc でやめる。何日かにするのは、足したあとで帯の端を引っぱる",
            };
            box.KeyDown += (sender, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    CommitNew();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CancelNew();
                }
            };
            box.LostKeyboardFocus += (sender, e) =>
            {
                // ほかの所を押した・別の窓へ移った。打った名前は足す（捨てない）
                if (ReferenceEquals(box, _newBox) && !box.IsKeyboardFocusWithin && !_extending)
                    Dispatcher.BeginInvoke(new Action(delegate { if (ReferenceEquals(box, _newBox) && !box.IsKeyboardFocusWithin) CommitNew(); }), DispatcherPriority.Input);
            };
            _newBox = box;
            return box;
        }

        private static TextBox Detach(TextBox box)
        {
            var parent = box.Parent as Panel;
            if (parent != null) parent.Children.Remove(box);
            return box;
        }

        /// <summary>打っている名前で予定を足す（空ならやめる）。打っていなければ何もしない。</summary>
        private void CommitNew()
        {
            if (_newBox == null || !_newOn.HasValue) return;

            var title = ScheduleFile.OneLine(_newBox.Text);
            var date = _newOn.Value;
            _newBox = null;
            _newOn = null;

            if (title.Length > 0 && Fresh())
            {
                Remember();
                var item = new ScheduleItem { Start = date, End = date, Title = title };
                _items.Add(item);
                _selected = item;
                _selectedOriginal = null;
                Save();
            }
            Rebuild();
            Focus();
        }

        private void CancelNew()
        {
            if (_newBox == null) return;
            _newBox = null;
            _newOn = null;
            Rebuild();
            Focus();
        }

        // ---------------------------------------------------------------
        // アウトラインの見出しを落として登録する（MainWindow.Calendar.cs が、アウトラインのドラッグから呼ぶ）
        // ---------------------------------------------------------------

        /// <summary>アウトラインの見出しをつかんで、この位置（カレンダーの中の位置）に来た。日の上なら、その日を塗って true。</summary>
        public bool ShowDrop(Point point)
        {
            DateTime? date = null;
            if (!_editorOpen && point.X >= 0 && point.Y >= 0 && point.X <= ActualWidth && point.Y <= ActualHeight)
            {
                var inView = TranslatePoint(point, _scroll);
                if (inView.Y >= 0 && inView.Y <= _scroll.ViewportHeight) date = DateAt(TranslatePoint(point, _weeks));
            }

            if (date != _dropOn)
            {
                var old = _dropOn;
                _dropOn = date;
                if (old.HasValue && _cells.ContainsKey(old.Value)) _cells[old.Value].Background = CellFill(old.Value);
                if (date.HasValue && _cells.ContainsKey(date.Value)) _cells[date.Value].Background = CellFill(date.Value);
            }
            return date.HasValue;
        }

        /// <summary>
        /// 塗った日に、その見出しと結んだ1日の予定を足す（名前は見出しの文字）。塗った日が無ければ何もしない。
        /// file は保存してある文書（保存していない文書は、呼ぶ側で断る）。
        /// </summary>
        public void DropHeading(string file, string page, string heading)
        {
            var date = _dropOn;
            ClearDrop();
            if (!date.HasValue || !Fresh()) return;

            CommitNew();
            Remember();
            var item = new ScheduleItem
            {
                Start = date.Value,
                End = date.Value,
                Title = ScheduleFile.OneLine(heading),
                LinkFile = file,
                LinkPage = page ?? string.Empty,
                LinkHeading = heading,
            };
            _items.Add(item);
            _selected = item;
            _selectedOriginal = null;
            Save();
            Rebuild();
            Focus();
            _status("見出しを予定にしました（何日かにするときは帯の端を引っぱる）: " + heading);
        }

        /// <summary>落とすのをやめた（塗った日を戻す）。</summary>
        public void ClearDrop()
        {
            ShowDrop(new Point(-1, -1));
        }

        // ---------------------------------------------------------------
        // AI に渡す今の状態（2026-11-21。MainWindow.Ai の AiContext）
        // ---------------------------------------------------------------

        /// <summary>予定の保存先。</summary>
        public string FilePath
        {
            get { return _path; }
        }

        /// <summary>選んでいる日の初め（選んでいなければ null）。</summary>
        public DateTime? SelectedFrom
        {
            get { return _dayAnchor.HasValue && _dayEnd.HasValue ? (_dayAnchor.Value < _dayEnd.Value ? _dayAnchor : _dayEnd) : null; }
        }

        /// <summary>選んでいる日の終わり（その日を含む。選んでいなければ null）。</summary>
        public DateTime? SelectedTo
        {
            get { return _dayAnchor.HasValue && _dayEnd.HasValue ? (_dayAnchor.Value < _dayEnd.Value ? _dayEnd : _dayAnchor) : null; }
        }

        /// <summary>選んでいる予定の写し（選んでいなければ null）。</summary>
        public ScheduleItem SelectedItem
        {
            get { return _selected != null ? _selected.Clone() : null; }
        }

        /// <summary>選んでいる回の始まり・終わりの日（繰り返しの予定なら、その回。動かした回は動かした先）。選んでいなければ null。</summary>
        public Tuple<DateTime, DateTime> SelectedDates
        {
            get
            {
                if (_selected == null) return null;
                if (!_selected.IsRepeat || !_selectedOriginal.HasValue) return Tuple.Create(_selected.Start, _selected.End);
                DateTime start;
                if (!_selected.Moves.TryGetValue(_selectedOriginal.Value, out start)) start = _selectedOriginal.Value;
                return Tuple.Create(start, start.AddDays((_selected.End - _selected.Start).TotalDays));
            }
        }

        /// <summary>見えている日の範囲（いちばん上に見えている週の日曜から、いちばん下に見えている週の土曜まで）。並ぶ前は今週。</summary>
        public void VisibleRange(out DateTime first, out DateTime last)
        {
            first = WeekStart(DateTime.Today);
            last = first.AddDays(6);
            var top = 0.0;
            var found = false;
            foreach (var pair in _rows)
            {
                var height = pair.Value.ActualHeight;
                var bottom = top + height;
                if (height > 0 && bottom > _scroll.VerticalOffset + 1 && top < _scroll.VerticalOffset + _scroll.ViewportHeight - 1)
                {
                    if (!found) first = pair.Key;
                    found = true;
                    last = pair.Key.AddDays(6);
                }
                top = bottom;
            }
        }

        /// <summary>
        /// 保存先が外（Claude など）で書き換わっていたら読み直して出し直す。打ちかけ・欄で直している最中・帯をつかんでいる最中は読み直さない
        /// （そのときは、次に直すときに Fresh が読み直す）。
        /// </summary>
        /// <returns>読み直した・読み直す要が無かったら true。打っている最中などで待つなら false（見張りが少し後にまた呼ぶ）。</returns>
        public bool ReloadIfChanged()
        {
            if (Stamp() == _loadedStamp) return true;
            if (_editorOpen || _newBox != null || _dragItem != null || _selectingDays || _sizingRow != null) return false;

            ReloadKeepingSelection();
            return true;
        }

        /// <summary>外で書き換わった保存先を読み直す。選んでいた予定は、同じ形の予定があれば選んだままにし、増えた・変わった予定を光らせる。</summary>
        private void ReloadKeepingSelection()
        {
            var before = _items;
            var selectedKey = _selected != null ? KeyOf(_selected) : null;
            Load();
            _undo.Clear();
            if (selectedKey != null) _selected = _items.FirstOrDefault(item => KeyOf(item) == selectedKey);
            MarkChanges(before);
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
            if (_newBox != null && _newBox.IsKeyboardFocusWithin) return;

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.Delete:
                    if (_selected == null) return;
                    e.Handled = true;
                    Delete(_selected, _selectedOriginal);   // 繰り返しの予定は、選んだ回だけ
                    break;
                case Key.Z:
                    if (!ctrl) return;
                    e.Handled = true;
                    Undo();
                    break;
                case Key.PageUp:
                    e.Handled = true;
                    _scroll.PageUp();
                    break;
                case Key.PageDown:
                    e.Handled = true;
                    _scroll.PageDown();
                    break;
                case Key.Up:
                    e.Handled = true;
                    _scroll.LineUp();
                    break;
                case Key.Down:
                    e.Handled = true;
                    _scroll.LineDown();
                    break;
                case Key.Home:
                    e.Handled = true;
                    ScrollToToday();
                    break;
                case Key.Escape:
                    // 選んでいる予定・日があれば、選ぶのをやめる（カレンダーは閉じない。閉じるのはタブだけ）
                    if (_selected == null && !_dayAnchor.HasValue) return;
                    e.Handled = true;
                    ClearDays();
                    if (_selected != null)
                    {
                        _selected = null;
                        Rebuild();
                    }
                    break;
            }
        }

        /// <summary>予定を消す。繰り返しの予定で original（その回の決まりどおりの日）があれば、その回だけ消す。</summary>
        private void Delete(ScheduleItem item, DateTime? original)
        {
            if (!Fresh()) return;
            var target = _items.FirstOrDefault(other => ReferenceEquals(other, item));
            if (target == null) return;

            Remember();
            if (target.IsRepeat && original.HasValue)
            {
                target.Moves.Remove(original.Value);
                if (!target.Skips.Contains(original.Value)) target.Skips.Add(original.Value);
                target.Skips.Sort();
                Save();
                if (ReferenceEquals(_selected, target)) _selected = null;
                Rebuild();
                _status("この回だけ消しました（Ctrl+Z で戻せます）: " + original.Value.ToString("M/d", CultureInfo.InvariantCulture) + " " + target.Title);
                return;
            }
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
        // 名前と繰り返しを直す欄（日付は帯で直す。メモの欄は 2026-11-21 にやめた。利用者の判断）
        // 繰り返しは、直すといつも全部の回が変わる（利用者の判断。この回だけ変えるのは、帯で動かすか消すだけ）
        // ---------------------------------------------------------------

        private UIElement BuildEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
            _editorLayer.Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));

            var panel = new StackPanel();
            _editDates.Foreground = Resource("TextMuted");
            _editDates.Margin = new Thickness(0, 0, 0, 10);
            _editDates.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(_editDates);
            _editLabel.Foreground = Resource("TextMuted");
            _editLabel.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(_editLabel);
            panel.Children.Add(_editTitle);

            // 繰り返し: 種類・間隔・曜日・終わりの日
            var repeatLabel = Label("繰り返し（直すと全部の回が変わる）");
            repeatLabel.Margin = new Thickness(0, 12, 0, 4);
            panel.Children.Add(repeatLabel);
            _editRepeat.Style = (Style)Application.Current.FindResource("DarkComboBox");
            _editRepeat.SelectionChanged += delegate { UpdateRepeatFields(); };
            panel.Children.Add(_editRepeat);

            _editIntervalRow.Orientation = Orientation.Horizontal;
            _editIntervalRow.Margin = new Thickness(0, 8, 0, 0);
            _editInterval.Width = 56;
            _editInterval.HorizontalContentAlignment = HorizontalAlignment.Right;
            _editIntervalRow.Children.Add(_editInterval);
            _editIntervalUnit.Margin = new Thickness(8, 0, 0, 0);
            _editIntervalUnit.VerticalAlignment = VerticalAlignment.Center;
            _editIntervalUnit.Foreground = Resource("TextPrimary");
            _editIntervalRow.Children.Add(_editIntervalUnit);
            panel.Children.Add(_editIntervalRow);

            _editWeekDays.Orientation = Orientation.Horizontal;
            _editWeekDays.Margin = new Thickness(0, 8, 0, 0);
            for (var d = 0; d < 7; d++)
            {
                var day = (DayOfWeek)d;
                var chip = new Border
                {
                    Width = 34,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 0),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = Japanese.DateTimeFormat.GetShortestDayName(day),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = d == 0 ? SundayText : d == 6 ? SaturdayText : Resource("TextPrimary"),
                    },
                };
                chip.MouseLeftButtonDown += (sender, e) =>
                {
                    e.Handled = true;
                    if (_editing != null && day == _editing.Start.DayOfWeek) return;   // 1回目の曜日は外せない
                    if (!_editDays.Remove(day)) _editDays.Add(day);
                    PaintWeekDays();
                };
                _editDayChips[d] = chip;
                _editWeekDays.Children.Add(chip);
            }
            panel.Children.Add(_editWeekDays);

            _editUntilLabel = Label("終わりの日（空なら無期限。例 2027-03-31・3/31）");
            _editUntilLabel.Margin = new Thickness(0, 8, 0, 4);
            panel.Children.Add(_editUntilLabel);
            panel.Children.Add(_editUntil);

            var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            _editDelete.Click += delegate
            {
                var item = _editing;
                var original = _editingOriginal;
                CloseEditor();
                if (item != null) Delete(item, original);
            };
            DockPanel.SetDock(_editDelete, Dock.Left);
            buttons.Children.Add(_editDelete);
            _editDeleteAll.Click += delegate
            {
                var item = _editing;
                CloseEditor();
                if (item != null) Delete(item, null);
            };
            DockPanel.SetDock(_editDeleteAll, Dock.Left);
            buttons.Children.Add(_editDeleteAll);

            var save = new Button { Content = "保存", Style = (Style)Application.Current.FindResource("AccentButton"), MinWidth = 72 };
            save.Click += delegate { CommitEdit(); };

            // 見出しと結んだ予定は、名前の代わりにリンク先を出し、「見出しへ飛ぶ」も置く（2026-11-21）
            var jump = ToolButton("見出しへ飛ぶ", "文書のその見出しへ（カレンダーは閉じる）");
            jump.Margin = new Thickness(0, 0, 6, 0);
            jump.Click += delegate
            {
                var item = _editing;
                if (!CommitEdit()) return;   // 繰り返しを変えていれば保存してから（終わりの日が読めなければ飛ばない）
                if (item != null) _openLink(item);
            };
            _editJump = jump;

            var cancel = ToolButton("キャンセル", "直さずに閉じる（Esc）");
            cancel.Margin = new Thickness(0, 0, 6, 0);
            cancel.Click += delegate { CloseEditor(); };
            DockPanel.SetDock(save, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            DockPanel.SetDock(jump, Dock.Right);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            buttons.Children.Add(jump);
            panel.Children.Add(buttons);

            KeyEventHandler enter = (sender, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                CommitEdit();
            };
            _editTitle.KeyDown += enter;
            _editInterval.KeyDown += enter;
            _editUntil.KeyDown += enter;

            _editorLayer.Children.Add(new Border
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
            });
            return _editorLayer;
        }

        /// <summary>繰り返しの種類の選び肢（始まりの日から作る。「毎月 24日」「毎月 第4火曜」…）。読めない決まりなら「そのまま」も。</summary>
        private void FillRepeatChoices(ScheduleItem item, bool keep)
        {
            var start = item.Start;
            var nth = (start.Day - 1) / 7 + 1;
            var dayName = Japanese.DateTimeFormat.GetShortestDayName(start.DayOfWeek);
            var rule = ScheduleRepeat.Parse(item.Repeat);
            _editRepeat.Items.Clear();
            if (keep)
                _editRepeat.Items.Add(new ComboBoxItem
                {
                    Content = "そのまま（" + (rule != null ? rule.Describe(start) : "Tsuzuri で読めない決まり: " + item.Repeat) + "）",
                    Tag = "keep",
                });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "なし", Tag = "none" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎日", Tag = "daily" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "平日（月〜金）", Tag = "weekdays" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎週（曜日を選ぶ）", Tag = "weekly" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎月 " + start.Day + "日", Tag = "monthday" });
            if (nth <= 4) _editRepeat.Items.Add(new ComboBoxItem { Content = "毎月 第" + nth + dayName + "曜", Tag = "monthnth" });
            if (start.AddDays(7).Month != start.Month) _editRepeat.Items.Add(new ComboBoxItem { Content = "毎月 最終" + dayName + "曜", Tag = "monthlast" });
            _editRepeat.Items.Add(new ComboBoxItem { Content = "毎年 " + start.Month + "月" + start.Day + "日", Tag = "yearly" });
        }

        private string RepeatChoice
        {
            get
            {
                var choice = _editRepeat.SelectedItem as ComboBoxItem;
                return choice != null ? (string)choice.Tag : "none";
            }
        }

        private void SelectRepeatChoice(string tag)
        {
            foreach (ComboBoxItem choice in _editRepeat.Items)
            {
                if ((string)choice.Tag != tag) continue;
                _editRepeat.SelectedItem = choice;
                return;
            }
            _editRepeat.SelectedIndex = 0;
        }

        /// <summary>選んだ種類に合わせて、間隔・曜日・終わりの日を出し入れする。</summary>
        private void UpdateRepeatFields()
        {
            var choice = RepeatChoice;
            var repeats = choice != "none" && choice != "keep";
            _editIntervalRow.Visibility = repeats && choice != "weekdays" ? Visibility.Visible : Visibility.Collapsed;
            _editIntervalUnit.Text = choice == "daily" ? "日ごと" : choice == "weekly" ? "週ごと" : choice == "yearly" ? "年ごと" : "か月ごと";
            _editWeekDays.Visibility = choice == "weekly" ? Visibility.Visible : Visibility.Collapsed;
            _editUntilLabel.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
            _editUntil.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
            PaintWeekDays();
        }

        /// <summary>
        /// 曜日のボタンを塗る。1回目の曜日は、いつも選んだことにして薄く出し、押せなくする（2026-11-21。利用者の判断。
        /// 1回目はいつも回に数えるので、その曜日を外すと、外した曜日に1回だけ出てしまうため）。
        /// </summary>
        private void PaintWeekDays()
        {
            var first = _editing != null ? _editing.Start.DayOfWeek : (DayOfWeek?)null;
            if (first.HasValue && !_editDays.Contains(first.Value)) _editDays.Add(first.Value);
            for (var d = 0; d < 7; d++)
            {
                var on = _editDays.Contains((DayOfWeek)d);
                var locked = first.HasValue && (DayOfWeek)d == first.Value;
                var chip = _editDayChips[d];
                chip.Background = on ? Resource("AccentFill") : Brushes.Transparent;
                chip.BorderBrush = locked ? Resource("InputBorder") : on ? TodayEdge : Resource("InputBorder");
                chip.Opacity = locked ? 0.45 : 1;
                chip.Cursor = locked ? Cursors.Arrow : Cursors.Hand;
                chip.ToolTip = locked ? "1回目の曜日（外せない）" : null;
            }
        }

        private void OpenEditor(ScheduleItem item, DateTime? original, DateTime shownStart, DateTime shownEnd)
        {
            CommitNew();
            _editing = item;
            _editingOriginal = original;

            var rule = ScheduleRepeat.Parse(item.Repeat);
            var dates = Dates(shownStart, shownEnd);
            if (item.IsRepeat)
                dates += "\n繰り返し: " + (rule != null ? rule.Describe(item.Start) : item.Repeat) + "（" + item.Start.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + " から）"
                         + (original.HasValue && original.Value != shownStart ? "\nこの回は " + original.Value.ToString("M/d (ddd)", Japanese) + " から動かした" : string.Empty);
            _editDates.Text = dates;

            if (item.IsLinked)
            {
                // 名前は見出しの文字のまま（直せない）。代わりにリンク先を出す（選んで写せる）
                _editLabel.Text = Found(item) ? "リンク先" : "リンク先（見出しが見つかりません。名前を変えたか消した）";
                _editTitle.Text = LinkAddress(item);
                _editTitle.IsReadOnly = true;
            }
            else
            {
                _editLabel.Text = "名前（時間もここに書く）";
                _editTitle.Text = item.Title;
                _editTitle.IsReadOnly = false;
            }
            _editJump.Visibility = item.IsLinked ? Visibility.Visible : Visibility.Collapsed;

            // 繰り返し。欄の選び肢で同じ決まりを作れないもの（読めない決まり・Claude が書いた「始まりの日と違う日付の毎月」など）は「そのまま」を選んでおく
            // （選び肢に当てはめて保存すると、決まりが変わってしまうため）
            _editDays.Clear();
            _editInterval.Text = rule != null ? rule.Interval.ToString(CultureInfo.InvariantCulture) : "1";
            _editUntil.Text = rule != null && rule.Until.HasValue ? rule.Until.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
            string tag;
            if (!item.IsRepeat) tag = "none";
            else if (rule == null) tag = "keep";
            else
            {
                var start = item.Start;
                var nth = (start.Day - 1) / 7 + 1;
                switch (rule.Kind)
                {
                    case RepeatKind.Daily: tag = "daily"; break;
                    case RepeatKind.Weekly:
                        _editDays.AddRange(rule.WeekDays.Count > 0 ? rule.WeekDays : new List<DayOfWeek> { start.DayOfWeek });
                        var weekdays = rule.Interval == 1 && _editDays.Count == 5 && !_editDays.Contains(DayOfWeek.Sunday) && !_editDays.Contains(DayOfWeek.Saturday);
                        tag = weekdays ? "weekdays" : "weekly";
                        break;
                    case RepeatKind.MonthlyDay:
                        tag = rule.MonthDay == 0 || rule.MonthDay == start.Day ? "monthday" : "keep";
                        break;
                    case RepeatKind.MonthlyNth:
                        if (rule.NthDay != start.DayOfWeek) tag = "keep";
                        else if (rule.Nth < 0) tag = start.AddDays(7).Month != start.Month ? "monthlast" : "keep";
                        else tag = rule.Nth == nth && nth <= 4 ? "monthnth" : "keep";
                        break;
                    default: tag = "yearly"; break;
                }
            }
            FillRepeatChoices(item, tag == "keep");
            SelectRepeatChoice(tag);
            if (_editDays.Count == 0) _editDays.Add(item.Start.DayOfWeek);
            UpdateRepeatFields();

            // 消す: 繰り返しなら「この回だけ」と「全部」
            _editDelete.Content = item.IsRepeat && original.HasValue ? "この回だけ消す" : "削除";
            _editDeleteAll.Visibility = item.IsRepeat && original.HasValue ? Visibility.Visible : Visibility.Collapsed;

            _editorLayer.Visibility = Visibility.Visible;
            _editorOpen = true;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                _editTitle.Focus();
                if (!_editTitle.IsReadOnly) _editTitle.SelectAll();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>見出しと結んだ予定のリンク先（ファイル › ページ › 見出し。ページが無ければ省く）。</summary>
        private static string LinkAddress(ScheduleItem item)
        {
            var address = item.LinkFile;
            if (!string.IsNullOrEmpty(item.LinkPage)) address += " › " + item.LinkPage;
            return address + " › " + item.LinkHeading;
        }

        private void CloseEditor()
        {
            _editorLayer.Visibility = Visibility.Collapsed;
            _editorOpen = false;
            _editing = null;
            _editingOriginal = null;
            Focus();
        }

        /// <summary>
        /// 欄の繰り返しを、保存先に書く決まりにする。なしなら null。読めない決まりを「そのまま」なら元の決まり。
        /// 終わりの日が読めなければ false（欄は閉じない）。
        /// </summary>
        private bool ReadRepeat(ScheduleItem item, out string repeat, out DateTime start)
        {
            repeat = null;
            start = item.Start;
            var choice = RepeatChoice;
            if (choice == "none") return true;
            if (choice == "keep")
            {
                repeat = item.Repeat;
                return true;
            }

            int interval;
            if (!int.TryParse(_editInterval.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1 || interval > 999) interval = 1;

            DateTime? until = null;
            var untilText = _editUntil.Text.Trim();
            if (untilText.Length > 0)
            {
                DateTime parsed;
                if (!TryReadDate(untilText, start, out parsed))
                {
                    _status("終わりの日を読めませんでした（例 2027-03-31・2027/3/31・3/31）: " + untilText);
                    return false;
                }
                until = parsed;
            }

            var rule = new ScheduleRepeat { Interval = interval, Until = until };
            switch (choice)
            {
                case "daily": rule.Kind = RepeatKind.Daily; break;
                case "weekdays":
                    rule.Kind = RepeatKind.Weekly;
                    rule.Interval = 1;
                    rule.WeekDays = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
                    break;
                case "weekly":
                    rule.Kind = RepeatKind.Weekly;
                    rule.WeekDays = _editDays.Count > 0 ? _editDays.OrderBy(day => day).ToList() : new List<DayOfWeek> { start.DayOfWeek };
                    break;
                case "monthday":
                    rule.Kind = RepeatKind.MonthlyDay;
                    rule.MonthDay = start.Day;
                    break;
                case "monthnth":
                    rule.Kind = RepeatKind.MonthlyNth;
                    rule.Nth = (start.Day - 1) / 7 + 1;
                    rule.NthDay = start.DayOfWeek;
                    break;
                case "monthlast":
                    rule.Kind = RepeatKind.MonthlyNth;
                    rule.Nth = -1;
                    rule.NthDay = start.DayOfWeek;
                    break;
                default:
                    rule.Kind = RepeatKind.Yearly;
                    break;
            }

            // 毎週は1回目の曜日をいつも含む（欄で外せない。PaintWeekDays）
            if (rule.Kind == RepeatKind.Weekly && !rule.WeekDays.Contains(start.DayOfWeek))
            {
                rule.WeekDays.Add(start.DayOfWeek);
                rule.WeekDays.Sort();
            }

            if (until.HasValue && until.Value < start)
            {
                _status("終わりの日が始まりの日（" + start.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + "）より前です");
                return false;
            }
            repeat = rule.ToString();
            return true;
        }

        /// <summary>終わりの日の欄（2027-03-31・2027/3/31・3/31）。年が無ければ、始まりの日以降で最初のその月日。</summary>
        private static bool TryReadDate(string text, DateTime start, out DateTime date)
        {
            var formats = new[] { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
            DateTime monthDay;
            if (DateTime.TryParseExact(text, new[] { "M/d", "M-d", "M.d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out monthDay))
            {
                try
                {
                    date = new DateTime(start.Year, monthDay.Month, monthDay.Day);
                    if (date < start) date = date.AddYears(1);
                    return true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 打ちかけを保存する（カレンダー・画面を閉じる前。MainWindow.Calendar.cs）。マスで打っている名前は足し、欄が開いていれば欄の中身を保存して閉じる。
        /// 終わりの日を読めなければ、欄は開いたまま false（閉じるのをやめる）。
        /// </summary>
        public bool CommitEdit()
        {
            CommitNew();
            if (!_editorOpen) return true;

            var editing = _editing;
            if (editing == null)
            {
                CloseEditor();
                return true;
            }

            string repeat;
            DateTime start;
            if (!ReadRepeat(editing, out repeat, out start)) return false;
            var title = editing.IsLinked ? editing.Title : ScheduleFile.OneLine(_editTitle.Text);   // 結んだ予定の欄に出しているのはリンク先
            CloseEditor();
            if (!Fresh()) return true;

            var target = _items.FirstOrDefault(other => ReferenceEquals(other, editing));
            if (target == null || (target.Title == title && target.Repeat == repeat && target.Start == start)) return true;

            Remember();
            target.Title = title;   // メモは画面では持たないが、保存先に書いてあれば消さずに残す
            if (start != target.Start)
            {
                target.End = target.End.AddDays((start - target.Start).TotalDays);
                target.Start = start;
            }
            if (target.Repeat != repeat)
            {
                target.Repeat = repeat;
                ScheduleRepeat.Prune(target);   // もう回でない日の「消した」「動かした」は捨てる
            }
            _selected = target;
            if (!target.IsRepeat) _selectedOriginal = null;
            Save();
            Rebuild();
            return true;
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
