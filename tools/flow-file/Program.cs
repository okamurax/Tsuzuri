using System;
using System.Linq;
using Tsuzuri.Markdown;

// フローのテキスト（FlowFile）の確かめ（2026-11-17）。tools/flow-file.sh から動かす。
internal static class Program
{
    private static int _failed;

    private static void Expect(bool ok, string name)
    {
        Console.WriteLine((ok ? "OK  " : "NG  ") + name);
        if (!ok) _failed++;
    }

    private static int Main()
    {
        var text = string.Join("\n", new[]
        {
            "# ビルドの流れ",
            "",
            "説明の行",
            "",
            "## [Claude] 原因を調べる",
            "<!-- 120,40 -->",
            "",
            "ログを読んで原因を調べて。",
            "直さないで報告だけ。",
            "",
            "→ 直す",
            "",
            "## [PowerShell] ビルド",
            "<!-- -30,200.5 -->",
            "dotnet build -c Release",
            "→ [成功] 試す",
            "-> [失敗] 原因を調べる",
            "",
            "## [Claude] 直す",
            "次のコードを見て:",
            "```",
            "## [Claude] これはカードではない",
            "→ これもつながりではない",
            "```",
            "→ ビルド",
            "",
            "## [Claude] 試す",
            "",
            "## [Claude] 直す",
            "題が重なったカード",
            "",
            "## [メモ] 知らない種類",
            "これは前のカードの本文になる",
        });

        var flow = FlowFile.Parse(text);
        Expect(flow.Preamble == "# ビルドの流れ\n\n説明の行", "前置き");
        Expect(flow.Cards.Count == 5, "カードの数 " + flow.Cards.Count);

        var first = flow.Cards[0];
        Expect(first.Kind == FlowKind.Claude && first.Title == "原因を調べる", "1枚目の種類と題");
        Expect(first.HasPosition && first.X == 120 && first.Y == 40, "1枚目の位置");
        Expect(first.Body == "ログを読んで原因を調べて。\n直さないで報告だけ。", "1枚目の本文");
        Expect(first.Edges.Count == 1 && first.Edges[0].Target == "直す" && first.Edges[0].Label == "", "1枚目のつながり");

        var build = flow.Cards[1];
        Expect(build.Kind == FlowKind.PowerShell && build.X == -30 && build.Y == 200.5, "PowerShell と位置（負・小数）");
        Expect(build.Body == "dotnet build -c Release", "PowerShell の本文");
        Expect(build.Edges.Count == 2 && build.Edges[0].Label == "成功" && build.Edges[0].Target == "試す"
               && build.Edges[1].Label == "失敗" && build.Edges[1].Target == "原因を調べる", "名前の付いた線（→ と ->）");

        var fix = flow.Cards[2];
        Expect(fix.Body.Contains("## [Claude] これはカードではない") && fix.Body.Contains("→ これもつながりではない"), "``` の中はカード・つながりにしない");
        Expect(fix.Edges.Count == 1 && fix.Edges[0].Target == "ビルド", "``` のあとのつながり");
        Expect(!fix.HasPosition, "位置の無いカード");

        Expect(flow.Cards[3].Title == "試す" && flow.Cards[3].Body == "", "本文の無いカード");
        Expect(flow.Cards[4].Title == "直す (2)", "重なった題を付け替える: " + flow.Cards[4].Title);
        Expect(flow.Cards[4].Body.EndsWith("## [メモ] 知らない種類\nこれは前のカードの本文になる"), "知らない種類は本文");

        // 書いて読み直しても同じ
        var again = FlowFile.Parse(flow.Serialize());
        Expect(again.Serialize() == flow.Serialize(), "書き直しても同じ");
        Expect(again.Cards.Count == flow.Cards.Count
               && again.Cards.Zip(flow.Cards, (a, b) => a.Title == b.Title && a.Body == b.Body && a.Kind == b.Kind
                                                        && a.HasPosition == b.HasPosition && a.X == Math.Round(b.X) && a.Y == Math.Round(b.Y)
                                                        && a.Edges.Count == b.Edges.Count).All(ok => ok),
            "読み直した中身が同じ");

        // 題を変えるとつながりも付け替わる・消すとつながりも消える
        flow.Rename(fix, "原因を調べる");
        Expect(fix.Title == "原因を調べる (2)", "重なる題には番号: " + fix.Title);
        Expect(first.Edges[0].Target == "原因を調べる (2)", "つながりも付け替わる");
        flow.Remove(build);
        Expect(fix.Edges.Count == 0 && flow.Cards.Count == 4, "消したカードへのつながりは消える");

        // 新しいカードの番号は、今ある番号の次
        var numbered = FlowFile.Parse("## [Claude] 1\n## [Claude] 7\n## [Claude] 題\n");
        Expect(numbered.NewId() == "8", "新しい番号: " + numbered.NewId());
        Expect(FlowFile.Parse("").NewId() == "1", "最初の番号");

        // 題の掃除
        Expect(FlowFile.CleanTitle(" a\tb\nc ") == "abc", "題から制御の文字を除く");
        Expect(flow.UniqueTitle("", null) == "カード", "空の題");

        // 空・カードの無いテキスト
        var empty = FlowFile.Parse("");
        Expect(empty.Cards.Count == 0 && empty.Serialize() == "", "空のテキスト");
        var crlf = FlowFile.Parse("## [ps] a\r\n<!-- 1,2 -->\r\nx\r\n\r\n→ b\r\n## [ai] b\r\n");
        Expect(crlf.Cards.Count == 2 && crlf.Cards[0].Kind == FlowKind.PowerShell && crlf.Cards[0].Body == "x"
               && crlf.Cards[0].Edges[0].Target == "b" && crlf.Cards[1].Kind == FlowKind.Claude, "CRLF・短い種類名");

        // 本文の最後の行が → で始まっても、つながりになる（決まり）。途中の → は本文
        var arrow = FlowFile.Parse("## [Claude] a\n→ 途中\n本文\n→ b\n");
        Expect(arrow.Cards[0].Body == "→ 途中\n本文" && arrow.Cards[0].Edges.Count == 1, "途中の → は本文");

        Console.WriteLine(_failed == 0 ? "全部 OK" : _failed + " 個 NG");
        return _failed == 0 ? 0 : 1;
    }
}
