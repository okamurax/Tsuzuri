using System;
using System.Collections.Generic;
using System.Linq;

namespace Tsuzuri.Markdown
{
    /// <summary>帯のつかみ方（動かす・左の端を引っぱる・右の端を引っぱる）。</summary>
    public enum ScheduleGesture { None, Move, ResizeStart, ResizeEnd }

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
        /// <param name="shownStart">つかんだときに出ていた、その回の始まりの日（動かした回なら動かした先）。</param>
        public bool Drag(ScheduleItem item, DateTime? original, DateTime shownStart, ScheduleGesture gesture, int delta)
        {
            if (delta == 0 || gesture == ScheduleGesture.None) return false;

            if (item.IsRepeat && original.HasValue && gesture == ScheduleGesture.Move)
            {
                var to = shownStart.AddDays(delta);
                DateTime now;
                if (!item.Moves.TryGetValue(original.Value, out now)) now = original.Value;
                if (to == now) return false;
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
