using System;
using System.Collections.Generic;
using System.Linq;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 外のカレンダーへ送る予定1つ（送り先に依らない形。SPEC 22章「Google へ送る」。2026-11-23）。
    /// Tsuzuri の予定から ScheduleExport.Build で作り、送り先の口（Google など）が自分の形に直して送る。
    /// </summary>
    public sealed class ExportEvent
    {
        public string Title = string.Empty;
        public string Description = string.Empty;

        /// <summary>終日の予定か。false なら時刻付き（通知のある予定。終日の予定では、その日の時刻に知らせられないため）。</summary>
        public bool AllDay;

        /// <summary>始まり。終日なら日付（時刻は 0:00）、時刻付きなら現地の日時。</summary>
        public DateTime Start;

        /// <summary>終わり（含まない）。終日なら最後の日の翌日、時刻付きなら現地の日時。</summary>
        public DateTime End;

        /// <summary>繰り返しの決まり（Until は日付で、その日を含む）。繰り返さなければ null。</summary>
        public ScheduleRepeat Rule;

        /// <summary>繰り返しのうち出さない回の始まり（Start と同じ形。消した回と、動かした回の元の日）。</summary>
        public List<DateTime> Excluded = new List<DateTime>();

        /// <summary>始まりの時刻に知らせる（通知のある予定）。</summary>
        public bool Remind;
    }

    /// <summary>
    /// Tsuzuri の予定を、外のカレンダーへ送る形にする（SPEC 22章「Google へ送る」。2026-11-23）。
    /// 送り先は「今日以降に掛かる予定を全部消して、全部入れ直す」ので、ここでも今日以降に掛かる予定だけを出す（過ぎた予定はさわらない）:
    ///   繰り返さない予定      終わりの日が今日以降
    ///   繰り返しの予定        終わりの日（UNTIL）が無いか、今日以降に掛かる回がある（消した回も数える。送り先も決まりで決めるため）。1件で送る
    ///   動かした回            決まりから外し（Excluded）、動かした先に繰り返さない1件として。動かした先の終わりが今日以降のときだけ
    ///   読めない決まり        カレンダーと同じく1回目だけ（繰り返さない予定として）
    /// 通知のある予定は、通知の時刻に始まる時刻付きの予定にする（終日の予定では、その日の時刻に知らせられないため）。
    /// 1日だけなら TimedMinutes 分（その日の終わりまで）、何日かなら最後の日の終わり（翌日の 0:00）まで。
    /// </summary>
    public static class ScheduleExport
    {
        public const int TimedMinutes = 30;

        public static List<ExportEvent> Build(IEnumerable<ScheduleItem> items, DateTime today)
        {
            today = today.Date;
            var list = new List<ExportEvent>();
            foreach (var item in items ?? Enumerable.Empty<ScheduleItem>())
            {
                if (item == null) continue;
                var rule = ScheduleRepeat.Parse(item.Repeat);
                if (rule == null)
                {
                    if (item.End >= today) list.Add(Make(item, item.Start));
                    continue;
                }

                if (SeriesReaches(item, rule, today))
                {
                    var series = Make(item, item.Start);
                    series.Rule = rule;
                    foreach (var date in item.Skips.Concat(item.Moves.Keys).Distinct().OrderBy(date => date))
                        if (rule.Matches(item.Start, date)) series.Excluded.Add(Make(item, date).Start);
                    list.Add(series);
                }

                foreach (var move in item.Moves)
                {
                    if (!rule.Matches(item.Start, move.Key) || item.Skips.Contains(move.Key)) continue;
                    if (move.Value.AddDays(item.Days - 1) >= today) list.Add(Make(item, move.Value));
                }
            }
            return list;
        }

        /// <summary>繰り返しの予定が、今日以降に掛かる回を持つか（消した・動かした回も決まりどおりに数える）。</summary>
        private static bool SeriesReaches(ScheduleItem item, ScheduleRepeat rule, DateTime today)
        {
            if (!rule.Until.HasValue) return true;
            if (rule.Until.Value < today.AddDays(-(item.Days - 1))) return false;
            var bare = item.Clone();
            bare.Skips.Clear();
            bare.Moves.Clear();
            return ScheduleRepeat.Occurrences(bare, today, rule.Until.Value).Any();
        }

        private static ExportEvent Make(ScheduleItem item, DateTime start)
        {
            var e = new ExportEvent { Title = item.Title ?? string.Empty, Description = DescriptionOf(item) };
            if (item.Notify.HasValue)
            {
                e.Start = start.Date + item.Notify.Value;
                // 1日だけでも、その日の終わり（翌日 0:00）を越えない（越えると、送り先では翌日に掛かる予定になり、今日以降の範囲の決め方がずれる）
                var dayEnd = start.Date.AddDays(item.Days);
                e.End = item.Days > 1 || e.Start.AddMinutes(TimedMinutes) > dayEnd ? dayEnd : e.Start.AddMinutes(TimedMinutes);
                e.Remind = true;
            }
            else
            {
                e.AllDay = true;
                e.Start = start.Date;
                e.End = start.Date.AddDays(item.Days);
            }
            return e;
        }

        /// <summary>メモと、結んだ見出し（文書のファイル名と見出し）。フォルダの場所は出さない。</summary>
        private static string DescriptionOf(ScheduleItem item)
        {
            var parts = new List<string>();
            var memo = (item.Memo ?? string.Empty).Trim();
            if (memo.Length > 0) parts.Add(memo);
            if (item.IsLinked)
            {
                var path = item.LinkFile.Trim();
                var name = path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);   // \\Mac\… も / も（試験は Mac で動かすので Path.GetFileName に頼らない）
                parts.Add("Tsuzuri の見出し: " + (item.LinkHeading ?? string.Empty) + "（" + name + "）");
            }
            return string.Join("\n\n", parts);
        }
    }
}
