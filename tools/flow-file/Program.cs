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

        // 結果のカード（中身は ``` で囲んで書き、読むときに外す。答えに ## や → や ``` があっても崩れない）
        var answer = "答えの1行目\n## [Claude] 見出しに見える行\n```\ncode\n```\n→ つながりに見える行";
        var withResult = new FlowFile { Preamble = FlowFile.Marker };
        var asker = new FlowCard { Kind = FlowKind.Claude, Title = "1", Body = "調べて" };
        asker.Edges.Add(new FlowEdge { Target = "2" });
        var result = new FlowCard { Kind = FlowKind.Result, Title = "2", Body = answer };
        result.Edges.Add(new FlowEdge { Target = "3" });
        withResult.Cards.Add(asker);
        withResult.Cards.Add(result);
        withResult.Cards.Add(new FlowCard { Kind = FlowKind.Claude, Title = "3", Body = "{{2}} を直して" });
        var written = withResult.Serialize();
        Expect(written.Contains("## [結果] 2\n\n````\n"), "結果は ```` で囲む（中の ``` より長く）");
        var reread = FlowFile.Parse(written);
        Expect(reread.Cards.Count == 3, "結果の中の ## はカードにしない: " + reread.Cards.Count);
        Expect(reread.Cards[1].Kind == FlowKind.Result && reread.Cards[1].Body == answer, "結果の中身が戻る: " + reread.Cards[1].Body);
        Expect(reread.Cards[1].Edges.Count == 1 && reread.Cards[1].Edges[0].Target == "3", "結果からのつながり");
        Expect(FlowFile.Parse(reread.Serialize()).Serialize() == written, "結果も書き直して同じ");
        var emptyResult = FlowFile.Parse("## [結果] 1\n## [Result] 2\n本文\n");
        Expect(emptyResult.Cards[0].Kind == FlowKind.Result && emptyResult.Cards[0].Body == "" && emptyResult.Cards[1].Kind == FlowKind.Result
               && emptyResult.Cards[1].Body == "本文", "空の結果・囲みの無い結果: " + emptyResult.Cards[1].Body);
        var fenced = new FlowCard { Kind = FlowKind.Result, Title = "1", Body = "```\nだけ\n```" };
        var onlyFence = new FlowFile();
        onlyFence.Cards.Add(fenced);
        Expect(FlowFile.Parse(onlyFence.Serialize()).Cards[0].Body == fenced.Body, "囲みだけの結果も戻る");

        // ``` は同じ文字・同じ長さ以上でだけ閉じる（```` の中の ``` で閉じない）
        var nested = FlowFile.Parse("## [Claude] a\n````\n```\n## [Claude] b\n```\n````\n## [Claude] c\n");
        Expect(nested.Cards.Count == 2 && nested.Cards[1].Title == "c", "```` の中の ``` で閉じない: " + nested.Cards.Count);
        var tilde = FlowFile.Parse("## [Claude] a\n~~~\n```\n## [Claude] b\n~~~\n## [Claude] c\n");
        Expect(tilde.Cards.Count == 2, "~~~ は ``` で閉じない: " + tilde.Cards.Count);

        // 送るテキストの組み立て（FlowResults）
        string problem;
        var chain = FlowFile.Parse(string.Join("\n", new[]
        {
            "## [Claude] 1", "調べて", "→ [要約] 2",
            "## [結果] 2", "```", "原因はX", "```", "→ 3", "→ 4",
            "## [Claude 新しい会話] 3", "直して",
            "## [PowerShell] 4", "echo '{{2}}'",
            "## [Claude] 5", "{{2}} と {{9}} と {{4}}",
            "## [結果] 6", "→ 5",
        }));
        Expect(FlowResults.Compose(chain, chain.Find("3"), out problem) == "直して\n\n前の結果:\n原因はX" && problem == null, "つながる結果を後ろに付ける");
        Expect(FlowResults.Compose(chain, chain.Find("4"), out problem) == "echo '原因はX'" && problem == null, "PowerShell は {{}} だけ（後ろに付けない）");
        var five = FlowResults.Compose(chain, chain.Find("5"), out problem);
        Expect(five == "原因はX と {{9}} と {{4}}" && problem != null && problem.Contains("6"), "{{}} で入れた結果は後ろに付けない・結果でない番号はそのまま・空の結果は送らない: " + five + " / " + problem);
        chain.Find("2").Body = "";
        FlowResults.Compose(chain, chain.Find("3"), out problem);
        Expect(problem != null && problem.Contains("2"), "まだ受け取っていない結果");

        var wants = FlowResults.WantsOf(chain, chain.Find("1"));
        Expect(wants.Count == 1 && wants[0].Title == "2" && wants[0].Label == "要約" && wants[0].Start.StartsWith("<<<結果 2 ") && wants[0].End.StartsWith("<<<ここまで 2 "), "受け取る結果と印");
        Expect(FlowResults.WantsOf(chain, chain.Find("4")).Count == 0, "PowerShell からは受け取らない");
        var instruction = FlowResults.Instruction(wants);
        Expect(instruction.Contains(wants[0].Start + "\n（ここに「要約」）\n" + wants[0].End), "頼む文に印: " + instruction);
        Expect(FlowResults.WantsOf(chain, chain.Find("1"))[0].Start != wants[0].Start, "印は送るたびに変わる");

        // 答えから印の間を取り出す
        const string s = "<<<結果 2 ab12>>>", e = "<<<ここまで 2 ab12>>>";
        Expect(FlowResults.Extract("前置き\n" + s + "\n1行目\n\n2行目\n" + e + "\n後書き", s, e) == "1行目\n\n2行目", "印の間");
        Expect(FlowResults.Extract("**" + s + "**\n値\n**" + e + "**", s, e) == "値", "印の行の強調は除く");
        Expect(FlowResults.Extract("```\n" + s + "\n値\n" + e + "\n```", s, e) == "値", "コードブロックの中の印");
        Expect(FlowResults.Extract(s + " 値 " + e, s, e) == "値", "1行の中の印");
        Expect(FlowResults.Extract(s + "\n古い\n" + e + "\n" + s + "\n新しい\n" + e, s, e) == "新しい", "何度も出たら最後");
        Expect(FlowResults.Extract(s + "\n書きかけ", s, e) == null && FlowResults.Extract("無い", s, e) == null, "終わりの印が無ければ受け取らない");
        Expect(FlowResults.Extract("<<<結果 2 zz99>>>\n別\n<<<ここまで 2 zz99>>>", s, e) == null, "合言葉の違う印は見ない");
        Expect(FlowResults.Extract(s + "\n```js\nx = `a`\n```\n" + e, s, e) == "```js\nx = `a`\n```", "中のコードブロックは残す");

        Console.WriteLine(_failed == 0 ? "全部 OK" : _failed + " 個 NG");
        return _failed == 0 ? 0 : 1;
    }
}
