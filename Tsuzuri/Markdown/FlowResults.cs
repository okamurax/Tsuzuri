using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// フローの結果のカード（2026-11-17。SPEC 21章）。送るテキストに結果を入れる・結果を印で囲んで書くよう頼む・答えから印の間を取り出す。
    /// 画面やターミナルに触らない部分（tools/flow-file で確かめる）。答えを待つのは MainWindow.FlowResult.cs。
    /// </summary>
    public static class FlowResults
    {
        private static readonly Regex Placeholder = new Regex(@"\{\{[ \t]*(?<title>[^{}\s]+)[ \t]*\}\}");

        /// <summary>
        /// 送るテキストを作る。本文の {{番号}} は、その結果のカードの中身にする。Claude のカードは、つながってくる結果のカードのうち
        /// {{ }} で使っていないものの中身を後ろに付ける。まだ受け取っていない結果があれば、送らずに problem を返す。
        /// </summary>
        public static string Compose(FlowFile flow, FlowCard card, out string problem)
        {
            string missing = null;
            var used = new HashSet<string>();
            var text = Placeholder.Replace(card.Body, match =>
            {
                var source = flow.Find(match.Groups["title"].Value);
                if (source == null || source.Kind != FlowKind.Result) return match.Value;   // 結果のカードでなければそのまま
                used.Add(source.Title);
                if (source.Body.Trim().Length == 0 && missing == null) missing = source.Title;
                return source.Body.Trim('\n');
            });

            if (card.Kind == FlowKind.Claude)
            {
                var incoming = flow.Cards.Where(other => other.Kind == FlowKind.Result && !used.Contains(other.Title)
                                                         && other.Edges.Any(edge => edge.Target == card.Title)).ToList();
                foreach (var source in incoming)
                {
                    if (source.Body.Trim().Length == 0)
                    {
                        if (missing == null) missing = source.Title;
                        continue;
                    }
                    text += "\n\n" + (incoming.Count > 1 ? "前の結果（" + source.Title + "）:" : "前の結果:") + "\n" + source.Body.Trim('\n');
                }
            }

            problem = missing != null ? "結果のカード " + missing + " をまだ受け取っていません（前の Claude のカードを送って答えを待つか、結果のカードに直接書いてください）" : null;
            return text;
        }

        /// <summary>このカードの答えを受け取る結果のカードと、その印（送るたびに変える。前の答えの印と取り違えないため）。</summary>
        public static List<FlowWant> WantsOf(FlowFile flow, FlowCard card)
        {
            var wants = new List<FlowWant>();
            if (card.Kind != FlowKind.Claude) return wants;
            var nonce = Guid.NewGuid().ToString("N").Substring(0, 4);
            foreach (var edge in card.Edges)
            {
                var target = flow.Find(edge.Target);
                if (target == null || target.Kind != FlowKind.Result || wants.Any(want => want.Title == target.Title)) continue;
                wants.Add(new FlowWant
                {
                    Title = target.Title,
                    Label = edge.Label,
                    Start = "<<<結果 " + target.Title + " " + nonce + ">>>",
                    End = "<<<ここまで " + target.Title + " " + nonce + ">>>",
                });
            }
            return wants;
        }

        /// <summary>結果を印で囲んで書くよう頼む文（送るテキストの後ろに付ける）。</summary>
        public static string Instruction(List<FlowWant> wants)
        {
            var text = new StringBuilder();
            text.Append("\n\n（この作業の結果は、ツールが受け取って次の作業に渡します。答えの最後に、渡す結果だけを次の印の行で囲んで書いてください。")
                .Append("印の行は1行ずつそのまま書き、コードブロックや強調の中に入れないでください。囲みの中には結果だけを書き、説明は囲みの外に書いてください）");
            foreach (var want in wants)
            {
                text.Append("\n").Append(want.Start)
                    .Append("\n（ここに").Append(want.Label.Length > 0 ? "「" + want.Label + "」" : "結果").Append("）\n")
                    .Append(want.End);
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

    /// <summary>Claude のカードからつながる結果のカード。答えのうち、この印で囲ませた部分を受け取る。</summary>
    public sealed class FlowWant
    {
        public string Title;
        public string Label;
        public string Start;
        public string End;
    }
}
