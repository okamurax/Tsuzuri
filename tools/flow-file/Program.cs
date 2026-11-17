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

        // フローの印（サブページの最初の行）
        Expect(FlowFile.IsFlow("\n<!-- tsuzuri-flow -->\n# a\n"), "印のあるページ");
        Expect(!FlowFile.IsFlow("# a\n<!-- tsuzuri-flow -->\n") && !FlowFile.IsFlow("") && !FlowFile.IsFlow(null), "印の無いページ");

        // 新しい会話・添付
        var session = FlowFile.Parse(string.Join("\n", new[]
        {
            "<!-- tsuzuri-flow -->",
            "## [Claude 新しい会話] 4",
            "<!-- 10,20 -->",
            "",
            "これを読んで",
            "",
            "添付: [ログ](ログ.txt)",
            "添付：[画像 1](<画像 1.png>)",
            "添付: [a\\]b](x(1).txt)",
            "",
            "→ 5",
            "## [PowerShell] 5",
            "添付: [途中](a.txt)",
            "本文",
        }));
        var four = session.Cards[0];
        Expect(session.Preamble == FlowFile.Marker, "印は前置きに残る");
        Expect(four.NewSession && four.Kind == FlowKind.Claude && four.Title == "4", "新しい会話の印");
        Expect(!session.Cards[1].NewSession, "印の無いカード");
        Expect(four.Body == "これを読んで", "添付の前までが本文: " + four.Body);
        Expect(four.Attachments.Count == 3
               && four.Attachments[0].Label == "ログ" && four.Attachments[0].Target == "ログ.txt"
               && four.Attachments[1].Label == "画像 1" && four.Attachments[1].Target == "<画像 1.png>"
               && four.Attachments[2].Label == "a\\]b" && four.Attachments[2].Target == "x(1).txt", "添付を読む");
        Expect(four.Edges.Count == 1 && four.Edges[0].Target == "5", "添付のあとのつながり");
        Expect(session.Cards[1].Attachments.Count == 0 && session.Cards[1].Body == "添付: [途中](a.txt)\n本文", "本文の途中の 添付: は本文");
        var round = FlowFile.Parse(session.Serialize());
        Expect(round.Serialize() == session.Serialize() && round.Cards[0].NewSession && round.Cards[0].Attachments.Count == 3, "新しい会話・添付も書き直して同じ");
        Expect(session.Serialize().StartsWith(FlowFile.Marker + "\n"), "書いても印が最初の行");

        Console.WriteLine(_failed == 0 ? "全部 OK" : _failed + " 個 NG");
        return _failed == 0 ? 0 : 1;
    }
}
