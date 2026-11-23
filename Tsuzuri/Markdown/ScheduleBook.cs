using System;
using System.Collections.Generic;
using System.Linq;

namespace Tsuzuri.Markdown
{
    /// <summary>帯のつかみ方（動かす・左の端を引っぱる・右の端を引っぱる）。</summary>
    public enum ScheduleGesture { None, Move, ResizeStart, ResizeEnd }

    /// <summary>繰り返しの予定の回を動かすとき、どこまで動かすか（2026-11-23。利用者の考え。Google カレンダーと同じ3つ）。</summary>
    public enum ScheduleMoveScope { One, Following, All }

    /// <summary>
    /// カレンダーの予定の一覧と、それを直す操作（SPEC 22章。2026-11-21 に CalendarView から分けた）。画面を持たないので、tools/schedule-fuzz で操作ごと確かめられる。
    /// 直す操作はどれも、直す前の中身を戻す記録（Ctrl+Z）に積み、変わったら true を返す。保存は呼ぶ側（CalendarView）がする。
    /// 直す相手（ScheduleItem）は Items の中の物。外で書き換わって読み直したあとは、Find で探し直す。
    /// </summary>
    public sealed class ScheduleBook
    {
        public const int UndoLimit = 50;

        private readonly List<List<ScheduleItem>> _undo = new List<List<ScheduleItem>>();

        public List<ScheduleItem> Items { get; private set; }

        public ScheduleBook()
        {
            Items = new List<ScheduleItem>();
        }

        /// <summary>直前の Drag で、頼まれたとおりにできなかったわけ（できたなら null）。画面の下に出す。</summary>
        public string LastNote { get; private set; }

        public int UndoCount
        {
            get { return _undo.Count; }
        }

        /// <summary>保存先に書く中身。</summary>
        public string Serialize()
        {
            return ScheduleFile.Serialize(Items);
        }

        // ---------------------------------------------------------------
        // 読み直す・探し直す
        // ---------------------------------------------------------------

        /// <summary>
        /// 読んだ中身で入れ替える（初めて読んだとき・外で書き換わったとき）。戻す記録は捨てる（外の書き換えを戻してしまわないため）。
        /// 入れ替える前の一覧を返す（増えた・変わった予定を探すため）。
        /// </summary>
        public List<ScheduleItem> Replace(string text)
        {
            var before = Items;
            Items = ScheduleFile.Parse(text);
            _undo.Clear();
            return before;
        }

        /// <summary>予定を見分ける形（日・名前・結んだ見出し・メモ・通知・繰り返し）。読み直したあとに同じ予定を探す・増えた予定を探すのに使う。</summary>
        public static string KeyOf(ScheduleItem item)
        {
            return item.Start.Ticks + "|" + item.End.Ticks + "|" + item.Title + "|" + item.LinkFile + "|" + item.LinkPage + "|" + item.LinkHeading + "|" + item.Memo
                   + "|" + (item.Notify.HasValue ? item.Notify.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
                   + "|" + item.Repeat + "|" + string.Join(",", item.Skips.Select(date => date.Ticks))
                   + "|" + string.Join(",", item.Moves.Select(pair => pair.Key.Ticks + ">" + pair.Value.Ticks));
        }

        /// <summary>直す相手。同じ物が一覧に無ければ（読み直した）、key と同じ形の予定。それも無ければ null（外で変わった・消えた）。</summary>
        public ScheduleItem Find(ScheduleItem item, string key)
        {
            if (item != null && Items.Contains(item)) return item;
            return key != null ? Items.FirstOrDefault(other => KeyOf(other) == key) : null;
        }

        /// <summary>
        /// before から今の一覧で、増えた（変わった）予定と、消えた（変わる前の）予定の数。同じ形の予定は、数が合うだけを同じとみなす。
        /// 変わった予定は「消えて増えた」に見える。
        /// </summary>
        public List<ScheduleItem> AddedSince(List<ScheduleItem> before, out int removed)
        {
            var oldKeys = new Dictionary<string, int>();
            foreach (var item in before)
            {
                var key = KeyOf(item);
                int n;
                oldKeys[key] = oldKeys.TryGetValue(key, out n) ? n + 1 : 1;
            }

            var added = new List<ScheduleItem>();
            foreach (var item in Items)
            {
                var key = KeyOf(item);
                int n;
                if (oldKeys.TryGetValue(key, out n) && n > 0) oldKeys[key] = n - 1;
                else added.Add(item);
            }
            removed = oldKeys.Values.Sum();
            return added;
        }

        // ---------------------------------------------------------------
        // 直す
        // ---------------------------------------------------------------

        private void Remember()
        {
            _undo.Add(Items.Select(item => item.Clone()).ToList());
            if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        }

        /// <summary>予定を足す（名前は1行にする）。足した予定を返す。</summary>
        public ScheduleItem Add(DateTime start, DateTime end, string title, string linkFile = null, string linkPage = null, string linkHeading = null)
        {
            Remember();
            var item = new ScheduleItem
            {
                Start = start.Date,
                End = end.Date < start.Date ? start.Date : end.Date,
                Title = ScheduleFile.OneLine(title),
            };
            if (!string.IsNullOrEmpty(linkFile))
            {
                item.LinkFile = linkFile;
                item.LinkPage = linkPage ?? string.Empty;
                item.LinkHeading = linkHeading ?? string.Empty;
            }
            Items.Add(item);
            return item;
        }

        /// <summary>
        /// 帯を delta 日つかんで動かした・端を引っぱった形（item は変えない）。長さは 1 日より短くしない。
        /// </summary>
        public static ScheduleItem Reshaped(ScheduleItem item, ScheduleGesture gesture, int delta)
        {
            var moved = item.Clone();
            switch (gesture)
            {
                case ScheduleGesture.Move:
                    moved.Start = item.Start.AddDays(delta);
                    moved.End = item.End.AddDays(delta);
                    break;
                case ScheduleGesture.ResizeEnd:
                    moved.End = item.End.AddDays(delta) < item.Start ? item.Start : item.End.AddDays(delta);
                    break;
                case ScheduleGesture.ResizeStart:
                    moved.Start = item.Start.AddDays(delta) > item.End ? item.End : item.Start.AddDays(delta);
                    break;
            }
            return moved;
        }

        /// <summary>
        /// 帯をつかんで delta 日動かした・引っぱった。繰り返しの予定を動かすと、original の回だけが動く（元の日へ戻したら、動かした記録を消す）。
        /// 繰り返しの予定の端を引っぱると、全部の回の長さが変わる（左の端は引っぱれない）。
        /// </summary>
        /// 繰り返しの予定の回を動かすときは scope で決める（2026-11-23）:
        ///   One       その回だけ（ほかの回はそのまま）
        ///   Following その回から後を、その回を動かした分だけずらす。前の回は元の予定に残し（終わりの日をその回の前の日にする）、
        ///             その回から後を新しい予定に分ける。その回が1回目なら All と同じ
        ///   All       全部の回を、その回を動かした分だけずらす（始まりの日・決まり・消した回・動かした回・終わりの日ごと。ShiftSeries）
        /// </summary>
        /// <param name="shownStart">つかんだときに出ていた、その回の始まりの日（動かした回なら動かした先）。</param>
        public bool Drag(ScheduleItem item, DateTime? original, DateTime shownStart, ScheduleGesture gesture, int delta,
                         ScheduleMoveScope scope = ScheduleMoveScope.One)
        {
            LastNote = null;
            if (delta == 0 || gesture == ScheduleGesture.None) return false;

            if (item.IsRepeat && original.HasValue && gesture == ScheduleGesture.Move)
            {
                var to = shownStart.AddDays(delta);
                DateTime now;
                if (!item.Moves.TryGetValue(original.Value, out now)) now = original.Value;
                if (to == now) return false;

                var days = (int)(to - original.Value).TotalDays;
                var rule = ScheduleRepeat.Parse(item.Repeat);
                // 決まりが読めない・元の日へ戻す（ずらす分が 0）なら、その回だけ
                if (scope != ScheduleMoveScope.One && rule != null && days != 0)
                {
                    // 写しで試し、ずらした決まりで回が並ばなければ（2週ごとの曜日が週の区切りをまたぐ・月の長さの違いで月がずれる）、その回だけ動かす
                    var head = item.Clone();
                    var split = scope == ScheduleMoveScope.Following && original.Value > item.Start;
                    var tail = split ? SplitAt(head, rule, original.Value) : head;
                    tail.Moves.Remove(original.Value);   // つかんだ回は、ずらした先が決まりどおりの日になる
                    if (ShiftSeries(tail, days, original.Value, to))
                    {
                        Remember();
                        item.Start = head.Start;
                        item.End = head.End;
                        item.Repeat = head.Repeat;
                        item.Skips = head.Skips;
                        item.Moves = head.Moves;
                        if (split) Items.Insert(Items.IndexOf(item) + 1, tail);
                        return true;
                    }
                    LastNote = "この繰り返しの決まりでは" + (split ? "これ以降の回" : "全部の回") + "を同じようにずらせないので、この回だけ動かしました";
                }

                Remember();
                if (to == original.Value) item.Moves.Remove(original.Value);
                else item.Moves[original.Value] = to;
                return true;
            }
            if (item.IsRepeat && original.HasValue && gesture == ScheduleGesture.ResizeStart) return false;

            var changed = Reshaped(item, gesture, delta);
            if (changed.Start == item.Start && changed.End == item.End) return false;
            Remember();
            item.Start = changed.Start;
            item.End = changed.End;
            return true;
        }

        /// <summary>
        /// 繰り返しの予定を at の回で2つに分ける（at は決まりどおりの日で、1回目より後）。item は at の前の日までにし、at から後を持つ新しい予定を返す（一覧には足さない）。
        /// 消した回・動かした回は、元の日が at より前なら item に、at から後なら新しい予定に。
        /// 新しい予定の1回目は at（決まりどおりの日なので、何日ごと・何週ごと・何か月ごとの数え方は変わらない）。
        /// </summary>
        private static ScheduleItem SplitAt(ScheduleItem item, ScheduleRepeat rule, DateTime at)
        {
            var length = item.End - item.Start;
            var tail = item.Clone();
            tail.Start = at;
            tail.End = at + length;
            tail.Skips = item.Skips.Where(date => date >= at).ToList();
            tail.Moves = new SortedDictionary<DateTime, DateTime>(item.Moves.Where(pair => pair.Key >= at).ToDictionary(pair => pair.Key, pair => pair.Value));
            var head = ScheduleRepeat.Parse(item.Repeat);
            var before = at.AddDays(-1);
            head.Until = head.Until.HasValue && head.Until.Value < before ? head.Until.Value : before;
            item.Repeat = head.ToString();
            ScheduleRepeat.Prune(item);
            return tail;
        }

        /// <summary>
        /// 繰り返しの予定の全部の回を days 日ずらす。to はつかんだ回を動かした先（毎月の日付・第n曜日を、その日から決め直す）。
        /// 始まりの日・終わりの日・消した回は days 日ずらす。動かした回は、元の日だけずらして動かした先はそのまま（ずらした日と同じになったら記録を消す）。
        /// 決まりは、毎週の曜日を days 日ずらし、毎月の日付・第n曜日を to から決め直す（毎日・毎年・曜日を書いていない毎週は、始まりの日から決まる）。
        /// ずらしたあと決まりに合わなくなった「消した」「動かした」は捨てる（Prune）。
        /// ずらした決まりで回が並ばなければ false（item は途中まで変わっているので、写しで呼ぶ）:
        ///   毎日・毎週は、どの回もちょうど days 日ずれるか（2年分を見る）。2週ごとの曜日が週の区切り（日曜）をまたぐと、週の数え方が変わって崩れる
        ///   毎月・毎年は、つかんだ回が to に出るか（月の長さの違いで、始まりの日と to とで月のずれ方が違うことがある）
        /// </summary>
        private static bool ShiftSeries(ScheduleItem item, int days, DateTime original, DateTime to)
        {
            var rule = ScheduleRepeat.Parse(item.Repeat);
            var oldRule = ScheduleRepeat.Parse(item.Repeat);
            var oldStart = item.Start;
            item.Start = item.Start.AddDays(days);
            item.End = item.End.AddDays(days);
            // 始まりの日は決まりに合わなくても1回目になる。つかんだのがそういう回なら、毎月の日付・第n曜日はそのまま（その日から決め直さない）
            var natural = oldRule.Kind == RepeatKind.MonthlyDay ? (oldRule.MonthDay < 0 ? IsMonthEnd(original) : original.Day == (oldRule.MonthDay > 0 ? oldRule.MonthDay : oldStart.Day))
                        : oldRule.Kind == RepeatKind.MonthlyNth ? original != oldStart || NthOf(original, oldRule.Nth < 0) == oldRule.Nth && original.DayOfWeek == oldRule.NthDay
                        : true;
            switch (natural ? rule.Kind : RepeatKind.Daily)
            {
                case RepeatKind.Weekly:
                    rule.WeekDays = rule.WeekDays.Select(day => (DayOfWeek)((((int)day + days) % 7 + 7) % 7)).Distinct().OrderBy(day => day).ToList();
                    break;
                case RepeatKind.MonthlyDay:
                    // 日付を書いていなくても書く（始まりの日を days 日ずらすと、月の長さの違いで to と別の日付になることがあるため。1/20 の回を 2/20→3/5 へ）
                    // 末日の決まりで、動かした先も月の末日なら末日のまま（1/31 の回を 2/28 へ動かしても、毎月 末日）
                    rule.MonthDay = oldRule.MonthDay < 0 && IsMonthEnd(to) ? -1 : to.Day;
                    break;
                case RepeatKind.MonthlyNth:
                    // 第5は月によって無いので、最終にする（第3火曜の回を8日後へ動かすと、その月の最終水曜）
                    rule.NthDay = to.DayOfWeek;
                    rule.Nth = NthOf(to, rule.Nth < 0);
                    break;
            }

            if (rule.Kind == RepeatKind.Daily || rule.Kind == RepeatKind.Weekly)
            {
                // 毎日・毎週は、どの回もちょうど days 日ずれる（確かめる）ので、終わりの日・消した回・動かした回も days 日ずらす
                if (rule.Until.HasValue) rule.Until = rule.Until.Value.AddDays(days);
                item.Repeat = rule.ToString();
                for (var date = item.Start; date <= item.Start.AddDays(730); date = date.AddDays(1))
                    if (rule.Matches(item.Start, date) != oldRule.Matches(oldStart, date.AddDays(-days))) return false;

                item.Skips = item.Skips.Select(date => date.AddDays(days)).ToList();
                var shifted = new SortedDictionary<DateTime, DateTime>();
                foreach (var pair in item.Moves)
                {
                    var key = pair.Key.AddDays(days);
                    if (key != pair.Value) shifted[key] = pair.Value;
                }
                item.Moves = shifted;
                ScheduleRepeat.Prune(item);
                return true;
            }
            else
            {
                // 毎月・毎年は、月の長さで日数が回ごとに違うので、日数でなく、つかんだ回と同じ月数（毎年は年数）だけ先の月（年）の決まりの日へ移す（2026-11-23）。
                // 1回目・終わりの日・消した回・動かした回も同じ（日数でずらすと、末日の回が月末に乗らず、消した回が戻る・終わりの日で最後の回が欠けていた）。
                // 回はひと月（ひと年）に1つなので、移した先が重なることはない。その月に回が無ければ捨てる（毎月30日の2月）
                var yearly = rule.Kind == RepeatKind.Yearly;
                Func<DateTime, int> period = date => yearly ? date.Year : date.Year * 12 + date.Month;
                var shift = period(to) - period(original);
                var oldOpen = ScheduleRepeat.Parse(oldRule.ToString());
                oldOpen.Until = null;

                // 1回目: 決まりの日だったなら、移した月の決まりの日（無い月なら、同じようにはずらせない）。
                // 決まりの日でない1回目（Claude が書いた形など）と毎年は、日数でずらしたまま（毎年の決まりの日は1回目から決まる）
                if (!yearly && oldRule.OnRuleDay(oldStart, oldStart))
                {
                    var length = item.End - item.Start;
                    var first = RuleDayIn(rule, item.Start, period(oldStart) + shift, false, true);
                    if (!first.HasValue) return false;
                    item.Start = first.Value;
                    item.End = first.Value + length;
                }
                var newStart = item.Start;
                var newOpen = ScheduleRepeat.Parse(rule.ToString());
                newOpen.Until = null;

                Func<DateTime, DateTime?> carry = date =>
                {
                    if (date == oldStart) return newStart;
                    if (date < oldStart || !oldOpen.OnRuleDay(oldStart, date) || !oldOpen.Matches(oldStart, date)) return null;
                    return RuleDayIn(newOpen, newStart, period(date) + shift, yearly, false);
                };

                if (oldRule.Until.HasValue)
                {
                    // 終わりの日は、その日までの最後の回が移った先。その月（年）に回が無ければ、その月（年）の終わり（回が1つも無ければ日数でずらす）
                    DateTime? lastOld = null;
                    for (var p = period(oldRule.Until.Value); p > period(oldStart) && !lastOld.HasValue; p--)
                    {
                        var day = RuleDayIn(oldOpen, oldStart, p, yearly, false);
                        if (day.HasValue && day.Value <= oldRule.Until.Value) lastOld = day;
                    }
                    if (!lastOld.HasValue && oldStart <= oldRule.Until.Value)
                    {
                        var day = RuleDayIn(oldOpen, oldStart, period(oldStart), yearly, false);
                        lastOld = day.HasValue && day.Value > oldStart && day.Value <= oldRule.Until.Value ? day : oldStart;
                    }
                    if (!lastOld.HasValue) rule.Until = oldRule.Until.Value.AddDays(days);
                    else
                    {
                        var target = period(lastOld.Value) + shift;
                        rule.Until = carry(lastOld.Value) ?? PeriodEnd(target, yearly);
                        if (!rule.Until.HasValue) return false;
                    }
                }
                item.Repeat = rule.ToString();
                if (!rule.Matches(item.Start, to) || carry(original) != to) return false;

                // 念のため: 移した先が重なったら（2つの回が1つになる）、同じようにはずらせない
                var keys = new[] { oldStart, original }.Concat(item.Skips).Concat(item.Moves.Keys).Distinct().ToList();
                var carried = keys.Select(carry).Where(date => date.HasValue).Select(date => date.Value).ToList();
                if (carried.Count != carried.Distinct().Count()) return false;

                item.Skips = item.Skips.Select(carry).Where(date => date.HasValue).Select(date => date.Value).Distinct().ToList();
                var moves = new SortedDictionary<DateTime, DateTime>();
                foreach (var pair in item.Moves)
                {
                    var key = carry(pair.Key);
                    if (key.HasValue && key.Value != pair.Value) moves[key.Value] = pair.Value;
                }
                item.Moves = moves;
                ScheduleRepeat.Prune(item);
                return true;
            }
        }

        /// <summary>
        /// その月（yearly ならその年。period は 年*12+月 か 年）の、決まりの回の日（間隔も見る。無ければ null）。終わりの日は見ない。
        /// 1回目そのものは、決まりの日のときだけ数える。anyDay なら、間隔と start より前かを見ない（ずらした1回目を決めるとき）。
        /// </summary>
        private static DateTime? RuleDayIn(ScheduleRepeat rule, DateTime start, int period, bool yearly, bool anyDay)
        {
            if (yearly)
            {
                if (period < 1 || period > 9999 || (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(period))) return null;
                var date = new DateTime(period, start.Month, start.Day);
                return anyDay || rule.Matches(start, date) ? date : (DateTime?)null;
            }
            var year = (period - 1) / 12;
            if (year < 1 || year > 9999) return null;
            var month = (period - 1) % 12 + 1;
            for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
            {
                var date = new DateTime(year, month, day);
                if (rule.OnRuleDay(start, date) && (anyDay || rule.Matches(start, date))) return date;
            }
            return null;
        }

        /// <summary>その月（年）の終わりの日。範囲の外なら null。</summary>
        private static DateTime? PeriodEnd(int period, bool yearly)
        {
            var year = yearly ? period : (period - 1) / 12;
            if (year < 1 || year > 9999) return null;
            return yearly ? new DateTime(year, 12, 31) : new DateTime(year, (period - 1) % 12 + 1, 1).AddMonths(1).AddDays(-1);
        }

        private static bool IsMonthEnd(DateTime date)
        {
            return date.Day == DateTime.DaysInMonth(date.Year, date.Month);   // AddDays(1) は 9999-12-31 で投げる
        }

        /// <summary>その日が月の第何曜日か（1〜4、第5・last で最終の週なら -1）。</summary>
        private static int NthOf(DateTime date, bool last)
        {
            var nth = (date.Day - 1) / 7 + 1;
            return nth == 5 || last && date.AddDays(7).Month != date.Month ? -1 : nth;
        }

        /// <summary>予定を消す。繰り返しの予定で original（その回の決まりどおりの日）があれば、その回だけ消す。</summary>
        public bool Delete(ScheduleItem item, DateTime? original)
        {
            if (!Items.Contains(item)) return false;
            Remember();
            if (item.IsRepeat && original.HasValue)
            {
                item.Moves.Remove(original.Value);
                if (!item.Skips.Contains(original.Value)) item.Skips.Add(original.Value);
                item.Skips.Sort();
                return true;
            }
            Items.Remove(item);
            return true;
        }

        /// <summary>
        /// 名前・繰り返し・通知の時刻を直す（いつも全部の回）。結んだ予定の名前は見出しの文字のままにする（title は見ない）。
        /// 決まりが変わったら、もう回でない日の「消した」「動かした」は捨てる。
        /// </summary>
        public bool Edit(ScheduleItem item, string title, string repeat, TimeSpan? notify)
        {
            var name = item.IsLinked ? item.Title : ScheduleFile.OneLine(title);
            repeat = string.IsNullOrEmpty(repeat) ? null : repeat;
            if (notify.HasValue) notify = new TimeSpan(notify.Value.Hours, notify.Value.Minutes, 0);
            if (name == item.Title && repeat == item.Repeat && notify == item.Notify) return false;

            Remember();
            item.Title = name;   // メモは画面では持たないが、保存先に書いてあれば消さずに残す
            item.Notify = notify;
            if (item.Repeat != repeat)
            {
                item.Repeat = repeat;
                ScheduleRepeat.Prune(item);
            }
            return true;
        }

        /// <summary>一つ前の中身に戻す。戻す記録が無ければ false。</summary>
        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            Items = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            return true;
        }
    }
}
