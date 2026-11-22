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
            if (rule.Until.HasValue) rule.Until = rule.Until.Value.AddDays(days);
            // 始まりの日は決まりに合わなくても1回目になる。つかんだのがそういう回なら、毎月の日付・第n曜日はそのまま（その日から決め直さない）
            var natural = oldRule.Kind == RepeatKind.MonthlyDay ? original.Day == (oldRule.MonthDay > 0 ? oldRule.MonthDay : oldStart.Day)
                        : oldRule.Kind == RepeatKind.MonthlyNth ? original != oldStart || NthOf(original, oldRule.Nth < 0) == oldRule.Nth && original.DayOfWeek == oldRule.NthDay
                        : true;
            switch (natural ? rule.Kind : RepeatKind.Daily)
            {
                case RepeatKind.Weekly:
                    rule.WeekDays = rule.WeekDays.Select(day => (DayOfWeek)((((int)day + days) % 7 + 7) % 7)).Distinct().OrderBy(day => day).ToList();
                    break;
                case RepeatKind.MonthlyDay:
                    // 日付を書いていなくても書く（始まりの日を days 日ずらすと、月の長さの違いで to と別の日付になることがあるため。1/20 の回を 2/20→3/5 へ）
                    rule.MonthDay = to.Day;
                    break;
                case RepeatKind.MonthlyNth:
                    // 第5は月によって無いので、最終にする（第3火曜の回を8日後へ動かすと、その月の最終水曜）
                    rule.NthDay = to.DayOfWeek;
                    rule.Nth = NthOf(to, rule.Nth < 0);
                    break;
            }
            item.Repeat = rule.ToString();

            if (rule.Kind == RepeatKind.Daily || rule.Kind == RepeatKind.Weekly)
            {
                for (var date = item.Start; date <= item.Start.AddDays(730); date = date.AddDays(1))
                    if (rule.Matches(item.Start, date) != oldRule.Matches(oldStart, date.AddDays(-days))) return false;
            }
            else if (!rule.Matches(item.Start, to)) return false;

            item.Skips = item.Skips.Select(date => date.AddDays(days)).ToList();
            var moves = new SortedDictionary<DateTime, DateTime>();
            foreach (var pair in item.Moves)
            {
                var key = pair.Key.AddDays(days);
                if (key != pair.Value) moves[key] = pair.Value;
            }
            item.Moves = moves;
            ScheduleRepeat.Prune(item);
            return true;
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
