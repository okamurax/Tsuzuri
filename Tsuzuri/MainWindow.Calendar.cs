using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        /// <summary>予定の保存先。app.json の scheduleFile。無ければ app.json の隣の 予定.md。</summary>
        private string ScheduleFilePath
        {
            get
            {
                var path = _settings.ScheduleFile;
                if (!string.IsNullOrWhiteSpace(path)) return Environment.ExpandEnvironmentVariables(path.Trim());
                return Path.Combine(Path.GetDirectoryName(AppSettings.StorePath) ?? ".", "予定.md");
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
            _calendar = new CalendarView(ScheduleFilePath, Status, ScheduleLinkFound, OpenScheduleLink,
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

        /// <summary>カレンダーを閉じる。マスで打っている名前・欄で直している中身は、先に保存する（打った文字を捨てない）。閉じたら true。</summary>
        private bool CloseCalendar()
        {
            if (_calendar == null) return true;
            if (!_calendar.CommitEdit())
            {
                Status("予定の欄に直すところがあります。直して保存するか、「やめる」を押してください");
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
        /// 結んだ見出しが、その文書のどこか（どのページでも。ブックマークと同じ探し方）にあるか。
        /// どこかの画面で開いていれば、その中身（保存していない編集も）で見る。閉じていればファイルを読む（更新日時で覚える）。
        /// </summary>
        private bool ScheduleLinkFound(ScheduleItem item)
        {
            foreach (var window in OpenWindows)
            {
                var open = window.FindOpenDocument(item.LinkFile);
                if (open != null) return HeadingsOf(open).Contains(item.LinkHeading);
            }

            if (!File.Exists(item.LinkFile)) return false;
            var stamp = File.GetLastWriteTimeUtc(item.LinkFile);
            KeyValuePair<DateTime, HashSet<string>> cached;
            if (!HeadingCache.TryGetValue(item.LinkFile, out cached) || cached.Key != stamp)
            {
                cached = new KeyValuePair<DateTime, HashSet<string>>(stamp, HeadingsOf(PageDocument.Load(item.LinkFile)));
                HeadingCache[item.LinkFile] = cached;
            }
            return cached.Value.Contains(item.LinkHeading);
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
