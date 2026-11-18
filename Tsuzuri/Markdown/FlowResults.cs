using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// フローで Claude に頼むこと（2026-11-17。SPEC 21章）。添付のファイル・分かれ道の行き先を選んで印で囲んで書くよう頼む・答えから印の間を取り出す。
    /// 画面やターミナルに触らない部分（tools/flow-file で確かめる）。答えを待つのは MainWindow.FlowResult.cs。
    /// 結果のカード（答えの一部を受け取って次のカードに渡す）は無くした（2026-11-18。利用者の判断）。
    /// </summary>
    public static class FlowResults
    {
        /// <summary>
        /// このカードへつないだ添付のカードのファイル（2026-11-17）。同じファイルは1度だけ。ページの並びの順。
        /// </summary>
        public static List<FlowAttachment> FilesFor(FlowFile flow, FlowCard card)
        {
            var files = new List<FlowAttachment>();
            foreach (var source in flow.Cards.Where(other => other.Kind == FlowKind.Files && other.Edges.Any(edge => edge.Target == card.Title)))
            {
                foreach (var attachment in source.Attachments)
                {
                    if (files.All(other => other.Target != attachment.Target)) files.Add(attachment);
                }
            }
            return files;
        }

        /// <summary>
        /// 分かれ道の行き先（2026-11-17）。Claude のカードから Claude のカードへの線のうち、名前の付いたものの名前（重なりは1つに）。
        /// 名前が2つ以上あれば分かれ道で、そのうち1つを選んだ線だけ進む（名前の無い線はいつも進む）。1つ以下なら分かれ道ではない（空の一覧）。
        /// </summary>
        public static List<string> Choices(FlowFile flow, FlowCard card)
        {
            var labels = new List<string>();
            if (card.Kind != FlowKind.Claude) return labels;
            foreach (var edge in card.Edges)
            {
                var target = flow.Find(edge.Target);
                if (target == null || target.Kind != FlowKind.Claude) continue;
                if (edge.Label.Length > 0 && !labels.Contains(edge.Label)) labels.Add(edge.Label);
            }
            return labels.Count >= 2 ? labels : new List<string>();
        }

        /// <summary>
        /// Claude が書いた行き先を、線の名前に合わせる。そのままか、かぎ括弧などを除いて同じなら、それ。
        /// 違えば、その中に1つだけ含まれる名前（「OK です」なら OK）。決まらなければ null。
        /// </summary>
        public static string MatchChoice(List<string> options, string value)
        {
            if (value == null) return null;
            var text = value.Trim().Trim('「', '」', '『', '』', '"', '\'', '*', '`', '[', ']', '(', ')', '（', '）', ' ', '　', '.', '。');
            var exact = options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var contained = options.Where(option => text.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            // 「直す」と「直さない」のように、ほかの名前に含まれる名前は、長いほうが当たったら数えない
            contained = contained.Where(option => !contained.Any(other => other.Length > option.Length && other.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            return contained.Count == 1 ? contained[0] : null;
        }

        /// <summary>このカードの答えから読むもの（分かれ道なら行き先）と、その印（送るたびに変える。前の答えの印と取り違えないため）。</summary>
        public static List<FlowWant> WantsOf(FlowFile flow, FlowCard card)
        {
            var wants = new List<FlowWant>();
            if (card.Kind != FlowKind.Claude) return wants;
            var nonce = Guid.NewGuid().ToString("N").Substring(0, 4);
            var choices = Choices(flow, card);
            if (choices.Count > 0)
            {
                wants.Add(new FlowWant
                {
                    Title = card.Title,
                    Start = "<<<行き先 " + nonce + ">>>",
                    End = "<<<ここまで 行き先 " + nonce + ">>>",
                    Options = choices,
                });
            }
            return wants;
        }

        /// <summary>行き先を選んで印で囲んで書くよう頼む文（送るテキストの後ろに付けるか、フックの文脈で渡す）。</summary>
        public static string Instruction(List<FlowWant> wants)
        {
            var text = new StringBuilder();
            foreach (var want in wants.Where(want => want.Options != null))
            {
                var names = string.Join("・", want.Options.Select(option => "「" + option + "」"));
                text.Append("\n\n（ツールが、この答えに合わせて次の頼みを決めます。答え終わったら、答えの最後に、答えの内容に一番合うものを次から1つだけ選び、")
                    .Append("その名前だけを次の印の行で囲んで書いてください: ").Append(names)
                    .Append("。印の行は1行ずつそのまま書き、コードブロックや強調の中に入れないでください）\n")
                    .Append(want.Start).Append("\n（").Append(string.Join(" か ", want.Options)).Append(" のどれか1つ）\n").Append(want.End);
            }
            return text.ToString();
        }

        /// <summary>
        /// 答えから、印で囲んだ部分を取り出す。印が何度も出たら最後のもの。印の行に残った強調などの記号（* ` _）は除く。無ければ null。
        /// </summary>
        public static string Extract(string answer, string start, string end)
        {
            var finish = answer.LastIndexOf(end, StringComparison.Ordinal);
            if (finish < 0) return null;
            var begin = answer.LastIndexOf(start, finish, StringComparison.Ordinal);
            if (begin < 0) return null;
            begin += start.Length;

            var value = answer.Substring(begin, finish - begin).Replace("\r\n", "\n");
            var firstBreak = value.IndexOf('\n');
            if (firstBreak >= 0 && IsMarkupOnly(value.Substring(0, firstBreak))) value = value.Substring(firstBreak + 1);
            var lastBreak = value.LastIndexOf('\n');
            if (lastBreak >= 0 && IsMarkupOnly(value.Substring(lastBreak + 1))) value = value.Substring(0, lastBreak);
            return value.Trim('\n', ' ', '\t');
        }

        private static bool IsMarkupOnly(string text)
        {
            return text.All(c => c == ' ' || c == '\t' || c == '*' || c == '`' || c == '_');
        }
    }

    /// <summary>Claude に選ばせる行き先。答えのうち、この印で囲ませた部分を読む。</summary>
    public sealed class FlowWant
    {
        public string Title;
        public string Start;
        public string End;

        /// <summary>選べる線の名前（2026-11-17）。Title はそのカード自身。</summary>
        public List<string> Options;
    }
}
