using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Tsuzuri.Markdown;

// カレンダーの予定のファズテスト。tools/schedule-fuzz.sh から動かす。
// 種が同じなら同じ入力を作るので、NG が出たら「回数 種」で同じものを出し直せる。
internal static class Program
{
    private static Random _random;
    private static int _ng;
    private static readonly DateTime MinDay = new DateTime(2024, 1, 1);
    private static readonly DateTime MaxDay = new DateTime(2028, 12, 31);

    private static int Main(string[] args)
    {
        var rounds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3000;
        var seed = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1;
        _random = new Random(seed);

        for (var round = 0; round < rounds && _ng < 20; round++)
        {
            RoundTrip(round);
            Garbage(round);
            Occurrences(round);
            WeekSplit(round);
            Rules(round);
            PruneKeepsMatching(round);
            BookOperations(round);
        }

        Console.WriteLine(_ng == 0 ? "全部 OK（" + rounds + " 回。種 " + seed + "）" : "NG: " + _ng + " 件（種 " + seed + "）");
        return _ng == 0 ? 0 : 1;
    }

    private static void Ng(int round, string what, string detail)
    {
        _ng++;
        Console.WriteLine("NG（" + round + " 回目）: " + what);
        if (!string.IsNullOrEmpty(detail)) Console.WriteLine(detail.Length > 3000 ? detail.Substring(0, 3000) + "…" : detail);
    }

    // ---------------------------------------------------------------
    // でたらめな材料
    // ---------------------------------------------------------------

    private static T Pick<T>(params T[] items) { return items[_random.Next(items.Length)]; }

    private static bool Chance(int percent) { return _random.Next(100) < percent; }

    private static DateTime Day()
    {
        return MinDay.AddDays(_random.Next((int)(MaxDay - MinDay).TotalDays + 1));
    }

    private static string D(DateTime date) { return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

    private static readonly string[] TitleParts =
    {
        "", " ", "10:00 打合せ", "出張", "## x", "# 見出し", "〜2026-01-05", "~ 2026-02-03", "〜 2026-13-01", "2026-01-04 から",
        "<!-- link: a | b | c -->", "-->", "<!--", " | ", "|", "\\## y", "\\\\## z", "\t", "  ", "😀", "A社", "\r", "\n", "\r\n", "x\ny",
        "<!-- repeat: FREQ=DAILY -->", "<!-- skip: 2026-01-01 -->", "<!-- move: 2026-01-01>2026-01-02 -->", "…", "　",
    };

    private static string Title()
    {
        var text = new StringBuilder();
        var count = _random.Next(4);
        for (var i = 0; i < count; i++) text.Append(Pick(TitleParts));
        return text.ToString();
    }

    private static readonly string[] MemoLines =
    {
        "", " ", "　", "メモ", "## 2026-01-01 見出しの形", "## 議題", "\\## そのまま", "\\\\## 二つ", "##x", "# 予定",
        "<!-- link: x | y | z -->", "<!-- repeat: FREQ=WEEKLY -->", "<!-- skip: 2026-01-08 -->", "<!-- move: 2026-01-15>2026-01-16 -->",
        "<!-- tsuzuri-schedule: x -->", "- 箇条書き", "  字下げ", "\t", "10:00〜12:00", "## 2026-13-01 月が無い", "## 見出しだけ",
    };

    private static string Memo()
    {
        if (Chance(40)) return string.Empty;
        var lines = new List<string>();
        var count = 1 + _random.Next(5);
        for (var i = 0; i < count; i++) lines.Add(Pick(MemoLines));
        return string.Join(Pick("\n", "\r\n", "\n"), lines);
    }

    private static string RandomRule(DateTime start)
    {
        if (Chance(8)) return Pick("FREQ=WEEKLY;COUNT=3", "freq=daily", "FREQ=MONTHLY;BYDAY=TU", "FREQ=HOURLY", "garbage", "FREQ=WEEKLY;BYDAY=XX",
                                   "FREQ=MONTHLY;BYMONTHDAY=32", "FREQ=YEARLY;INTERVAL=0", "FREQ=DAILY;UNTIL=2026", "FREQ=WEEKLY;BYDAY=MO;;");
        var rule = new ScheduleRepeat
        {
            Kind = Pick(RepeatKind.Daily, RepeatKind.Weekly, RepeatKind.MonthlyDay, RepeatKind.MonthlyNth, RepeatKind.Yearly),
            Interval = Chance(60) ? 1 : 1 + _random.Next(4),
        };
        switch (rule.Kind)
        {
            case RepeatKind.Weekly:
                foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
                    if (Chance(35)) rule.WeekDays.Add(day);
                break;
            case RepeatKind.MonthlyDay:
                rule.MonthDay = Chance(50) ? 0 : 1 + _random.Next(31);
                break;
            case RepeatKind.MonthlyNth:
                rule.Nth = Chance(25) ? -1 : 1 + _random.Next(5);
                rule.NthDay = (DayOfWeek)_random.Next(7);
                break;
        }
        if (Chance(40)) rule.Until = start.AddDays(_random.Next(-30, 900));
        return rule.ToString();
    }

    private static ScheduleItem Item()
    {
        var start = Day();
        var item = new ScheduleItem
        {
            Start = start,
            End = Chance(70) ? start : start.AddDays(1 + _random.Next(Chance(90) ? 6 : 40)),
            Title = Title(),
            Memo = Memo(),
        };
        if (Chance(25))
        {
            item.LinkFile = Pick(@"D:\案件\見積.md", @"\\Mac\Home\a b.md", " C:\\x.md ", "rel/p.md");
            item.LinkPage = Pick("", "p3", "p12");
            item.LinkHeading = Pick("見積", "a | b", "x --> y", " 前後に空白 ", "## h", "見出し -");
        }
        if (Chance(45))
        {
            item.Repeat = RandomRule(start);
            var count = _random.Next(4);
            for (var i = 0; i < count; i++)
            {
                var date = start.AddDays(_random.Next(0, 400));
                if (Chance(50)) { if (!item.Skips.Contains(date)) item.Skips.Add(date); }
                else item.Moves[date] = date.AddDays(_random.Next(-10, 11));
            }
        }
        return item;
    }

    // ---------------------------------------------------------------
    // 読み書きで変わらない
    // ---------------------------------------------------------------

    /// <summary>書いて読み戻したときの、あるべき形（名前は1行・link は Plain・メモは端の空行を除く・読めない決まりの行は書かない）。</summary>
    private static ScheduleItem Expected(ScheduleItem item)
    {
        var expected = item.Clone();
        expected.Title = ScheduleFile.OneLine(item.Title);

        var lines = (item.Memo ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        expected.Memo = string.Join("\n", lines);

        if (item.IsLinked)
        {
            expected.LinkFile = Plain(item.LinkFile);
            expected.LinkPage = Plain(item.LinkPage);
            expected.LinkHeading = Plain(item.LinkHeading);
            if (expected.LinkFile.Length == 0) expected.LinkFile = expected.LinkPage = expected.LinkHeading = null;
        }

        var writable = item.IsRepeat && System.Text.RegularExpressions.Regex.IsMatch(item.Repeat, @"^[A-Za-z0-9=;,\-]+$");
        if (!writable)
        {
            expected.Repeat = null;
            expected.Skips.Clear();
            expected.Moves.Clear();
        }
        else expected.Skips = expected.Skips.Distinct().OrderBy(date => date).ToList();
        return expected;
    }

    private static string Plain(string text)
    {
        return ScheduleFile.OneLine(text).Replace("-->", "-- >");
    }

    private static string Show(ScheduleItem item)
    {
        return D(item.Start) + "〜" + D(item.End) + " [" + item.Title + "] memo[" + item.Memo.Replace("\n", "⏎") + "] link[" + item.LinkFile + "|" + item.LinkPage + "|" + item.LinkHeading
               + "] repeat[" + item.Repeat + "] skip[" + string.Join(",", item.Skips.Select(D)) + "] move[" + string.Join(",", item.Moves.Select(p => D(p.Key) + ">" + D(p.Value))) + "]";
    }

    private static bool Same(ScheduleItem a, ScheduleItem b)
    {
        return a.Start == b.Start && a.End == b.End && a.Title == b.Title && a.Memo == b.Memo
               && (a.LinkFile ?? "") == (b.LinkFile ?? "") && (a.IsLinked ? a.LinkPage == b.LinkPage && a.LinkHeading == b.LinkHeading : !b.IsLinked)
               && (a.Repeat ?? "") == (b.Repeat ?? "") && a.Skips.SequenceEqual(b.Skips) && a.Moves.SequenceEqual(b.Moves);
    }

    private static void RoundTrip(int round)
    {
        var items = new List<ScheduleItem>();
        var count = _random.Next(8);
        for (var i = 0; i < count; i++) items.Add(Item());

        var text = ScheduleFile.Serialize(items);
        var back = ScheduleFile.Parse(text);
        var expected = items.OrderBy(item => item.Start).Select(Expected).ToList();   // 書くときは始まりの日の順（同じ日は前の並び）

        if (back.Count != expected.Count)
        {
            Ng(round, "読み書きで数が変わる: " + expected.Count + " → " + back.Count, text);
            return;
        }
        for (var i = 0; i < back.Count; i++)
        {
            if (Same(back[i], expected[i])) continue;
            Ng(round, "読み書きで変わる（" + i + " 番目）", "あるべき: " + Show(expected[i]) + "\n読んだ:   " + Show(back[i]) + "\n" + text);
            return;
        }
        if (ScheduleFile.Serialize(back) != text) Ng(round, "2回目の書き出しが違う", text);
    }

    // ---------------------------------------------------------------
    // 崩れた .md を読んでも落ちず、2回目からは同じ
    // ---------------------------------------------------------------

    private static readonly string[] GarbageLines =
    {
        "## 2026-01-01 x", "## 2026-01-01〜2026-01-03 y", "## 2026-01-05 ~ 2026-01-02 逆", "## 2026-02-30 無い日", "## 2026-01-01", "##  2026-01-01 二つの空白",
        "## 2026-1-1 短い", "<!-- link: a | b | c -->", "<!-- link: a | b -->", "<!-- repeat: FREQ=WEEKLY;BYDAY=TU -->", "<!-- repeat: FREQ=WEEKLY; BYDAY=TU -->",
        "<!-- skip: 2026-01-08 2026-01-15 -->", "<!-- skip: 2026-01-08 x -->", "<!-- skip:  -->", "<!-- move: 2026-01-08>2026-01-09 -->", "<!-- move: 2026-01-08>2026-01-09 2026-01-08>2026-01-10 -->",
        "<!-- move: 2026-01-08 -->", "メモ", "", " ", "\\## x", "# 予定", ScheduleFile.Marker, "\r", "## 見出し", "### 2026-01-01 三つ",
    };

    private static void Garbage(int round)
    {
        var lines = new List<string>();
        var count = _random.Next(20);
        for (var i = 0; i < count; i++) lines.Add(Pick(GarbageLines));
        var text = string.Join(Pick("\n", "\r\n"), lines);

        try
        {
            var once = ScheduleFile.Serialize(ScheduleFile.Parse(text));
            var twice = ScheduleFile.Serialize(ScheduleFile.Parse(once));
            if (once != twice) Ng(round, "崩れた .md: 2回目から同じにならない", text + "\n--- 1回目\n" + once + "\n--- 2回目\n" + twice);
            foreach (var item in ScheduleFile.Parse(text))
                if (item.End < item.Start) Ng(round, "崩れた .md: 終わりが始まりより前", text);
        }
        catch (Exception ex)
        {
            Ng(round, "崩れた .md で落ちた: " + ex.GetType().Name + " " + ex.Message, text);
        }
    }

    // ---------------------------------------------------------------
    // 繰り返しの回を、別に書いた素朴な数え方と比べる
    // ---------------------------------------------------------------

    /// <summary>決まりどおりの日を、日を足していく作り方で並べる（ScheduleRepeat.Matches の当てはめ方とは別に書く）。limit までの日。</summary>
    private static List<DateTime> NaiveOriginals(ScheduleItem item, ScheduleRepeat rule, DateTime limit)
    {
        var dates = new SortedSet<DateTime> { item.Start };
        var start = item.Start;
        var last = rule.Until.HasValue && rule.Until.Value < limit ? rule.Until.Value : limit;

        switch (rule.Kind)
        {
            case RepeatKind.Daily:
                for (var date = start; date <= last; date = date.AddDays(rule.Interval)) dates.Add(date);
                break;
            case RepeatKind.Weekly:
            {
                var week = start.AddDays(-(int)start.DayOfWeek);
                var days = rule.WeekDays.Count > 0 ? rule.WeekDays : new List<DayOfWeek> { start.DayOfWeek };
                for (; week <= last; week = week.AddDays(7 * rule.Interval))
                    foreach (var day in days)
                    {
                        var date = week.AddDays((int)day);
                        if (date >= start && date <= last) dates.Add(date);
                    }
                break;
            }
            case RepeatKind.MonthlyDay:
            case RepeatKind.MonthlyNth:
                for (var month = new DateTime(start.Year, start.Month, 1); month <= last; month = month.AddMonths(rule.Interval))
                {
                    var inMonth = Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month)).Select(i => month.AddDays(i)).ToList();
                    DateTime? hit = null;
                    if (rule.Kind == RepeatKind.MonthlyDay)
                    {
                        var day = rule.MonthDay > 0 ? rule.MonthDay : start.Day;
                        if (day <= inMonth.Count) hit = inMonth[day - 1];
                    }
                    else
                    {
                        var same = inMonth.Where(date => date.DayOfWeek == rule.NthDay).ToList();
                        if (rule.Nth < 0) hit = same[same.Count - 1];
                        else if (rule.Nth <= same.Count) hit = same[rule.Nth - 1];
                    }
                    if (hit.HasValue && hit.Value >= start && hit.Value <= last) dates.Add(hit.Value);
                }
                break;
            default:
                for (var year = start.Year; year <= last.Year; year += rule.Interval)
                {
                    if (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year)) continue;
                    var date = new DateTime(year, start.Month, start.Day);
                    if (date >= start && date <= last) dates.Add(date);
                }
                break;
        }

        // 終わりの日が1回目より前なら、1回目も出さない（Matches と同じ決め）
        if (rule.Until.HasValue && rule.Until.Value < start) dates.Clear();
        return dates.ToList();
    }

    private static string Key(ScheduleOccurrence occ) { return D(occ.Original ?? DateTime.MinValue) + ">" + D(occ.Start) + "~" + D(occ.End); }

    private static void Occurrences(int round)
    {
        var item = Item();
        var from = Day();
        var to = from.AddDays(_random.Next(0, 120));
        var actual = ScheduleRepeat.Occurrences(item, from, to);
        var rule = ScheduleRepeat.Parse(item.Repeat);
        var length = (int)(item.End - item.Start).TotalDays;

        var expected = new List<string>();
        if (rule == null)
        {
            if (item.Start <= to && item.End >= from) expected.Add(D(DateTime.MinValue) + ">" + D(item.Start) + "~" + D(item.End));
        }
        else
        {
            var limit = new[] { to, item.Moves.Count > 0 ? item.Moves.Keys.Max() : to }.Max();
            var originals = new HashSet<DateTime>(NaiveOriginals(item, rule, limit));
            foreach (var original in originals.OrderBy(date => date))
            {
                if (item.Skips.Contains(original)) continue;
                DateTime start;
                if (!item.Moves.TryGetValue(original, out start)) start = original;
                if (start <= to && start.AddDays(length) >= from) expected.Add(D(original) + ">" + D(start) + "~" + D(start.AddDays(length)));
            }
        }

        var got = actual.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        expected = expected.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!got.SequenceEqual(expected))
            Ng(round, "繰り返しの回が素朴な数え方と違う（" + D(from) + "〜" + D(to) + "）",
               Show(item) + "\nあるべき: " + string.Join(" ", expected) + "\n数えた:   " + string.Join(" ", got));
        if (actual.Select(occ => occ.Original).Distinct().Count() != actual.Count)
            Ng(round, "同じ回が2度出る", Show(item) + "\n" + string.Join(" ", got));
        if (actual.Any(occ => !ReferenceEquals(occ.Item, item)))
            Ng(round, "回の Item が元の予定でない", Show(item));
    }

    /// <summary>週ごとに数えた回（カレンダーが週の行を組むときの数え方）が、まとめて数えた回と同じか。</summary>
    private static void WeekSplit(int round)
    {
        var item = Item();
        var first = Day();
        first = first.AddDays(-(int)first.DayOfWeek);
        var weeks = 1 + _random.Next(20);
        var last = first.AddDays(7 * weeks - 1);

        var whole = ScheduleRepeat.Occurrences(item, first, last).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var split = new HashSet<string>();
        for (var w = 0; w < weeks; w++)
            foreach (var occ in ScheduleRepeat.Occurrences(item, first.AddDays(7 * w), first.AddDays(7 * w + 6))) split.Add(Key(occ));
        var joined = split.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!whole.SequenceEqual(joined))
            Ng(round, "週ごとに数えた回と、まとめて数えた回が違う", Show(item) + "\nまとめて: " + string.Join(" ", whole) + "\n週ごと:   " + string.Join(" ", joined));
    }

    // ---------------------------------------------------------------
    // 決まりの読み書き・Prune
    // ---------------------------------------------------------------

    private static void Rules(int round)
    {
        var start = Day();
        var raw = RandomRule(start);
        try
        {
            var rule = ScheduleRepeat.Parse(raw);
            if (rule == null) return;
            var text = rule.ToString();
            var again = ScheduleRepeat.Parse(text);
            if (again == null || again.ToString() != text) Ng(round, "決まりの書き直しで変わる: " + raw + " → " + text + " → " + again, null);
            if (string.IsNullOrEmpty(rule.Describe(start))) Ng(round, "決まりの説明が空: " + raw, null);
        }
        catch (Exception ex)
        {
            Ng(round, "決まりで落ちた: " + raw + " " + ex.GetType().Name + " " + ex.Message, null);
        }
    }

    private static void PruneKeepsMatching(int round)
    {
        var item = Item();
        if (!item.IsRepeat) return;
        var before = item.Clone();
        ScheduleRepeat.Prune(item);
        var rule = ScheduleRepeat.Parse(item.Repeat);
        if (rule == null)
        {
            if (item.Skips.Count > 0 || item.Moves.Count > 0) Ng(round, "読めない決まりで Prune が残した", Show(before));
            return;
        }
        if (item.Skips.Any(date => !rule.Matches(item.Start, date)) || item.Moves.Keys.Any(date => !rule.Matches(item.Start, date)))
            Ng(round, "Prune が回でない日を残した", Show(before) + "\n→ " + Show(item));
        if (before.Skips.Count(date => rule.Matches(before.Start, date)) != item.Skips.Count)
            Ng(round, "Prune が回の日を捨てた", Show(before) + "\n→ " + Show(item));
    }

    // ---------------------------------------------------------------
    // 直す操作（ScheduleBook）を続けて流す
    // ---------------------------------------------------------------

    /// <summary>
    /// でたらめな操作を続けて、毎回: 保存先に書いて読み戻せる・変わらなかった操作は中身を変えない・戻す（Undo）とその前の中身に戻る・
    /// 繰り返しの回を動かすと動かした先に1回だけ出る・回を消すと出なくなる・読み直して同じ予定を探せる、を見る。
    /// </summary>
    private static void BookOperations(int round)
    {
        var book = new ScheduleBook();
        var seed = new List<ScheduleItem>();
        var count = _random.Next(6);
        for (var i = 0; i < count; i++) seed.Add(Item());
        book.Replace(ScheduleFile.Serialize(seed));

        var log = new List<string>();
        for (var step = 0; step < 12; step++)
        {
            var before = book.Serialize();
            var undoBefore = book.UndoCount;
            var item = book.Items.Count > 0 ? book.Items[_random.Next(book.Items.Count)] : null;
            var occs = item != null ? ScheduleRepeat.Occurrences(item, item.Start, item.Start.AddDays(200)) : new List<ScheduleOccurrence>();
            var occ = occs.Count > 0 ? occs[_random.Next(occs.Count)] : null;
            bool changed;
            string what;

            switch (item == null ? 0 : _random.Next(6))
            {
                case 0:
                {
                    var start = Day();
                    var linked = Chance(30);
                    var added = book.Add(start, start.AddDays(_random.Next(-2, 5)), Title(), linked ? @"D:\a.md" : null, linked ? "p1" : null, linked ? "見出し" : null);
                    changed = true;
                    what = "足す " + Show(added);
                    if (added.End < added.Start) Ng(round, "足した予定の終わりが始まりより前", Show(added));
                    break;
                }
                case 1:
                {
                    if (occ == null) goto case 0;
                    var gesture = Pick(ScheduleGesture.Move, ScheduleGesture.ResizeStart, ScheduleGesture.ResizeEnd);
                    var delta = _random.Next(-20, 21);
                    var original = occ.Original;
                    var target = occ.Start.AddDays(delta);
                    what = "つかむ " + gesture + " " + delta + " " + Show(item) + " 回 " + (original.HasValue ? D(original.Value) : "-") + "@" + D(occ.Start);
                    changed = book.Drag(item, original, occ.Start, gesture, delta);
                    if (item.End < item.Start) Ng(round, "つかんだら終わりが始まりより前", what);
                    if (changed && item.IsRepeat && original.HasValue && gesture == ScheduleGesture.Move)
                    {
                        var after = ScheduleRepeat.Occurrences(item, target.AddDays(-50), target.AddDays(50)).Where(o => o.Original == original).ToList();
                        if (after.Count != 1 || after[0].Start != target) Ng(round, "動かした回が、動かした先に1回だけ出ない", what + "\n→ " + Show(item));
                    }
                    break;
                }
                case 2:
                {
                    var original = occ != null ? occ.Original : null;
                    what = "消す " + Show(item) + " 回 " + (original.HasValue ? D(original.Value) : "全部");
                    changed = book.Delete(item, original);
                    if (item.IsRepeat && original.HasValue)
                    {
                        if (ScheduleRepeat.Occurrences(item, original.Value.AddDays(-60), original.Value.AddDays(60)).Any(o => o.Original == original))
                            Ng(round, "消した回が出る", what + "\n→ " + Show(item));
                    }
                    else if (book.Items.Contains(item)) Ng(round, "消した予定が残る", what);
                    break;
                }
                case 3:
                {
                    var repeat = Chance(30) ? null : RandomRule(item.Start);
                    var title = Chance(50) ? item.Title : Title();
                    what = "直す [" + title + "] " + repeat + " " + Show(item);
                    var oldRepeat = item.Repeat;
                    var oldTitle = item.Title;
                    changed = book.Edit(item, title, repeat);
                    // 決まりを変えたときだけ、回でない日の「消した」「動かした」を捨てる（変えていなければ、元からある物はそのまま。数えるときに見ないだけ）
                    var rule = ScheduleRepeat.Parse(item.Repeat);
                    if (item.Repeat != oldRepeat && rule != null
                        && (item.Skips.Any(d => !rule.Matches(item.Start, d)) || item.Moves.Keys.Any(d => !rule.Matches(item.Start, d))))
                        Ng(round, "決まりを直したあと、回でない日の消した・動かしたが残る", what + "\n→ " + Show(item));
                    if (item.IsLinked && item.Title != oldTitle) Ng(round, "結んだ予定の名前が変わった", what + "\n→ " + Show(item));
                    break;
                }
                case 4:
                {
                    what = "戻す";
                    changed = book.Undo();
                    break;
                }
                default:
                {
                    // 外で書き換わった: 書いて読み直し、同じ予定を探せるか
                    var key = ScheduleBook.KeyOf(item);
                    var old = book.Items;
                    book.Replace(book.Serialize());
                    int removed;
                    var added = book.AddedSince(old, out removed);
                    what = "読み直す";
                    var found = book.Find(item, key);
                    var expected = ScheduleFile.Parse(ScheduleFile.Serialize(new List<ScheduleItem> { item }))[0];
                    if (found == null || ScheduleBook.KeyOf(found) != ScheduleBook.KeyOf(expected))
                    {
                        // 書くと形が変わる予定（名前の改行など）は、読み直す前の形では探せない。読み戻した形で探せればよい
                        if (book.Find(null, ScheduleBook.KeyOf(expected)) == null) Ng(round, "読み直したあと、同じ予定を探せない", Show(item));
                    }
                    if (book.UndoCount != 0) Ng(round, "読み直したのに戻す記録が残る", null);
                    changed = false;
                    if (book.Serialize() != before) Ng(round, "書いて読み直したら中身が変わった", before + "\n---\n" + book.Serialize());
                    log.Add(what);
                    continue;
                }
            }
            log.Add(what + (changed ? "" : "（変わらない）"));

            var now = book.Serialize();
            var reparsed = ScheduleFile.Serialize(ScheduleFile.Parse(now));
            if (reparsed != now) Ng(round, "操作のあと、書いて読み戻すと変わる", string.Join("\n", log) + "\n" + now);
            if (!changed && what != "戻す" && now != before) Ng(round, "変わらないと言った操作で中身が変わった", string.Join("\n", log));
            if (changed && what != "戻す")
            {
                if (book.UndoCount != Math.Min(undoBefore + 1, ScheduleBook.UndoLimit)) Ng(round, "戻す記録が積まれない", string.Join("\n", log));
                var copy = book.Serialize();
                if (book.Undo() && book.Serialize() != before) Ng(round, "戻すと前の中身に戻らない", string.Join("\n", log) + "\n前:\n" + before + "\n戻した:\n" + book.Serialize());
                // 戻したのをやり直す代わりに、読み直して続ける（戻す記録は捨てる）
                book.Replace(copy);
            }
        }
    }
}
