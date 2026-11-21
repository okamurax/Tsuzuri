using System;
using System.Collections.Generic;
using System.Linq;
using Tsuzuri.Markdown;

// カレンダーの予定の保存先（ScheduleFile）の確かめ。tools/schedule-file.sh から動かす。
internal static class Program
{
    private static int _ng;

    private static void Check(bool ok, string what)
    {
        if (ok) return;
        _ng++;
        Console.WriteLine("NG: " + what);
    }

    private static DateTime D(int y, int m, int d) { return new DateTime(y, m, d); }

    private static bool Same(List<ScheduleItem> a, List<ScheduleItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Start != b[i].Start || a[i].End != b[i].End || a[i].Title != b[i].Title || a[i].Memo != b[i].Memo
                || a[i].LinkFile != b[i].LinkFile || a[i].LinkPage != b[i].LinkPage || a[i].LinkHeading != b[i].LinkHeading
                || a[i].Repeat != b[i].Repeat || !a[i].Skips.SequenceEqual(b[i].Skips) || !a[i].Moves.SequenceEqual(b[i].Moves))
            {
                Console.WriteLine("  違い " + i + ": [" + a[i].Title + "|" + a[i].Memo + "] [" + b[i].Title + "|" + b[i].Memo + "]");
                return false;
            }
        }
        return true;
    }

    private static int Main()
    {
        // 読み書きで変わらない
        var items = new List<ScheduleItem>
        {
            new ScheduleItem { Start = D(2026, 11, 25), End = D(2026, 11, 25), Title = "14:00- 定例" },
            new ScheduleItem { Start = D(2026, 11, 21), End = D(2026, 11, 25), Title = "出張（大阪）", Memo = "新幹線 8:00\n\n宿: xxxx" },
            new ScheduleItem { Start = D(2026, 11, 21), End = D(2026, 11, 21), Title = "10:00 A社打合せ", Memo = "## 議題\n- 見積\n\\## そのまま" },
            new ScheduleItem { Start = D(2026, 12, 1), End = D(2026, 12, 1), Title = string.Empty },
            new ScheduleItem { Start = D(2026, 12, 2), End = D(2026, 12, 4), Title = "見積 | 第2版", Memo = "<!-- link: x | y | z -->",
                               LinkFile = @"D:\案件\見積.md", LinkPage = "p3", LinkHeading = "見積 | 第2版" },
            new ScheduleItem { Start = D(2026, 12, 5), End = D(2026, 12, 5), Title = "本体の見出し", LinkFile = @"\\Mac\Home\a.md", LinkPage = "", LinkHeading = "本体の見出し" },
            new ScheduleItem { Start = D(2026, 11, 24), End = D(2026, 11, 24), Title = "10:00 定例", Repeat = "FREQ=WEEKLY;BYDAY=TU",
                               Skips = new List<DateTime> { D(2026, 12, 29) },
                               Moves = new SortedDictionary<DateTime, DateTime> { { D(2026, 12, 8), D(2026, 12, 9) } }, Memo = "<!-- repeat: FREQ=DAILY -->" },
            new ScheduleItem { Start = D(2026, 12, 10), End = D(2026, 12, 11), Title = "月の締め", Repeat = "FREQ=MONTHLY;BYDAY=2TH",
                               LinkFile = @"D:\a.md", LinkPage = "p1", LinkHeading = "締め" },
        };
        var text = ScheduleFile.Serialize(items);
        var back = ScheduleFile.Parse(text);
        var sorted = items.OrderBy(item => item.Start).ToList();
        Check(Same(back, sorted), "読み書きで変わる\n" + text);
        Check(ScheduleFile.Serialize(back) == text, "2回目の書き出しが違う");
        Check(text.StartsWith(ScheduleFile.Marker + "\n# 予定\n", StringComparison.Ordinal), "頭の印・題が無い");
        Check(text.Contains("## 2026-11-21〜2026-11-25 出張（大阪）\n"), "何日かにまたがる予定の見出し");
        Check(text.Contains("\n\\## 議題\n"), "メモの中の ## に \\ が付かない");
        Check(back[0].Title == "出張（大阪）" && back[1].Title == "10:00 A社打合せ", "同じ日の並びが変わった");

        Check(text.Contains("## 2026-12-02〜2026-12-04 見積 | 第2版\n<!-- link: D:\\案件\\見積.md | p3 | 見積 | 第2版 -->\n"), "見出しの場所の行");
        var linked = back.FirstOrDefault(item => item.LinkPage == "p3");
        Check(linked != null && linked.Memo == "<!-- link: x | y | z -->", "メモの中の link の形の行を、見出しの場所として読んだ");
        var root = back.FirstOrDefault(item => item.Title == "本体の見出し");
        Check(root != null && root.IsLinked && root.LinkPage == "", "ルートのページの見出しの場所");
        Check(!back[0].IsLinked, "直接打った予定に見出しの場所が付いた");

        // 崩れた行・手で書いた形
        var hand = "# 予定\nメモの前の行\n## 2026-11-03 祝日\n## 2026-11-05 ~ 2026-11-07 研修\n本文\n## 2026-11-09〜2026-11-08 逆\n## 2026-13-01 月が無い\n## 見出しだけ\n";
        var read = ScheduleFile.Parse(hand);
        Check(read.Count == 3, "手で書いた形の数: " + read.Count);
        Check(read.Count > 1 && read[1].Start == D(2026, 11, 5) && read[1].End == D(2026, 11, 7) && read[1].Title == "研修", "~ の範囲");
        Check(read.Count > 2 && read[2].End == read[2].Start, "終わりが前なら1日にする");
        Check(read.Count > 2 && read[2].Memo.Contains("## 2026-13-01 月が無い") && read[2].Memo.Contains("## 見出しだけ"), "読めない見出しはメモに残す");
        Check(ScheduleFile.Parse("").Count == 0 && ScheduleFile.Parse(null).Count == 0, "空");
        Check(ScheduleFile.Parse("a\r\n## 2026-01-02 x\r\nm\r\n")[0].Memo == "m", "CRLF");

        Check(text.Contains("## 2026-11-24 10:00 定例\n<!-- repeat: FREQ=WEEKLY;BYDAY=TU -->\n<!-- skip: 2026-12-29 -->\n<!-- move: 2026-12-08>2026-12-09 -->\n"), "繰り返しの行");
        var weekly = back.First(item => item.Title == "10:00 定例");
        Check(weekly.Memo == "<!-- repeat: FREQ=DAILY -->", "メモの中の repeat の形の行を、決まりとして読んだ");
        Check(back.First(item => item.Title == "月の締め").IsLinked && back.First(item => item.Title == "月の締め").Repeat == "FREQ=MONTHLY;BYDAY=2TH", "link と repeat の両方");
        var reorder = ScheduleFile.Parse("## 2026-01-01 x\n<!-- skip: 2026-01-08 -->\n<!-- repeat: FREQ=WEEKLY -->\nmemo\n<!-- move: 2026-01-15>2026-01-16 -->\n")[0];
        Check(reorder.Repeat == "FREQ=WEEKLY" && reorder.Skips.Count == 1 && reorder.Moves.Count == 0 && reorder.Memo.EndsWith("-->"), "meta の行の順・メモのあとは meta にしない");

        // 繰り返しの回
        Func<string, DateTime, DateTime, DateTime, List<DateTime>> occ = (rule, start, from, to) =>
            ScheduleRepeat.Occurrences(new ScheduleItem { Start = start, End = start, Repeat = rule }, from, to).Select(o => o.Start).OrderBy(d => d).ToList();
        Func<List<DateTime>, string> show = list => string.Join(",", list.Select(d => d.ToString("MM-dd")));

        Check(show(occ("FREQ=WEEKLY;BYDAY=TU", D(2026, 11, 24), D(2026, 11, 1), D(2026, 12, 31))) == "11-24,12-01,12-08,12-15,12-22,12-29", "毎週火");
        Check(show(occ("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", D(2026, 11, 20), D(2026, 11, 20), D(2026, 11, 27))) == "11-20,11-23,11-24,11-25,11-26,11-27", "平日");
        Check(show(occ("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;WKST=SU", D(2026, 11, 23), D(2026, 11, 22), D(2026, 12, 19))) == "11-23,11-25,12-07,12-09", "2週ごと 月・水");
        Check(show(occ("FREQ=DAILY;INTERVAL=3", D(2026, 11, 1), D(2026, 11, 1), D(2026, 11, 10))) == "11-01,11-04,11-07,11-10", "3日ごと");
        Check(show(occ("FREQ=MONTHLY;BYMONTHDAY=31", D(2026, 1, 31), D(2026, 1, 1), D(2026, 7, 31))) == "01-31,03-31,05-31,07-31", "毎月31日（無い月は飛ばす）");
        Check(show(occ("FREQ=MONTHLY", D(2026, 1, 15), D(2026, 1, 1), D(2026, 3, 31))) == "01-15,02-15,03-15", "毎月（日付は始まりの日）");
        Check(show(occ("FREQ=MONTHLY;BYDAY=2TU", D(2026, 11, 10), D(2026, 11, 1), D(2027, 1, 31))) == "11-10,12-08,01-12", "毎月第2火曜");
        Check(show(occ("FREQ=MONTHLY;BYDAY=-1FR", D(2026, 11, 27), D(2026, 11, 1), D(2027, 1, 31))) == "11-27,12-25,01-29", "毎月最終金曜");
        Check(show(occ("FREQ=MONTHLY;INTERVAL=2;BYMONTHDAY=5", D(2026, 1, 5), D(2026, 1, 1), D(2026, 6, 30))) == "01-05,03-05,05-05", "2か月ごと");
        Check(show(occ("FREQ=YEARLY", D(2024, 2, 29), D(2024, 1, 1), D(2028, 12, 31))).Split(',').Length == 2, "毎年2/29 はうるう年だけ");
        Check(show(occ("FREQ=WEEKLY;BYDAY=TU;UNTIL=20261208", D(2026, 11, 24), D(2026, 11, 1), D(2026, 12, 31))) == "11-24,12-01,12-08", "終わりの日を含む");
        Check(show(occ("FREQ=WEEKLY;UNTIL=20261208T145959Z", D(2026, 11, 24), D(2026, 11, 1), D(2026, 12, 31))) == "11-24,12-01,12-08", "UNTIL の時刻付き・BYDAY 無し");
        Check(show(occ("FREQ=WEEKLY;COUNT=3", D(2026, 11, 24), D(2026, 11, 1), D(2026, 12, 31))) == "11-24", "読めない決まりは1回だけ");
        Check(occ("FREQ=DAILY", D(2026, 11, 24), D(2026, 11, 1), D(2026, 11, 23)).Count == 0, "始まりより前は出さない");

        // 消した回・動かした回・何日かの回
        var series = new ScheduleItem { Start = D(2026, 11, 24), End = D(2026, 11, 25), Repeat = "FREQ=WEEKLY;BYDAY=TU",
                                        Skips = new List<DateTime> { D(2026, 12, 1) },
                                        Moves = new SortedDictionary<DateTime, DateTime> { { D(2026, 12, 8), D(2026, 12, 13) } } };
        var week = ScheduleRepeat.Occurrences(series, D(2026, 12, 13), D(2026, 12, 19)).OrderBy(o => o.Start).ToList();
        Check(week.Count == 2 && week[0].Start == D(2026, 12, 13) && week[0].Original == D(2026, 12, 8) && week[0].End == D(2026, 12, 14)
              && week[1].Start == D(2026, 12, 15), "動かした回は動かした先の週に出る");
        Check(ScheduleRepeat.Occurrences(series, D(2026, 11, 29), D(2026, 12, 12)).Count == 0, "消した回・動かした元は出さない");
        Check(ScheduleRepeat.Occurrences(series, D(2026, 11, 25), D(2026, 11, 25)).Count == 1, "前の日から掛かる回");
        var single = ScheduleRepeat.Occurrences(new ScheduleItem { Start = D(2026, 1, 1), End = D(2026, 1, 3) }, D(2026, 1, 2), D(2026, 1, 2));
        Check(single.Count == 1 && single[0].Original == null, "繰り返さない予定");

        // 書く形と説明
        var parsed = ScheduleRepeat.Parse("freq=weekly;interval=2;byday=we,mo");
        Check(parsed != null && parsed.ToString() == "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;WKST=SU", "書く形: " + parsed);
        Check(ScheduleRepeat.Parse("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR").Describe(D(2026, 1, 1)) == "平日（月〜金）", "平日の説明");
        Check(ScheduleRepeat.Parse("FREQ=MONTHLY;BYDAY=-1FR;UNTIL=20270331").Describe(D(2026, 1, 1)) == "毎月 最終金曜（2027/3/31 まで）", "最終金曜の説明");
        Check(ScheduleRepeat.Parse("FREQ=HOURLY") == null && ScheduleRepeat.Parse("FREQ=MONTHLY;BYDAY=TU") == null && ScheduleRepeat.Parse("FREQ=WEEKLY;BYMONTHDAY=3") == null, "読めない決まり");

        // 決まりが変わったら、合わない「消した」「動かした」を捨てる
        var prune = series.Clone();
        prune.Repeat = "FREQ=WEEKLY;BYDAY=WE";
        ScheduleRepeat.Prune(prune);
        Check(prune.Skips.Count == 0 && prune.Moves.Count == 0, "Prune");

        // 名前が範囲の形で始まる1日の予定（2026-11-21 のレビュー。読み戻すと何日かの予定になっていた）
        var rangeLike = new List<ScheduleItem>
        {
            new ScheduleItem { Start = D(2026, 1, 1), End = D(2026, 1, 1), Title = "〜2026-01-05 x" },
            new ScheduleItem { Start = D(2026, 1, 2), End = D(2026, 1, 2), Title = "~ 2026-01-09" },
            new ScheduleItem { Start = D(2026, 1, 3), End = D(2026, 1, 3), Title = "2026-01-04 から" },
        };
        Check(Same(ScheduleFile.Parse(ScheduleFile.Serialize(rangeLike)), rangeLike), "範囲の形で始まる名前\n" + ScheduleFile.Serialize(rangeLike));

        // 通知の時刻（2026-11-21）
        var notified = ScheduleFile.Parse("## 2026-01-04 定例\n<!-- notify: 9:05 -->\n<!-- repeat: FREQ=WEEKLY;BYDAY=SU -->\nメモ\n");
        Check(notified.Count == 1 && notified[0].Notify == new TimeSpan(9, 5, 0) && notified[0].IsRepeat && notified[0].Memo == "メモ", "通知の行を読む");
        Check(ScheduleFile.Serialize(notified).Contains("## 2026-01-04 定例\n<!-- notify: 09:05 -->\n<!-- repeat: FREQ=WEEKLY;BYDAY=SU -->\n"), "通知の行を書く\n" + ScheduleFile.Serialize(notified));
        var badNotify = ScheduleFile.Parse("## 2026-01-04 x\n<!-- notify: 24:00 -->\n");
        Check(badNotify[0].Notify == null && badNotify[0].Memo == "<!-- notify: 24:00 -->", "読めない時刻はメモのまま");
        foreach (var pair in new[] { Tuple.Create("10:00", "10:00"), Tuple.Create("9:30", "09:30"), Tuple.Create("10", "10:00"), Tuple.Create("1030", "10:30"),
                                     Tuple.Create("930", "09:30"), Tuple.Create("10時", "10:00"), Tuple.Create("10時半", "10:30"), Tuple.Create("10時15分", "10:15"),
                                     Tuple.Create("１０：００", "10:00"), Tuple.Create("٣", ""), Tuple.Create("10：00", "10:00"), Tuple.Create("24:00", ""), Tuple.Create("9:60", ""), Tuple.Create("x", ""), Tuple.Create("", "") })
        {
            TimeSpan time;
            var ok = ScheduleFile.TryReadNotify(pair.Item1, out time);
            Check(pair.Item2.Length > 0 ? ok && ScheduleFile.NotifyText(time) == pair.Item2 : !ok, "時刻の欄: " + pair.Item1 + " → " + (ok ? ScheduleFile.NotifyText(time) : "読めない"));
        }

        // 名前の改行
        Check(ScheduleFile.OneLine(" a\r\n b \n") == "a b", "名前の改行");

        Console.WriteLine(_ng == 0 ? "全部 OK" : "NG: " + _ng + " 件");
        return _ng == 0 ? 0 : 1;
    }
}
