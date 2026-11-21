using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: カレンダー（SPEC 22章。2026-11-20）。
    // 予定を週の行で下へずっと並べ、カレンダーの画面で直接打つ。保存先は .md（ScheduleFile）で、裏に置くだけ（利用者の考え）。
    // ランチャーと同じく、本文の場所いっぱいに重ねて出し、画面のタブの並びの一番右（ランチャーの右）のタブで出し入れする。
    // ランチャーとは同時に出さない（片方を出すと、もう片方を閉じる）。
    // アウトラインの見出しをカレンダーの日へドラッグすると、その見出しと結んだ予定になる（2026-11-20）。結ぶのは見出しの文字で、名前が変わっても追わない。
    public partial class MainWindow
    {
        private CalendarView _calendar;
        private Button _calendarButton;

        /// <summary>
        /// 予定の保存先。app.json の scheduleFile。無ければ app.json の隣の schedule.md。
        /// 人が直接開かないファイルなので、名前に日本語を使わない（2026-11-21。前は 予定.md。前の名前からの移し替えは持たない）。
        /// </summary>
        private string ScheduleFilePath
        {
            get
            {
                var path = _settings.ScheduleFile;
                if (!string.IsNullOrWhiteSpace(path)) return Environment.ExpandEnvironmentVariables(path.Trim());
                return Path.Combine(Path.GetDirectoryName(AppSettings.StorePath) ?? ".", "schedule.md");
            }
        }

        /// <summary>「カレンダー」のタブ。見た目はランチャーのタブと同じ（App.xaml の LauncherTabButton）。Alt+C でも。</summary>
        private Button CalendarButton
        {
            get
            {
                if (_calendarButton != null) return _calendarButton;
                _calendarButton = new Button
                {
                    Content = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    Style = (Style)FindResource("LauncherTabButton"),
                    Width = 34,
                    Height = 26,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "カレンダー（Alt+C）。予定を週の行で下へずっと並べ、ここで直接打つ。もう一度押すと閉じる（ほかを押しても閉じない）",
                };
                _calendarButton.Click += delegate { ToggleCalendar(); };
                AccessKeyManager.Register("C", _calendarButton);
                return _calendarButton;
            }
        }

        private void ToggleCalendar()
        {
            if (_calendar != null)
            {
                CloseCalendar();
                return;
            }

            CloseLauncher();
            var heights = ReadWeekHeights();
            _calendar = new CalendarView(ScheduleFilePath, Status, ScheduleHeadings, OpenScheduleLink,
                                         heights, delegate { WriteWeekHeights(heights); });
            CalendarHost.Child = _calendar;
            CalendarHost.Visibility = Visibility.Visible;
            CalendarButton.Tag = "Open";

            // 覆われた本文に打ち込まないよう、キー入力をカレンダーへ移す（ランチャーと同じ）
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_calendar != null && !CalendarHost.IsKeyboardFocusWithin) _calendar.Focus();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>高さを広げた週（app.json の calendarWeekHeights。「yyyy-MM-dd=高さ」）。読めない項目は捨てる。</summary>
        private Dictionary<DateTime, double> ReadWeekHeights()
        {
            var heights = new Dictionary<DateTime, double>();
            foreach (var entry in _settings.CalendarWeekHeights ?? new List<string>())
            {
                var parts = (entry ?? string.Empty).Split('=');
                DateTime week;
                double height;
                if (parts.Length == 2
                    && DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out week)
                    && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out height)
                    && height > 0 && height < 10000)
                    heights[week.Date] = height;
            }
            return heights;
        }

        private void WriteWeekHeights(Dictionary<DateTime, double> heights)
        {
            var list = heights.OrderBy(pair => pair.Key)
                              .Select(pair => pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "="
                                              + Math.Round(pair.Value).ToString(CultureInfo.InvariantCulture))
                              .ToList();
            _settings.CalendarWeekHeights = list.Count > 0 ? list : null;
            _settings.Save();
        }

        /// <summary>
        /// AI に渡す今の状態に、カレンダーを足す（2026-11-21。利用者と日付の話をするため）。開いていなければ何も書かない。
        /// 見えている週・選んでいる日・選んでいる予定。保存先や結んだ文書が AI に渡さない場所なら、その中身は書かない。
        /// </summary>
        private void AppendCalendarContext(StringBuilder text)
        {
            var calendar = _calendar;
            if (calendar == null) return;

            if (AiGuard.IsGuarded(calendar.FilePath))
            {
                text.Append("カレンダー: 開いている（予定の保存先は、利用者が AI に渡さないと決めた場所にある。中身を尋ねたり探したりしない）\n");
                return;
            }

            DateTime first, last;
            calendar.VisibleRange(out first, out last);
            text.Append("カレンダー: 開いている（予定の保存先: ").Append(calendar.FilePath).Append("）\n");
            text.Append("  見えている週: ").Append(Day(first)).Append("〜").Append(Day(last)).Append('\n');

            var from = calendar.SelectedFrom;
            var to = calendar.SelectedTo;
            if (from.HasValue && to.HasValue)
            {
                text.Append("  選んでいる日: ").Append(Day(from.Value));
                if (to.Value > from.Value) text.Append("〜").Append(Day(to.Value)).Append("（").Append((int)(to.Value - from.Value).TotalDays + 1).Append(" 日）");
                text.Append('\n');
            }

            var item = calendar.SelectedItem;
            var dates = calendar.SelectedDates;
            if (item != null && dates != null)
            {
                // 繰り返しの予定は、選んだ回の日と、決まり（保存先の repeat の行と同じ RRULE）
                text.Append("  選んでいる予定: ").Append(Day(dates.Item1));
                if (dates.Item2 > dates.Item1) text.Append("〜").Append(Day(dates.Item2));
                if (item.IsRepeat)
                {
                    var rule = ScheduleRepeat.Parse(item.Repeat);
                    text.Append("（繰り返しの1回。").Append(rule != null ? rule.Describe(item.Start) : item.Repeat)
                        .Append("、").Append(Day(item.Start)).Append(" から。repeat: ").Append(item.Repeat).Append('）');
                }
                if (item.IsLinked && AiGuard.IsGuarded(item.LinkFile))
                    text.Append("（AI に渡さない場所の文書の見出しと結んだ予定。名前は伏せた）\n");
                else
                {
                    text.Append(" ").Append(item.Title);
                    if (item.IsLinked) text.Append("（文書の見出しと結んだ予定: ").Append(item.LinkFile).Append(" の「").Append(item.LinkHeading).Append("」）");
                    text.Append(item.Memo.Trim().Length > 0 ? "（メモあり。保存先を読む）" : string.Empty).Append('\n');
                }
            }

            if (!(from.HasValue) && item == null) text.Append("  選んでいる日・予定: なし\n");
        }

        /// <summary>AI に渡す日付（2026-11-21（土））。</summary>
        private static string Day(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "（" + CultureInfo.GetCultureInfo("ja-JP").DateTimeFormat.GetShortestDayName(date.DayOfWeek) + "）";
        }

        /// <summary>カレンダーを閉じる。マスで打っている名前・欄で直している中身は、先に保存する（打った文字を捨てない）。閉じたら true。</summary>
        private bool CloseCalendar()
        {
            if (_calendar == null) return true;
            if (!_calendar.CommitEdit())
            {
                Status("予定の欄に直すところがあります。直して保存するか、「キャンセル」を押してください");
                return false;
            }

            var hadFocus = CalendarHost.IsKeyboardFocusWithin;
            _calendar = null;
            CalendarHost.Child = null;
            CalendarHost.Visibility = Visibility.Collapsed;
            CalendarButton.Tag = null;
            if (hadFocus && Active != null) Active.TextArea.Focus();
            return true;
        }

        // ---------------------------------------------------------------
        // アウトラインの見出しと結んだ予定
        // ---------------------------------------------------------------

        /// <summary>閉じている文書の見出しの文字（ファイルの更新日時で覚える。全部の画面で共有）。カレンダーを組み直すたびに読み直さないため。</summary>
        private static readonly Dictionary<string, KeyValuePair<DateTime, HashSet<string>>> HeadingCache =
            new Dictionary<string, KeyValuePair<DateTime, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 結んだ文書の見出しの文字（どのページでも。ブックマークと同じ探し方）。文書が無ければ null。カレンダーが組み直すたびに、文書ごとに1回呼ぶ。
        /// どこかの画面で開いていれば、その中身（保存していない編集も）で見る。閉じていればファイルを読む（更新日時で覚える）。
        /// </summary>
        private HashSet<string> ScheduleHeadings(string file)
        {
            foreach (var window in OpenWindows)
            {
                var open = window.FindOpenDocument(file);
                if (open != null) return HeadingsOf(open);
            }

            if (!File.Exists(file)) return null;
            var stamp = File.GetLastWriteTimeUtc(file);
            KeyValuePair<DateTime, HashSet<string>> cached;
            if (!HeadingCache.TryGetValue(file, out cached) || cached.Key != stamp)
            {
                cached = new KeyValuePair<DateTime, HashSet<string>>(stamp, HeadingsOf(PageDocument.Load(file)));
                HeadingCache[file] = cached;
            }
            return cached.Value;
        }

        private static HashSet<string> HeadingsOf(PageDocument document)
        {
            var headings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in document.Pages)
                foreach (var heading in MarkdownParser.Headings(document.TextOf(page.Id))) headings.Add(heading.Text);
            return headings;
        }

        /// <summary>結んだ見出しへ飛ぶ。カレンダーを閉じ、文書を出して（ほかの画面で開いていればその画面を前に出し、閉じていれば開く）、見出しへ移る。</summary>
        private void OpenScheduleLink(ScheduleItem item)
        {
            if (!File.Exists(item.LinkFile))
            {
                Status("文書が見つかりません: " + item.LinkFile);
                return;
            }
            if (!CloseCalendar()) return;

            var mark = new HeadingBookmark { File = item.LinkFile, Page = item.LinkPage ?? string.Empty, Text = item.LinkHeading };
            OpenFileThen(item.LinkFile, (window, document) =>
            {
                mark.File = document.FilePath;
                window.OpenHeadingBookmark(mark);
            });
        }

        /// <summary>アウトラインの見出しをつかんで動かしている（MainWindow.OutlineDrag.cs）。カレンダーの日の上なら、その日を塗って true。</summary>
        private bool ShowCalendarDrop()
        {
            return _calendar != null && _calendar.ShowDrop(Mouse.GetPosition(_calendar));
        }

        /// <summary>
        /// アウトラインの見出しを離した。カレンダーの日の上なら、その見出しと結んだ予定を足して true（節は動かさない）。
        /// 保存していない文書（ファイルが無い）の見出しは結べないので断る。
        /// </summary>
        private bool DropOnCalendar(string heading, bool released)
        {
            if (_calendar == null) return false;
            if (!released || !_calendar.ShowDrop(Mouse.GetPosition(_calendar)))
            {
                _calendar.ClearDrop();
                return false;
            }

            var owner = _outlineOwner ?? Active;
            if (owner == null || string.IsNullOrEmpty(owner.FilePath) || !File.Exists(owner.FilePath))
            {
                _calendar.ClearDrop();
                Status("保存していない文書の見出しは、予定にできません。先に保存してください");
                return true;
            }

            _calendar.DropHeading(owner.FilePath, owner.PageId ?? string.Empty, heading);
            return true;
        }
    }
}
