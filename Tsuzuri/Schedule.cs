using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Markdown;

namespace Tsuzuri
{
    /// <summary>
    /// 予定を知らせる（2026-11-04。SPEC 19章）。
    ///
    /// 予定は exe の隣の schedule.json に AI（AI の欄の Claude）が書く。利用者は「月の初めに〇〇を教えて」「毎日17時に」のように曖昧に頼み、
    /// Claude がそれを下の決まった形（繰り返し・日・時刻）に直して書く。Tsuzuri は30秒ごとに見て、時刻が来た予定を最前面の小窓で知らせる。
    /// 知らせた時刻は schedule-shown.txt に書く（schedule.json は Claude のもので、こちらからは書かない）。
    /// Tsuzuri が止まっていて逃した予定は、次に起動したときに1回だけ知らせる。
    /// </summary>
    public static class Schedule
    {
        public static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schedule.json");
        private static readonly string ShownPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schedule-shown.txt");

        private const string Stamp = "yyyy-MM-dd'T'HH:mm";

        private static DispatcherTimer _timer;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static List<ScheduleItem> _items = new List<ScheduleItem>();
        private static string _error;
        private static AlertWindow _alert;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timer.Tick += delegate { Check(); };
            _timer.Start();
            Check();
        }

        private static void Check()
        {
            try
            {
                Reload();

                var now = DateTime.Now;
                var shown = ReadShown();
                var due = new List<string>();
                var changed = false;

                foreach (var item in _items)
                {
                    if (string.IsNullOrEmpty(item.Id)) continue;

                    DateTime last;
                    var hasLast = shown.TryGetValue(item.Id, out last);
                    var created = Parse(item.Created);

                    // いつ登録したかが分からず、まだ一度も知らせていない予定は、今を起点にする（過去の分をまとめて鳴らさない）。
                    if (!hasLast && created == null)
                    {
                        shown[item.Id] = Minute(now);
                        changed = true;
                        continue;
                    }

                    var latest = item.Latest(now);
                    if (latest == null) continue;

                    var since = hasLast ? last : created.Value;
                    if (latest.Value <= since) continue;

                    shown[item.Id] = latest.Value;
                    changed = true;
                    due.Add(Describe(item, latest.Value, now));
                }

                // 消えた予定の覚えは捨てる。
                foreach (var id in shown.Keys.ToList())
                {
                    if (_items.All(item => item.Id != id))
                    {
                        shown.Remove(id);
                        changed = true;
                    }
                }

                if (changed) WriteShown(shown);
                if (due.Count > 0) Alert(due);
            }
            catch (Exception ex)
            {
                ReportError("予定を確かめられませんでした: " + ex.Message);
            }
        }

        private static void Reload()
        {
            if (!File.Exists(FilePath))
            {
                _items = new List<ScheduleItem>();
                _loadedAt = DateTime.MinValue;
                return;
            }

            var written = File.GetLastWriteTime(FilePath);
            if (written == _loadedAt) return;

            try
            {
                using (var stream = new MemoryStream(File.ReadAllBytes(FilePath)))
                {
                    var file = new DataContractJsonSerializer(typeof(ScheduleFile)).ReadObject(stream) as ScheduleFile;
                    _items = file?.Items?.Where(item => item != null).ToList() ?? new List<ScheduleItem>();
                }

                _loadedAt = written;
                _error = null;
            }
            catch (Exception ex)
            {
                // Claude が書いている途中かもしれない。次の見回りで読み直す（同じ誤りは1回だけ知らせる）。
                if (DateTime.Now - written < TimeSpan.FromSeconds(10)) return;
                _loadedAt = written;
                ReportError("schedule.json を読めませんでした（AI の欄で直してもらってください）: " + ex.Message);
            }
        }

        private static void ReportError(string message)
        {
            if (_error == message) return;
            _error = message;
            Alert(new List<string> { message });
        }

        private static string Describe(ScheduleItem item, DateTime when, DateTime now)
        {
            var text = string.IsNullOrWhiteSpace(item.Text) ? item.Said ?? item.Id : item.Text;
            var late = now - when > TimeSpan.FromMinutes(5)
                ? "（" + when.ToString("M/d H:mm", CultureInfo.InvariantCulture) + " の分）"
                : string.Empty;
            return text + late;
        }

        private static void Alert(List<string> lines)
        {
            SystemSounds.Asterisk.Play();

            if (_alert == null || !_alert.IsLoaded)
            {
                _alert = new AlertWindow();
                _alert.Closed += delegate { _alert = null; };
                _alert.Show();
            }

            foreach (var line in lines) _alert.Add(line);
            _alert.Topmost = true;
            _alert.Activate();
        }

        private static Dictionary<string, DateTime> ReadShown()
        {
            var shown = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            if (!File.Exists(ShownPath)) return shown;

            foreach (var line in File.ReadAllLines(ShownPath, Encoding.UTF8))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var when = Parse(line.Substring(tab + 1));
                if (when != null) shown[line.Substring(0, tab)] = when.Value;
            }

            return shown;
        }

        private static void WriteShown(Dictionary<string, DateTime> shown)
        {
            File.WriteAllLines(ShownPath,
                shown.Select(pair => pair.Key + "\t" + pair.Value.ToString(Stamp, CultureInfo.InvariantCulture)),
                new UTF8Encoding(false));
        }

        internal static DateTime? Parse(string text)
        {
            DateTime value;
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var format in new[] { Stamp, "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" })
            {
                if (DateTime.TryParseExact(text.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return value;
            }

            return null;
        }

        private static DateTime Minute(DateTime value)
        {
            return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0);
        }

        /// <summary>最前面に出す知らせの小窓。続けて来た知らせは同じ窓に足す。</summary>
        private sealed class AlertWindow : Window
        {
            private readonly StackPanel _list = new StackPanel();

            public AlertWindow()
            {
                Title = "Tsuzuri の予定";
                Width = 380;
                SizeToContent = SizeToContent.Height;
                MaxHeight = 600;
                ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Topmost = true;
                ShowInTaskbar = true;
                Background = Theme.Background;

                var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
                ok.Click += delegate { Close(); };

                var root = new StackPanel { Margin = new Thickness(16) };
                root.Children.Add(new TextBlock { Text = "予定の時刻です", Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 8) });
                root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 });
                root.Children.Add(ok);
                Content = root;
            }

            public void Add(string line)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = DateTime.Now.ToString("H:mm", CultureInfo.InvariantCulture) + "  " + line,
                    Foreground = Theme.Text,
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }
    }

    [DataContract]
    public sealed class ScheduleFile
    {
        [DataMember(Name = "items")] public List<ScheduleItem> Items { get; set; }
    }

    /// <summary>
    /// 予定1つ。repeat が once（1回）・daily（毎日）・weekly（毎週 weekdays の曜日）・monthly（毎月 day 日、または第 nth の weekdays 曜日）・yearly（毎年 date の月日）。
    /// day が -1 なら月末、nth が -1 なら最後の週。time が無ければ 9:00。until を過ぎたら知らせない。
    /// </summary>
    [DataContract]
    public sealed class ScheduleItem
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "text")] public string Text { get; set; }
        [DataMember(Name = "said")] public string Said { get; set; }
        [DataMember(Name = "created")] public string Created { get; set; }
        [DataMember(Name = "repeat")] public string Repeat { get; set; }
        [DataMember(Name = "date")] public string Date { get; set; }
        [DataMember(Name = "time")] public string Time { get; set; }
        [DataMember(Name = "weekdays")] public int[] Weekdays { get; set; }
        [DataMember(Name = "day")] public int Day { get; set; }
        [DataMember(Name = "nth")] public int Nth { get; set; }
        [DataMember(Name = "until")] public string Until { get; set; }

        /// <summary>now かそれより前で、いちばん新しい知らせる時刻。無ければ null。</summary>
        public DateTime? Latest(DateTime now)
        {
            var time = ParseTime(Time);
            var date = Schedule.Parse(Date);
            var until = Schedule.Parse(Until);

            for (var back = 0; back <= 400; back++)
            {
                var day = now.Date.AddDays(-back);
                if (until != null && day > until.Value.Date) continue;
                if (!Matches(day, date)) continue;

                var when = day + time;
                if (when <= now) return when;
            }

            return null;
        }

        private bool Matches(DateTime day, DateTime? date)
        {
            switch ((Repeat ?? "once").ToLowerInvariant())
            {
                case "daily":
                    return true;
                case "weekly":
                    return Weekdays != null && Weekdays.Contains((int)day.DayOfWeek);
                case "monthly":
                    if (Nth != 0 && Weekdays != null && Weekdays.Length > 0)
                    {
                        if (!Weekdays.Contains((int)day.DayOfWeek)) return false;
                        return Nth > 0 ? (day.Day - 1) / 7 + 1 == Nth : day.AddDays(7).Month != day.Month;
                    }
                    var last = DateTime.DaysInMonth(day.Year, day.Month);
                    var target = Day == -1 || Day > last ? last : Day;
                    return day.Day == target;
                case "yearly":
                    return date != null && day.Month == date.Value.Month && day.Day == Math.Min(date.Value.Day, DateTime.DaysInMonth(day.Year, day.Month));
                default:
                    return date != null && day == date.Value.Date;
            }
        }

        private static TimeSpan ParseTime(string text)
        {
            TimeSpan value;
            if (!string.IsNullOrWhiteSpace(text) && TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out value)) return value;
            return TimeSpan.FromHours(9);
        }
    }
}
