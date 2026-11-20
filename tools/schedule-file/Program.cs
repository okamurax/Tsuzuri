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
            if (a[i].Start != b[i].Start || a[i].End != b[i].End || a[i].Title != b[i].Title || a[i].Memo != b[i].Memo)
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

        // 崩れた行・手で書いた形
        var hand = "# 予定\nメモの前の行\n## 2026-11-03 祝日\n## 2026-11-05 ~ 2026-11-07 研修\n本文\n## 2026-11-09〜2026-11-08 逆\n## 2026-13-01 月が無い\n## 見出しだけ\n";
        var read = ScheduleFile.Parse(hand);
        Check(read.Count == 3, "手で書いた形の数: " + read.Count);
        Check(read.Count > 1 && read[1].Start == D(2026, 11, 5) && read[1].End == D(2026, 11, 7) && read[1].Title == "研修", "~ の範囲");
        Check(read.Count > 2 && read[2].End == read[2].Start, "終わりが前なら1日にする");
        Check(read.Count > 2 && read[2].Memo.Contains("## 2026-13-01 月が無い") && read[2].Memo.Contains("## 見出しだけ"), "読めない見出しはメモに残す");
        Check(ScheduleFile.Parse("").Count == 0 && ScheduleFile.Parse(null).Count == 0, "空");
        Check(ScheduleFile.Parse("a\r\n## 2026-01-02 x\r\nm\r\n")[0].Memo == "m", "CRLF");

        // 名前の改行
        Check(ScheduleFile.OneLine(" a\r\n b \n") == "a b", "名前の改行");

        Console.WriteLine(_ng == 0 ? "全部 OK" : "NG: " + _ng + " 件");
        return _ng == 0 ? 0 : 1;
    }
}
