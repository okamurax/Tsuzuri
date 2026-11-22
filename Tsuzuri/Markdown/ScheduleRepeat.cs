using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tsuzuri.Markdown
{
    /// <summary>カレンダーに出す予定の1回分（繰り返しの予定なら、その回）。</summary>
    public sealed class ScheduleOccurrence
    {
        public ScheduleItem Item;

        /// <summary>繰り返しの予定なら、決まりどおりの日（動かした回でも元の日）。繰り返さない予定は null。</summary>
        public DateTime? Original;

        public DateTime Start;
        public DateTime End;

        public int Days
        {
            get { return (int)(End - Start).TotalDays + 1; }
        }

        public bool IsRepeat
        {
            get { return Original.HasValue; }
        }
    }

    /// <summary>通知を出す1回分（ScheduleRepeat.Alarms）。</summary>
    public sealed class ScheduleAlarm
    {
        public ScheduleOccurrence Occurrence;

        /// <summary>鳴らす日時（回の始まりの日＋通知の時刻）。</summary>
        public DateTime At;
    }

    public enum RepeatKind { Daily, Weekly, MonthlyDay, MonthlyNth, Yearly }

    /// <summary>
    /// 繰り返しの決まり（SPEC 22章。2026-11-21）。保存先には Google カレンダーと同じ RRULE の形で書く（あとで Google へ上げるときにそのまま渡せるように）。
    /// 読めるのは、Tsuzuri の欄で作れる形だけ:
    ///   FREQ=DAILY[;INTERVAL=n]
    ///   FREQ=WEEKLY[;INTERVAL=n];BYDAY=MO,TU…[;WKST=SU]        平日は BYDAY=MO,TU,WE,TH,FR
    ///   FREQ=MONTHLY[;INTERVAL=n][;BYMONTHDAY=d]                日付（無ければ始まりの日の日付）。その日が無い月は飛ばす（Google と同じ）
    ///   FREQ=MONTHLY[;INTERVAL=n];BYDAY=2TU / -1FR              第n曜日・最終曜日
    ///   FREQ=YEARLY[;INTERVAL=n]                                始まりの日の月日（2/29 は、うるう年だけ）
    ///   どれにも [;UNTIL=yyyyMMdd] を付けられる（その日を含む）。COUNT などほかの項目があれば読めない（null。始まりの日だけを出す）。
    /// 始まりの日（予定の Start）はいつも1回目に数える。
    /// </summary>
    public sealed class ScheduleRepeat
    {
        public RepeatKind Kind;
        public int Interval = 1;

        /// <summary>毎週の曜日。</summary>
        public List<DayOfWeek> WeekDays = new List<DayOfWeek>();

        /// <summary>毎月の日付（1〜31）。0 なら始まりの日の日付。</summary>
        public int MonthDay;

        /// <summary>毎月の第n曜日（1〜5。-1 は最終）と、その曜日。</summary>
        public int Nth;
        public DayOfWeek NthDay;

        /// <summary>終わりの日（その日を含む）。null なら無期限。</summary>
        public DateTime? Until;

        private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
        private static readonly string[] DayNames = { "日", "月", "火", "水", "木", "金", "土" };

        public static ScheduleRepeat Parse(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return null;

            var parts = new Dictionary<string, string>();
            foreach (var piece in rule.Trim().Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = piece.Split('=');
                var key = pair.Length == 2 ? pair[0].Trim().ToUpperInvariant() : null;
                if (key == null || parts.ContainsKey(key)) return null;
                parts[key] = pair[1].Trim().ToUpperInvariant();
            }

            string freq;
            if (!parts.TryGetValue("FREQ", out freq)) return null;
            var repeat = new ScheduleRepeat();

            string value;
            if (parts.TryGetValue("INTERVAL", out value))
            {
                int interval;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1 || interval > 999) return null;
                repeat.Interval = interval;
            }
            if (parts.TryGetValue("UNTIL", out value))
            {
                DateTime until;
                var date = value.Length >= 8 ? value.Substring(0, 8) : value;
                if (!DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out until)) return null;
                repeat.Until = until;
            }

            string byDay, byMonthDay;
            parts.TryGetValue("BYDAY", out byDay);
            parts.TryGetValue("BYMONTHDAY", out byMonthDay);

            // 読める項目だけか
            foreach (var key in parts.Keys)
            {
                if (key == "FREQ" || key == "INTERVAL" || key == "UNTIL" || key == "WKST") continue;
                if (key == "BYDAY" && (freq == "WEEKLY" || freq == "MONTHLY")) continue;
                if (key == "BYMONTHDAY" && freq == "MONTHLY") continue;
                return null;
            }

            switch (freq)
            {
                case "DAILY":
                    repeat.Kind = RepeatKind.Daily;
                    break;
                case "WEEKLY":
                    repeat.Kind = RepeatKind.Weekly;
                    if (byDay != null)
                    {
                        foreach (var code in byDay.Split(','))
                        {
                            var index = Array.IndexOf(DayCodes, code.Trim());
                            if (index < 0) return null;
                            if (!repeat.WeekDays.Contains((DayOfWeek)index)) repeat.WeekDays.Add((DayOfWeek)index);
                        }
                        repeat.WeekDays.Sort();
                    }
                    break;
                case "MONTHLY":
                    if (byDay != null && byMonthDay != null) return null;
                    if (byDay != null)
                    {
                        // 2TU・-1FR（1つだけ）
                        if (byDay.Length < 3 || byDay.Contains(",")) return null;
                        var index = Array.IndexOf(DayCodes, byDay.Substring(byDay.Length - 2));
                        int nth;
                        if (index < 0 || !int.TryParse(byDay.Substring(0, byDay.Length - 2), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nth)
                            || !(nth == -1 || (nth >= 1 && nth <= 5))) return null;
                        repeat.Kind = RepeatKind.MonthlyNth;
                        repeat.Nth = nth;
                        repeat.NthDay = (DayOfWeek)index;
                    }
                    else
                    {
                        repeat.Kind = RepeatKind.MonthlyDay;
                        if (byMonthDay != null)
                        {
                            int day;
                            if (!int.TryParse(byMonthDay, NumberStyles.None, CultureInfo.InvariantCulture, out day) || day < 1 || day > 31) return null;
                            repeat.MonthDay = day;
                        }
                    }
                    break;
                case "YEARLY":
                    repeat.Kind = RepeatKind.Yearly;
                    break;
                default:
                    return null;
            }
            return repeat;
        }

        /// <summary>保存先に書く形（RRULE）。</summary>
        public override string ToString()
        {
            var parts = new List<string>();
            switch (Kind)
            {
                case RepeatKind.Daily: parts.Add("FREQ=DAILY"); break;
                case RepeatKind.Weekly: parts.Add("FREQ=WEEKLY"); break;
                case RepeatKind.MonthlyDay:
                case RepeatKind.MonthlyNth: parts.Add("FREQ=MONTHLY"); break;
                case RepeatKind.Yearly: parts.Add("FREQ=YEARLY"); break;
            }
            if (Interval > 1) parts.Add("INTERVAL=" + Interval.ToString(CultureInfo.InvariantCulture));
            if (Kind == RepeatKind.Weekly && WeekDays.Count > 0)
                parts.Add("BYDAY=" + string.Join(",", WeekDays.OrderBy(day => day).Select(day => DayCodes[(int)day])));
            if (Kind == RepeatKind.Weekly && Interval > 1) parts.Add("WKST=SU");   // 何週ごとかを日曜始まりの週で数える（Tsuzuri の週と同じ）
            if (Kind == RepeatKind.MonthlyDay && MonthDay > 0) parts.Add("BYMONTHDAY=" + MonthDay.ToString(CultureInfo.InvariantCulture));
            if (Kind == RepeatKind.MonthlyNth) parts.Add("BYDAY=" + Nth.ToString(CultureInfo.InvariantCulture) + DayCodes[(int)NthDay]);
            if (Until.HasValue) parts.Add("UNTIL=" + Until.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            return string.Join(";", parts);
        }

        private bool IsWeekdays
        {
            get
            {
                return Kind == RepeatKind.Weekly && Interval == 1 && WeekDays.Count == 5
                    && !WeekDays.Contains(DayOfWeek.Sunday) && !WeekDays.Contains(DayOfWeek.Saturday);
            }
        }

        /// <summary>画面に出す説明（「毎週 火・木」「毎月 第2火曜」「2か月ごと 25日」…）。終わりの日があれば添える。</summary>
        public string Describe(DateTime start)
        {
            string text;
            switch (Kind)
            {
                case RepeatKind.Daily:
                    text = Interval > 1 ? Interval + "日ごと" : "毎日";
                    break;
                case RepeatKind.Weekly:
                    if (IsWeekdays) text = "平日（月〜金）";
                    else
                    {
                        var days = WeekDays.Count > 0 ? WeekDays : new List<DayOfWeek> { start.DayOfWeek };
                        text = (Interval > 1 ? Interval + "週ごと " : "毎週 ") + string.Join("・", days.OrderBy(day => day).Select(day => DayNames[(int)day]));
                    }
                    break;
                case RepeatKind.MonthlyDay:
                    text = (Interval > 1 ? Interval + "か月ごと " : "毎月 ") + (MonthDay > 0 ? MonthDay : start.Day) + "日";
                    break;
                case RepeatKind.MonthlyNth:
                    text = (Interval > 1 ? Interval + "か月ごと " : "毎月 ") + (Nth < 0 ? "最終" : "第" + Nth) + DayNames[(int)NthDay] + "曜";
                    break;
                default:
                    text = (Interval > 1 ? Interval + "年ごと " : "毎年 ") + start.Month + "月" + start.Day + "日";
                    break;
            }
            if (Until.HasValue) text += "（" + Until.Value.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + " まで）";
            return text;
        }

        private static DateTime WeekStart(DateTime date)
        {
            return date.Date.AddDays(-(int)date.DayOfWeek);
        }

        /// <summary>その日が、start から始まるこの決まりの回か（終わりの日も見る。動かした・消した回は見ない）。</summary>
        public bool Matches(DateTime start, DateTime date)
        {
            if (date < start) return false;
            if (Until.HasValue && date > Until.Value) return false;
            if (date == start) return true;   // 始まりの日はいつも1回目

            switch (Kind)
            {
                case RepeatKind.Daily:
                    return (int)(date - start).TotalDays % Interval == 0;
                case RepeatKind.Weekly:
                {
                    var weeks = (int)(WeekStart(date) - WeekStart(start)).TotalDays / 7;
                    if (weeks % Interval != 0) return false;
                    return WeekDays.Count > 0 ? WeekDays.Contains(date.DayOfWeek) : date.DayOfWeek == start.DayOfWeek;
                }
                case RepeatKind.MonthlyDay:
                case RepeatKind.MonthlyNth:
                {
                    var months = (date.Year - start.Year) * 12 + date.Month - start.Month;
                    if (months % Interval != 0) return false;
                    if (Kind == RepeatKind.MonthlyDay) return date.Day == (MonthDay > 0 ? MonthDay : start.Day);
                    if (date.DayOfWeek != NthDay) return false;
                    return Nth < 0 ? date.AddDays(7).Month != date.Month : (date.Day - 1) / 7 + 1 == Nth;
                }
                default:
                    return (date.Year - start.Year) % Interval == 0 && date.Month == start.Month && date.Day == start.Day;
            }
        }

        /// <summary>
        /// from〜to（どちらも含む）に掛かる回。繰り返さない予定は、掛かれば1回（Original は null）。
        /// 消した回は出さず、動かした回は動かした先に出す。決まりが読めなければ、始まりの日の1回だけ。
        /// </summary>
        public static List<ScheduleOccurrence> Occurrences(ScheduleItem item, DateTime from, DateTime to)
        {
            var list = new List<ScheduleOccurrence>();
            var length = (int)(item.End - item.Start).TotalDays;
            var rule = Parse(item.Repeat);
            if (rule == null)
            {
                if (item.Start <= to && item.End >= from)
                    list.Add(new ScheduleOccurrence { Item = item, Start = item.Start, End = item.End });
                return list;
            }

            var first = from.AddDays(-length) > item.Start ? from.AddDays(-length) : item.Start;
            var last = rule.Until.HasValue && rule.Until.Value < to ? rule.Until.Value : to;
            for (var date = first; date <= last; date = date.AddDays(1))
            {
                if (!rule.Matches(item.Start, date) || item.Skips.Contains(date) || item.Moves.ContainsKey(date)) continue;
                list.Add(new ScheduleOccurrence { Item = item, Original = date, Start = date, End = date.AddDays(length) });
            }

            foreach (var move in item.Moves)
            {
                var start = move.Value;
                if (start > to || start.AddDays(length) < from) continue;
                if (!rule.Matches(item.Start, move.Key) || item.Skips.Contains(move.Key)) continue;
                list.Add(new ScheduleOccurrence { Item = item, Original = move.Key, Start = start, End = start.AddDays(length) });
            }
            return list;
        }

        /// <summary>
        /// 通知を出す時刻が after より後で until まで（after は含まず until は含む）の、予定の回（2026-11-22。SPEC 22章「通知の時刻」）。
        /// 鳴るのは回の始まりの日の、その予定の通知の時刻（何日かの予定は始まりの日だけ・消した回は無し・動かした回は動かした先の日）。時刻の順。
        /// </summary>
        public static List<ScheduleAlarm> Alarms(IEnumerable<ScheduleItem> items, DateTime after, DateTime until)
        {
            var list = new List<ScheduleAlarm>();
            if (items == null || until <= after) return list;
            foreach (var item in items)
            {
                if (item == null || !item.Notify.HasValue) continue;
                var time = item.Notify.Value;
                foreach (var occurrence in Occurrences(item, after.Date, until.Date))
                {
                    var at = occurrence.Start.Date + time;
                    if (at > after && at <= until) list.Add(new ScheduleAlarm { Occurrence = occurrence, At = at });
                }
            }
            return list.OrderBy(alarm => alarm.At).ToList();
        }

        /// <summary>決まりが変わったとき、もう回でない日の「消した」「動かした」を捨てる。</summary>
        public static void Prune(ScheduleItem item)
        {
            var rule = Parse(item.Repeat);
            if (rule == null)
            {
                item.Skips.Clear();
                item.Moves.Clear();
                return;
            }
            item.Skips.RemoveAll(date => !rule.Matches(item.Start, date));
            foreach (var key in item.Moves.Keys.Where(date => !rule.Matches(item.Start, date)).ToList()) item.Moves.Remove(key);
        }
    }
}
