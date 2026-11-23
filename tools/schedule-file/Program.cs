using System;
using System.Collections.Generic;
using System.Linq;
using Tsuzuri.Markdown;
using Tsuzuri.Sync;

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

    // Google へ送る形（ScheduleExport・GoogleCalendarFormat。2026-11-23）
    private static void ExportChecks()
    {
        var today = D(2026, 11, 23);
        Func<ScheduleItem, string> sched = item => ScheduleFile.Serialize(new List<ScheduleItem> { item });
        Func<string, List<ExportEvent>> build = text => ScheduleExport.Build(ScheduleFile.Parse(text), today);

        // 繰り返さない予定: 終わりの日が今日以降だけ。終日は終わりを翌日に
        var plain = ScheduleExport.Build(new List<ScheduleItem>
        {
            new ScheduleItem { Start = D(2026, 11, 20), End = D(2026, 11, 22), Title = "過ぎた" },
            new ScheduleItem { Start = D(2026, 11, 22), End = D(2026, 11, 23), Title = "今日まで" },
            new ScheduleItem { Start = D(2026, 11, 30), End = D(2026, 11, 30), Title = "先" },
        }, today);
        Check(plain.Count == 2 && plain[0].Title == "今日まで" && plain[0].AllDay && plain[0].Start == D(2026, 11, 22) && plain[0].End == D(2026, 11, 24)
              && plain[1].End == D(2026, 12, 1), "送る形: 繰り返さない予定 " + string.Join(",", plain.Select(e => e.Title + e.Start.ToString("MMdd") + "-" + e.End.ToString("MMdd"))));

        // 通知のある予定は時刻付き（1日は30分、何日かは最後の日の翌日 0:00 まで）
        var timed = ScheduleExport.Build(new List<ScheduleItem>
        {
            new ScheduleItem { Start = D(2026, 11, 24), End = D(2026, 11, 24), Title = "a", Notify = new TimeSpan(10, 30, 0) },
            new ScheduleItem { Start = D(2026, 11, 24), End = D(2026, 11, 26), Title = "b", Notify = new TimeSpan(9, 0, 0) },
        }, today);
        Check(!timed[0].AllDay && timed[0].Remind && timed[0].Start == D(2026, 11, 24).AddHours(10.5) && timed[0].End == D(2026, 11, 24).AddHours(11)
              && timed[1].Start == D(2026, 11, 24).AddHours(9) && timed[1].End == D(2026, 11, 27), "送る形: 通知のある予定は時刻付き");

        // 夜遅い通知でも、その日の終わり（翌日 0:00）を越えない（越えると Google では翌日に掛かり、今日以降の範囲がずれる）
        var late = ScheduleExport.Build(new List<ScheduleItem> { new ScheduleItem { Start = D(2026, 11, 24), End = D(2026, 11, 24), Title = "夜", Notify = new TimeSpan(23, 45, 0) } }, today);
        Check(late[0].End == D(2026, 11, 25), "送る形: 23:45 の通知は 0:00 まで " + late[0].End);

        // 繰り返し: 1件で送り、消した回・動かした回の元の日は外し、動かした回は別の1件に（動かした先が今日以降のときだけ）
        var weekly = new ScheduleItem { Start = D(2026, 11, 3), End = D(2026, 11, 3), Title = "毎週", Repeat = "FREQ=WEEKLY;BYDAY=TU" };
        weekly.Skips.Add(D(2026, 12, 1));
        weekly.Moves[D(2026, 12, 8)] = D(2026, 12, 9);
        weekly.Moves[D(2026, 11, 10)] = D(2026, 11, 11);   // 過ぎた所へ動かした回は送らない（元の日は外す）
        var series = ScheduleExport.Build(new List<ScheduleItem> { weekly }, today);
        Check(series.Count == 2 && series[0].Rule != null && series[0].Start == D(2026, 11, 3)
              && string.Join(",", series[0].Excluded.Select(d => d.ToString("MMdd"))) == "1110,1201,1208"
              && series[1].Rule == null && series[1].Start == D(2026, 12, 9), "送る形: 繰り返しと動かした回 " + series.Count);

        // 終わりの日が過ぎた繰り返しは送らない。今日に掛かる回があれば送る。読めない決まりは1回目だけ
        var ended = new ScheduleItem { Start = D(2026, 10, 1), End = D(2026, 10, 1), Title = "済", Repeat = "FREQ=DAILY;UNTIL=20261122" };
        var reaching = new ScheduleItem { Start = D(2026, 10, 1), End = D(2026, 10, 3), Title = "掛かる", Repeat = "FREQ=WEEKLY;UNTIL=20261121" };   // 11/19〜11/21 の回まで
        var reachingToday = new ScheduleItem { Start = D(2026, 10, 3), End = D(2026, 10, 5), Title = "今日に掛かる", Repeat = "FREQ=WEEKLY;UNTIL=20261121" };   // 11/21〜11/23
        var odd = new ScheduleItem { Start = D(2026, 11, 25), End = D(2026, 11, 25), Title = "読めない", Repeat = "FREQ=WEEKLY;COUNT=3" };
        var mix = ScheduleExport.Build(new List<ScheduleItem> { ended, reaching, reachingToday, odd }, today);
        Check(string.Join(",", mix.Select(e => e.Title + (e.Rule != null ? "R" : ""))) == "今日に掛かるR,読めない", "送る形: 終わった・掛かる・読めない " + string.Join(",", mix.Select(e => e.Title)));

        // 結んだ見出しは、ファイル名と見出しだけ（フォルダは出さない）
        var linked = ScheduleExport.Build(new List<ScheduleItem>
        {
            new ScheduleItem { Start = D(2026, 11, 24), End = D(2026, 11, 24), Title = "x", Memo = "メモ", LinkFile = @"C:\docs\plan.md", LinkPage = "p1", LinkHeading = "議題" },
        }, today);
        Check(linked[0].Description == "メモ\n\nTsuzuri の見出し: 議題（plan.md）", "送る形: 説明 " + linked[0].Description);

        // Google の JSON
        Func<DateTime, DateTime> jst = local => local.AddHours(-9);
        var allDayJson = GoogleCalendarFormat.EventJson(series[0], "Asia/Tokyo", jst, "t1");
        Check(allDayJson.Contains("\"start\":{\"date\":\"2026-11-03\"}") && allDayJson.Contains("\"end\":{\"date\":\"2026-11-04\"}")
              && allDayJson.Contains("\"RRULE:FREQ=WEEKLY;BYDAY=TU\"") && allDayJson.Contains("\"EXDATE;VALUE=DATE:20261110,20261201,20261208\"")
              && allDayJson.Contains("\"overrides\":[]") && allDayJson.Contains("\"tsuzuriSend\":\"t1\""), "Google の形: 終日の繰り返し " + allDayJson);
        var timedRule = new ExportEvent
        {
            Title = "a\"b\n", Start = D(2026, 11, 24).AddHours(10), End = D(2026, 11, 24).AddHours(10.5), Remind = true,
            Rule = ScheduleRepeat.Parse("FREQ=MONTHLY;BYMONTHDAY=-1;UNTIL=20261231"),
        };
        timedRule.Excluded.Add(D(2026, 11, 30).AddHours(10));
        var timedJson = GoogleCalendarFormat.EventJson(timedRule, "Asia/Tokyo", jst, "t1");
        Check(timedJson.Contains("\"summary\":\"a\\\"b\\n\"") && timedJson.Contains("\"dateTime\":\"2026-11-24T10:00:00\",\"timeZone\":\"Asia/Tokyo\"")
              && timedJson.Contains("\"RRULE:FREQ=MONTHLY;BYMONTHDAY=-1;UNTIL=20261231T145959Z\"") && timedJson.Contains("\"EXDATE;TZID=Asia/Tokyo:20261130T100000\"")
              && timedJson.Contains("{\"method\":\"email\",\"minutes\":0}"), "Google の形: 時刻付きの繰り返し " + timedJson);

        // バッチ
        var body = GoogleCalendarFormat.BatchBody(new List<GoogleCalendarFormat.BatchPart>
        {
            new GoogleCalendarFormat.BatchPart { Method = "POST", Path = "/calendar/v3/calendars/c%40g/events", Body = "{}" },
            new GoogleCalendarFormat.BatchPart { Method = "DELETE", Path = "/calendar/v3/calendars/c%40g/events/e1" },
        }, "b1");
        Check(body == "--b1\r\nContent-Type: application/http\r\nContent-ID: <item0>\r\n\r\nPOST /calendar/v3/calendars/c%40g/events HTTP/1.1\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{}\r\n"
                   + "--b1\r\nContent-Type: application/http\r\nContent-ID: <item1>\r\n\r\nDELETE /calendar/v3/calendars/c%40g/events/e1 HTTP/1.1\r\n\r\n--b1--\r\n", "バッチの本文");
        var answer = "--batch_x\r\nContent-Type: application/http\r\nContent-ID: <response-item1>\r\n\r\nHTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n\r\n"
                   + "--batch_x\r\nContent-Type: application/http\r\nContent-ID: <response-item0>\r\n\r\nHTTP/1.1 200 OK\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{\n \"id\": \"abc\"\n}\r\n"
                   + "--batch_x--\r\n";
        var parsed = GoogleCalendarFormat.ParseBatch("multipart/mixed; boundary=\"batch_x\"", answer);
        Check(parsed.Count == 2 && parsed[0].Status == 200 && parsed[0].Body.Contains("\"abc\"") && parsed[1].Status == 204 && parsed[1].Body == string.Empty,
              "バッチの返事 " + string.Join(",", parsed.Select(pair => pair.Key + ":" + pair.Value.Status)));
        Check(GoogleCalendarFormat.ParseBatch("text/html", answer).Count == 0, "バッチの返事: boundary が無い");
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
        Check(show(occ("FREQ=MONTHLY;BYMONTHDAY=-1", D(2026, 1, 31), D(2026, 1, 1), D(2026, 5, 31))) == "01-31,02-28,03-31,04-30,05-31", "毎月末日");
        Check(show(occ("FREQ=MONTHLY;INTERVAL=2;BYMONTHDAY=-1", D(2027, 12, 31), D(2027, 12, 1), D(2028, 4, 30))) == "12-31,02-29,04-30", "2か月ごと末日（うるう年）");
        Check(ScheduleRepeat.Parse("FREQ=MONTHLY;BYMONTHDAY=-1").ToString() == "FREQ=MONTHLY;BYMONTHDAY=-1" && ScheduleRepeat.Parse("FREQ=MONTHLY;BYMONTHDAY=-1").Describe(D(2026, 1, 31)) == "毎月 末日", "末日の読み書き・説明");
        Check(ScheduleRepeat.Parse("FREQ=MONTHLY;BYMONTHDAY=-2") == null && ScheduleRepeat.Parse("FREQ=MONTHLY;BYMONTHDAY=0") == null, "末日のほかの負の日付・0 は読めない");
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

        // 通知を出す回（2026-11-22。Windows の通知）
        var alarmItems = ScheduleFile.Parse(
            "## 2026-11-24 定例\n<!-- notify: 10:00 -->\n<!-- repeat: FREQ=WEEKLY;BYDAY=TU -->\n<!-- skip: 2026-12-01 -->\n<!-- move: 2026-12-08>2026-12-09 -->\n" +
            "## 2026-11-25〜2026-11-27 出張\n<!-- notify: 7:30 -->\n" +
            "## 2026-11-25 通知なし\n" +
            "## 2026-11-26 締め\n<!-- notify: 23:59 -->\n");
        Func<DateTime, DateTime, string> alarms = (after, until) => string.Join(",", ScheduleRepeat.Alarms(alarmItems, after, until)
            .Select(alarm => alarm.At.ToString("MM-dd HH:mm") + " " + alarm.Occurrence.Item.Title));
        Check(alarms(D(2026, 11, 20), D(2026, 12, 16)) == "11-24 10:00 定例,11-25 07:30 出張,11-26 23:59 締め,12-09 10:00 定例,12-15 10:00 定例",
              "通知の回（消した回・動かした回・何日かは始まりの日だけ）: " + alarms(D(2026, 11, 20), D(2026, 12, 16)));
        Check(alarms(D(2026, 11, 24).AddHours(10), D(2026, 11, 25).AddHours(7.5)) == "11-25 07:30 出張", "after は含まず until は含む: " + alarms(D(2026, 11, 24).AddHours(10), D(2026, 11, 25).AddHours(7.5)));
        Check(alarms(D(2026, 11, 26), D(2026, 11, 27).AddHours(12)) == "11-26 23:59 締め", "何日かの予定の2日目からは鳴らない: " + alarms(D(2026, 11, 26), D(2026, 11, 27).AddHours(12)));
        Check(alarms(D(2026, 11, 24).AddHours(9.99), D(2026, 11, 24).AddHours(10)) == "11-24 10:00 定例", "ちょうどの時刻");
        Check(alarms(D(2026, 11, 24).AddHours(11), D(2026, 11, 24).AddHours(10)) == "", "時刻が戻ったら何も出さない");

        // 繰り返しの回を動かす: この回だけ・これ以降・全部（2026-11-23）
        Func<ScheduleItem, DateTime, DateTime, string> days = (item, from, to) => string.Join(",",
            ScheduleRepeat.Occurrences(item, from, to).OrderBy(o => o.Start).Select(o => o.Start.ToString("MM-dd")));
        Func<ScheduleBook> sundays = () =>
        {
            var book = new ScheduleBook();
            book.Replace(ScheduleFile.Serialize(new List<ScheduleItem> { new ScheduleItem { Start = D(2026, 11, 22), End = D(2026, 11, 22), Title = "日曜", Repeat = "FREQ=WEEKLY" } }));
            return book;
        };
        var one = sundays();
        one.Drag(one.Items[0], D(2026, 11, 22), D(2026, 11, 22), ScheduleGesture.Move, -7, ScheduleMoveScope.One);
        Check(days(one.Items[0], D(2026, 11, 1), D(2026, 12, 13)) == "11-15,11-29,12-06,12-13", "この回だけ: " + days(one.Items[0], D(2026, 11, 1), D(2026, 12, 13)));
        var all = sundays();
        all.Drag(all.Items[0], D(2026, 11, 22), D(2026, 11, 22), ScheduleGesture.Move, -7, ScheduleMoveScope.All);
        Check(all.Items.Count == 1 && all.Items[0].Start == D(2026, 11, 15) && all.Items[0].Moves.Count == 0, "全部（1回目）: 始まりの日が 11/15 になる");
        Check(days(all.Items[0], D(2026, 11, 1), D(2026, 12, 13)) == "11-15,11-22,11-29,12-06,12-13", "全部（1回目）: " + days(all.Items[0], D(2026, 11, 1), D(2026, 12, 13)));
        var after = sundays();
        after.Drag(after.Items[0], D(2026, 12, 6), D(2026, 12, 6), ScheduleGesture.Move, 1, ScheduleMoveScope.Following);
        Check(after.Items.Count == 2 && after.Items[0].Repeat == "FREQ=WEEKLY;UNTIL=20261205" && after.Items[1].Start == D(2026, 12, 7), "これ以降: 2つに分かれる " + after.Serialize());
        Check(days(after.Items[0], D(2026, 11, 1), D(2026, 12, 31)) + "|" + days(after.Items[1], D(2026, 11, 1), D(2026, 12, 31)) == "11-22,11-29|12-07,12-14,12-21,12-28",
              "これ以降: " + days(after.Items[0], D(2026, 11, 1), D(2026, 12, 31)) + "|" + days(after.Items[1], D(2026, 11, 1), D(2026, 12, 31)));
        var shift = sundays();
        shift.Drag(shift.Items[0], D(2026, 11, 29), D(2026, 11, 29), ScheduleGesture.Move, 2, ScheduleMoveScope.All);
        Check(days(shift.Items[0], D(2026, 11, 1), D(2026, 12, 13)) == "11-24,12-01,12-08" && shift.Items[0].Start == D(2026, 11, 24), "全部（2回目を火曜へ）: " + days(shift.Items[0], D(2026, 11, 1), D(2026, 12, 13)));
        var twoWeeks = new ScheduleBook();
        twoWeeks.Replace(ScheduleFile.Serialize(new List<ScheduleItem> { new ScheduleItem { Start = D(2026, 11, 23), End = D(2026, 11, 23), Title = "隔週", Repeat = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,SA;WKST=SU" } }));
        twoWeeks.Drag(twoWeeks.Items[0], D(2026, 11, 28), D(2026, 11, 28), ScheduleGesture.Move, 1, ScheduleMoveScope.All);
        Check(twoWeeks.LastNote != null && twoWeeks.Items[0].Start == D(2026, 11, 23) && twoWeeks.Items[0].Moves.Count == 1, "2週ごとの土曜を日曜へ全部は、この回だけにする");
        Func<ScheduleBook> monthEnds = () =>
        {
            var book = new ScheduleBook();
            book.Replace(ScheduleFile.Serialize(new List<ScheduleItem> { new ScheduleItem { Start = D(2026, 11, 30), End = D(2026, 11, 30), Title = "末日", Repeat = "FREQ=MONTHLY;BYMONTHDAY=-1" } }));
            return book;
        };
        var endToEnd = monthEnds();
        endToEnd.Drag(endToEnd.Items[0], D(2026, 12, 31), D(2026, 12, 31), ScheduleGesture.Move, 31, ScheduleMoveScope.All);
        Check(endToEnd.Items[0].Repeat == "FREQ=MONTHLY;BYMONTHDAY=-1" && days(endToEnd.Items[0], D(2026, 11, 1), D(2027, 2, 28)) == "12-31,01-31,02-28",
              "全部（末日を次の末日へ）: 末日のまま " + endToEnd.Serialize());
        var endToDay = monthEnds();
        endToDay.Drag(endToDay.Items[0], D(2026, 12, 31), D(2026, 12, 31), ScheduleGesture.Move, -1, ScheduleMoveScope.All);
        Check(endToDay.Items[0].Repeat == "FREQ=MONTHLY;BYMONTHDAY=30" && days(endToDay.Items[0], D(2026, 11, 1), D(2027, 1, 31)) == "11-30,12-30,01-30",
              "全部（末日を30日へ）: 毎月30日 " + endToDay.Serialize());
        // 末日の消した回・動かした回・終わりの日は「何回目か」で移す（日数でずらすと月末に乗らず、消した回が戻っていた）
        Func<string, string, ScheduleBook> monthEndWith = (repeat, extra) =>
        {
            var item = new ScheduleItem { Start = D(2026, 11, 30), End = D(2026, 11, 30), Title = "末日", Repeat = repeat };
            var book = new ScheduleBook();
            book.Replace(ScheduleFile.Serialize(new List<ScheduleItem> { item }).Replace("<!-- repeat: " + repeat + " -->", "<!-- repeat: " + repeat + " -->\n" + extra));
            return book;
        };
        var skipA = monthEndWith("FREQ=MONTHLY;BYMONTHDAY=-1", "<!-- skip: 2027-03-31 -->");
        skipA.Drag(skipA.Items[0], D(2027, 1, 31), D(2027, 1, 31), ScheduleGesture.Move, 28, ScheduleMoveScope.All);
        Check(days(skipA.Items[0], D(2026, 11, 1), D(2027, 5, 31)) == "12-31,01-31,02-28,03-31,05-31", "全部（末日・消した回。ひと月ずらしたので 3/31 の消した回は 4/30 へ）: " + days(skipA.Items[0], D(2026, 11, 1), D(2027, 5, 31)) + " " + skipA.Serialize());
        var untilB = monthEndWith("FREQ=MONTHLY;BYMONTHDAY=-1;UNTIL=20270331", "");
        untilB.Drag(untilB.Items[0], D(2027, 1, 31), D(2027, 1, 31), ScheduleGesture.Move, 28, ScheduleMoveScope.All);
        Check(days(untilB.Items[0], D(2026, 11, 1), D(2027, 6, 30)) == "12-31,01-31,02-28,03-31,04-30", "全部（末日・終わりの日）: " + days(untilB.Items[0], D(2026, 11, 1), D(2027, 6, 30)) + " " + untilB.Serialize());
        var skipC = monthEndWith("FREQ=MONTHLY;BYMONTHDAY=-1", "<!-- skip: 2026-11-30 2027-04-30 -->");
        skipC.Drag(skipC.Items[0], D(2026, 12, 31), D(2026, 12, 31), ScheduleGesture.Move, -1, ScheduleMoveScope.All);
        Check(days(skipC.Items[0], D(2026, 11, 1), D(2027, 5, 31)) == "12-30,01-30,03-30,05-30", "全部（末日を30日へ・消した回）: " + days(skipC.Items[0], D(2026, 11, 1), D(2027, 5, 31)) + " " + skipC.Serialize());
        var moveD = monthEndWith("FREQ=MONTHLY;BYMONTHDAY=-1", "<!-- move: 2026-11-30>2026-11-26 -->");
        moveD.Drag(moveD.Items[0], D(2026, 12, 31), D(2026, 12, 31), ScheduleGesture.Move, -1, ScheduleMoveScope.All);
        Check(days(moveD.Items[0], D(2026, 11, 1), D(2027, 1, 31)) == "11-26,12-30,01-30", "全部（1回目を動かしてあった）: " + days(moveD.Items[0], D(2026, 11, 1), D(2027, 1, 31)) + " " + moveD.Serialize());

        // 1回目の移し先が2回目と重ならない（1/31 始まりの末日で 2/28 の回を 3/31 へ。1/31+31日は 3/3 なので、日数で決めると3月に2つ重なっていた）
        Func<string, DateTime, ScheduleBook> oneRepeat = (repeat, start) =>
        {
            var book = new ScheduleBook();
            book.Replace(ScheduleFile.Serialize(new List<ScheduleItem> { new ScheduleItem { Start = start, End = start, Title = "x", Repeat = repeat } }));
            return book;
        };
        var x1 = oneRepeat("FREQ=MONTHLY;BYMONTHDAY=-1", D(2027, 1, 31));
        x1.Drag(x1.Items[0], D(2027, 2, 28), D(2027, 2, 28), ScheduleGesture.Move, 31, ScheduleMoveScope.All);
        Check(days(x1.Items[0], D(2027, 1, 1), D(2027, 4, 30)) == "02-28,03-31,04-30", "全部（1/31 始まりの末日、2/28 を 3/31 へ）: " + days(x1.Items[0], D(2027, 1, 1), D(2027, 4, 30)) + " " + x1.Serialize());
        var x2 = monthEndWith("FREQ=MONTHLY;BYMONTHDAY=-1", "<!-- skip: 2026-11-30 -->");
        x2.Items[0].Start = x2.Items[0].End = D(2027, 1, 31);
        x2.Items[0].Skips = new List<DateTime> { D(2027, 1, 31) };
        x2.Drag(x2.Items[0], D(2027, 2, 28), D(2027, 2, 28), ScheduleGesture.Move, 31, ScheduleMoveScope.All);
        Check(days(x2.Items[0], D(2027, 1, 1), D(2027, 4, 30)) == "03-31,04-30", "全部（消した1回目は消したまま、つかんだ回は出る）: " + days(x2.Items[0], D(2027, 1, 1), D(2027, 4, 30)) + " " + x2.Serialize());
        // 20年より前に始まった予定・20年より先の終わりの日（前は回を20年分だけ並べていた）
        var y1 = oneRepeat("FREQ=YEARLY", D(2000, 5, 1));
        y1.Drag(y1.Items[0], D(2026, 5, 1), D(2026, 5, 1), ScheduleGesture.Move, 1, ScheduleMoveScope.All);
        Check(y1.LastNote == null && y1.Items[0].Start == D(2000, 5, 2) && days(y1.Items[0], D(2026, 1, 1), D(2026, 12, 31)) == "05-02", "全部（2000年からの毎年）: " + y1.LastNote + " " + y1.Serialize());
        var y2 = oneRepeat("FREQ=MONTHLY;BYMONTHDAY=10;UNTIL=20600101", D(2026, 11, 10));
        y2.Drag(y2.Items[0], D(2026, 12, 10), D(2026, 12, 10), ScheduleGesture.Move, 1, ScheduleMoveScope.All);
        Check(y2.Items[0].Repeat == "FREQ=MONTHLY;BYMONTHDAY=11;UNTIL=20591211", "全部（遠い終わりの日）: " + y2.Items[0].Repeat);

        var dayTwenty = new ScheduleBook();
        dayTwenty.Replace(ScheduleFile.Serialize(new List<ScheduleItem> { new ScheduleItem { Start = D(2026, 1, 20), End = D(2026, 1, 20), Title = "20日", Repeat = "FREQ=MONTHLY" } }));
        dayTwenty.Drag(dayTwenty.Items[0], D(2026, 2, 20), D(2026, 2, 20), ScheduleGesture.Move, 13, ScheduleMoveScope.All);
        Check(days(dayTwenty.Items[0], D(2026, 1, 1), D(2026, 4, 30)) == "02-05,03-05,04-05", "全部（2/20 の回を 3/5 へ）: 1回目も 2/5 に。余分な回を出さない " + dayTwenty.Serialize());

        // 名前の改行
        Check(ScheduleFile.OneLine(" a\r\n b \n") == "a b", "名前の改行");

        ExportChecks();

        Console.WriteLine(_ng == 0 ? "全部 OK" : "NG: " + _ng + " 件");
        return _ng == 0 ? 0 : 1;
    }
}
