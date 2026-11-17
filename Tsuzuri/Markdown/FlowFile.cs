using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// フロー（2026-11-17。SPEC 21章）。AI の欄のターミナルへ流し込むテキストを、カードとして並べてつないだもの。
    /// 文書のサブページ（図エディタの図と同じく今の文書の中。最初の行が印 Marker）。人が読んでも、Claude が読み書きしても分かる形にする:
    ///
    ///   &lt;!-- tsuzuri-flow --&gt;          … フローの印（最初の行）
    ///   # フローの名前                 … 最初のカードより前の行は、そのまま残す（説明など）
    ///
    ///   ## [Claude] 1                  … カード。[Claude] か [PowerShell] と題（番号。つながりの目印で、画面には出さない）
    ///   ## [Claude 新しい会話] 4       … ここから新しい会話で送る（続くカードもその会話へ）
    ///   &lt;!-- 120,40 --&gt;               … フロー図での位置（無くてもよい。無ければ並べて置く）
    ///
    ///   ログを読んで原因を調べて。      … 送るテキスト（前後の空行は除く）
    ///
    ///   添付: [ログ](ログ.txt)          … 添付のファイル（文書と同じリンクの書き方）
    ///
    ///   → 2                            … つながり。最後に並べる。→ [名前] 題 で、線に名前を付ける
    ///   → [失敗] 3
    ///
    ///   ## [結果] 5                    … 結果のカード（2026-11-17）。つながる前の Claude のカードの答えのうち、印で囲ませた部分を受け取る。
    ///   ```                             … 中身は ``` で囲む（答えに ## や → の行があっても崩れないように。答えの ``` より長い印にする）
    ///   受け取った答え
    ///   ```
    ///
    /// 送るテキストの中には、`## [Claude] ` などで始まる行を書けない（次のカードになる）。最後の `添付:`・`→ ` で始まる行は、添付・つながりになる。
    /// </summary>
    public sealed class FlowFile
    {
        /// <summary>最初のカードより前の行（説明など）。末尾の空行は除く。</summary>
        public string Preamble = string.Empty;

        public readonly List<FlowCard> Cards = new List<FlowCard>();

        /// <summary>サブページがフローである印（最初の行。2026-11-17）。</summary>
        public const string Marker = "<!-- tsuzuri-flow -->";

        /// <summary>新しい会話で送るカードの印（種類の後ろ）。</summary>
        public const string NewSessionWord = "新しい会話";

        /// <summary>ページの中身がフローか（空行を除いた最初の行が印）。</summary>
        public static bool IsFlow(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                return trimmed == Marker;
            }
            return false;
        }

        private static readonly Regex CardLine = new Regex(@"^##[ \t]+\[(?<kind>[^\]]+)\][ \t]*(?<title>.*?)[ \t]*$");
        private static readonly Regex PositionLine = new Regex(@"^<!--[ \t]*(?<x>-?\d+(?:\.\d+)?)[ \t]*,[ \t]*(?<y>-?\d+(?:\.\d+)?)[ \t]*-->[ \t]*$");
        private static readonly Regex AttachmentLine = new Regex(@"^添付[:：][ \t]*\[(?<label>(?:\\.|[^\]\\])*)\]\((?<target>.*)\)[ \t]*$");
        private static readonly Regex EdgeLine = new Regex(@"^(?:→|->)[ \t]*(?:\[(?<label>[^\]]*)\][ \t]*)?(?<target>.*?)[ \t]*$");

        /// <summary>カードの種類の名前を読む。知らない名前は null。</summary>
        public static FlowKind? KindOf(string name)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Equals("Claude", StringComparison.OrdinalIgnoreCase) || value.Equals("AI", StringComparison.OrdinalIgnoreCase)) return FlowKind.Claude;
            if (value.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) || value.Equals("PS", StringComparison.OrdinalIgnoreCase)) return FlowKind.PowerShell;
            if (value == ResultWord || value.Equals("Result", StringComparison.OrdinalIgnoreCase)) return FlowKind.Result;
            return null;
        }

        /// <summary>結果のカードの種類の名前。</summary>
        public const string ResultWord = "結果";

        public static string NameOf(FlowKind kind)
        {
            switch (kind)
            {
                case FlowKind.PowerShell:
                    return "PowerShell";
                case FlowKind.Result:
                    return ResultWord;
                default:
                    return "Claude";
            }
        }

        public static FlowFile Parse(string text)
        {
            var flow = new FlowFile();
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var preamble = new List<string>();
            FlowCard card = null;
            var body = new List<string>();
            var fence = string.Empty;   // 開いている ``` の印（無ければ空）

            foreach (var line in lines)
            {
                // ``` の中は見出しと見なさない（送るテキストにコードを書けるように）。閉じるのは、同じ文字で同じ長さ以上の印だけの行
                // （2026-11-17。前は ``` で始まる行ごとに開け閉めしていて、```` の中の ``` で閉じたと見なした）
                var inFence = fence.Length > 0;
                var run = FenceRun(line);
                if (fence.Length == 0)
                {
                    if (run.Length > 0) fence = run;
                }
                else if (run.Length >= fence.Length && run[0] == fence[0] && line.Trim() == run)
                {
                    fence = string.Empty;
                }

                var match = inFence || fence.Length > 0 ? Match.Empty : CardLine.Match(line);
                var words = match.Success ? match.Groups["kind"].Value.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
                FlowKind? kind = words.Length > 0 ? KindOf(words[0]) : null;
                if (kind != null)
                {
                    if (card != null) Finish(card, body);
                    card = new FlowCard { Kind = kind.Value, Title = match.Groups["title"].Value, NewSession = words.Skip(1).Contains(NewSessionWord) };
                    flow.Cards.Add(card);
                    body = new List<string>();
                    continue;
                }

                if (card == null) preamble.Add(line);
                else body.Add(line);
            }
            if (card != null) Finish(card, body);

            flow.Preamble = string.Join("\n", TrimBlank(preamble));
            flow.MakeTitlesUnique();
            return flow;
        }

        /// <summary>行の頭の ``` か ~~~（3つ以上）。無ければ空。</summary>
        private static string FenceRun(string line)
        {
            var trimmed = line.TrimStart(' ');
            if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~')) return string.Empty;
            var n = 0;
            while (n < trimmed.Length && trimmed[n] == trimmed[0]) n++;
            return n >= 3 ? trimmed.Substring(0, n) : string.Empty;
        }

        /// <summary>結果のカードの中身を ``` で囲む（中の ` の並びより長い印で）。空なら空のまま。</summary>
        private static string WrapResult(string body)
        {
            if (body.Length == 0) return body;
            var longest = 0;
            var current = 0;
            foreach (var c in body)
            {
                current = c == '`' ? current + 1 : 0;
                if (current > longest) longest = current;
            }
            var fence = new string('`', Math.Max(3, longest + 1));
            return fence + "\n" + body + "\n" + fence;
        }

        /// <summary>結果のカードの中身の囲みを1つ外す（最初の行が印だけで、最後の行が同じ印）。</summary>
        private static string UnwrapResult(string body)
        {
            var lines = body.Split('\n');
            if (lines.Length < 2) return body;
            var run = FenceRun(lines[0]);
            if (run.Length == 0 || run[0] != '`' || lines[0].Trim() != run || lines[lines.Length - 1].Trim() != run) return body;
            return string.Join("\n", lines.Skip(1).Take(lines.Length - 2));
        }

        private static void Finish(FlowCard card, List<string> lines)
        {
            // 題の直後（空行を挟まない）の位置の行
            if (lines.Count > 0)
            {
                var position = PositionLine.Match(lines[0]);
                if (position.Success)
                {
                    card.X = double.Parse(position.Groups["x"].Value, CultureInfo.InvariantCulture);
                    card.Y = double.Parse(position.Groups["y"].Value, CultureInfo.InvariantCulture);
                    card.HasPosition = true;
                    lines.RemoveAt(0);
                }
            }

            // 末尾の添付・つながりの行（空行は挟んでよい）
            var end = lines.Count;
            while (end > 0 && lines[end - 1].Trim().Length == 0) end--;
            var edgeStart = end;
            while (edgeStart > 0)
            {
                var line = lines[edgeStart - 1];
                if (line.Trim().Length == 0 || IsEdge(line) || AttachmentLine.IsMatch(line)) edgeStart--;
                else break;
            }
            for (var i = edgeStart; i < end; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                var attachment = AttachmentLine.Match(lines[i]);
                if (attachment.Success)
                {
                    card.Attachments.Add(new FlowAttachment { Label = attachment.Groups["label"].Value, Target = attachment.Groups["target"].Value.Trim() });
                    continue;
                }
                var match = EdgeLine.Match(lines[i]);
                var target = match.Groups["target"].Value;
                if (target.Length == 0) continue;
                card.Edges.Add(new FlowEdge { Target = target, Label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : string.Empty });
            }

            card.Body = string.Join("\n", TrimBlank(lines.Take(edgeStart)));
            if (card.Kind == FlowKind.Result) card.Body = UnwrapResult(card.Body);
        }

        private static bool IsEdge(string line)
        {
            var trimmed = line.TrimEnd();
            return trimmed.StartsWith("→", StringComparison.Ordinal) || trimmed.StartsWith("->", StringComparison.Ordinal);
        }

        private static IEnumerable<string> TrimBlank(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            var start = 0;
            while (start < list.Count && list[start].Trim().Length == 0) start++;
            var end = list.Count;
            while (end > start && list[end - 1].Trim().Length == 0) end--;
            return list.Skip(start).Take(end - start).Select(line => line.TrimEnd());
        }

        public string Serialize()
        {
            var text = new StringBuilder();
            if (Preamble.Length > 0) text.Append(Preamble).Append("\n\n");

            for (var i = 0; i < Cards.Count; i++)
            {
                var card = Cards[i];
                if (i > 0) text.Append('\n');
                text.Append("## [").Append(NameOf(card.Kind)).Append(card.NewSession ? " " + NewSessionWord : string.Empty).Append("] ").Append(card.Title).Append('\n');
                if (card.HasPosition)
                {
                    text.Append("<!-- ").Append(Math.Round(card.X).ToString(CultureInfo.InvariantCulture))
                        .Append(',').Append(Math.Round(card.Y).ToString(CultureInfo.InvariantCulture)).Append(" -->\n");
                }

                var body = string.Join("\n", TrimBlank(card.Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')));
                if (card.Kind == FlowKind.Result) body = WrapResult(body);
                if (body.Length > 0) text.Append('\n').Append(body).Append('\n');

                if (card.Attachments.Count > 0)
                {
                    text.Append('\n');
                    foreach (var attachment in card.Attachments)
                        text.Append("添付: [").Append(attachment.Label).Append("](").Append(attachment.Target).Append(")\n");
                }

                if (card.Edges.Count > 0)
                {
                    text.Append('\n');
                    foreach (var edge in card.Edges)
                    {
                        text.Append("→ ");
                        if (edge.Label.Length > 0) text.Append('[').Append(edge.Label).Append("] ");
                        text.Append(edge.Target).Append('\n');
                    }
                }
            }

            return text.ToString();
        }

        public FlowCard Find(string title)
        {
            return Cards.FirstOrDefault(card => card.Title == title);
        }

        /// <summary>題を、ほかのカードと重ならないものにする（空なら「カード」。重なれば (2)・(3)…）。</summary>
        public string UniqueTitle(string title, FlowCard except)
        {
            var name = CleanTitle(title);
            if (name.Length == 0) name = "カード";
            var candidate = name;
            for (var n = 2; Cards.Any(card => !ReferenceEquals(card, except) && card.Title == candidate); n++)
                candidate = name + " (" + n + ")";
            return candidate;
        }

        /// <summary>
        /// 新しいカードの題（番号。2026-11-17）。題は画面に出さず、つながりの目印にだけ使うので、今ある番号の次にする。
        /// </summary>
        public string NewId()
        {
            var max = 0;
            foreach (var card in Cards)
            {
                int number;
                if (int.TryParse(card.Title, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > max) max = number;
            }
            return UniqueTitle((max + 1).ToString(CultureInfo.InvariantCulture), null);
        }

        /// <summary>題に使えない文字（改行・制御の文字）を除く。</summary>
        public static string CleanTitle(string title)
        {
            var text = new StringBuilder();
            foreach (var c in title ?? string.Empty)
            {
                if (c >= ' ' && c != '\x7f') text.Append(c);
            }
            return text.ToString().Trim();
        }

        /// <summary>カードの題を変え、そのカードへのつながりも付け替える。</summary>
        public void Rename(FlowCard card, string title)
        {
            var name = UniqueTitle(title, card);
            if (name == card.Title) return;

            foreach (var other in Cards)
            {
                foreach (var edge in other.Edges)
                {
                    if (edge.Target == card.Title) edge.Target = name;
                }
            }
            card.Title = name;
        }

        /// <summary>カードを除き、そのカードへのつながりも除く。</summary>
        public void Remove(FlowCard card)
        {
            Cards.Remove(card);
            foreach (var other in Cards) other.Edges.RemoveAll(edge => edge.Target == card.Title);
        }

        /// <summary>読んだときに題が重なっていたら、後のほうを付け替える（つながりは前のほうを指したまま）。</summary>
        private void MakeTitlesUnique()
        {
            var seen = new HashSet<string>();
            foreach (var card in Cards)
            {
                card.Title = CleanTitle(card.Title);
                if (card.Title.Length == 0) card.Title = "カード";
                if (seen.Add(card.Title)) continue;

                var name = card.Title;
                for (var n = 2; seen.Contains(name); n++) name = card.Title + " (" + n + ")";
                card.Title = name;
                seen.Add(name);
            }
        }
    }

    public enum FlowKind
    {
        Claude,
        PowerShell,

        /// <summary>結果（2026-11-17）。送らない。前の Claude のカードの答えを受け取り、次のカードに渡す。</summary>
        Result,
    }

    public sealed class FlowCard
    {
        public FlowKind Kind;
        public string Title = string.Empty;
        public string Body = string.Empty;
        public double X;
        public double Y;
        public bool HasPosition;

        /// <summary>ここから新しい会話で送る（Claude のカード。続くカードもその会話へ）。</summary>
        public bool NewSession;

        public readonly List<FlowAttachment> Attachments = new List<FlowAttachment>();
        public readonly List<FlowEdge> Edges = new List<FlowEdge>();
    }

    /// <summary>カードに付けたファイル。リンクは文書の本文と同じ書き方（添付フォルダの中なら名前だけ）。</summary>
    public sealed class FlowAttachment
    {
        /// <summary>表示名（[ ] の中。記法の文字は \ で逃がしたまま）。</summary>
        public string Label = string.Empty;

        /// <summary>リンク先（( ) の中。空白などを含むときは &lt; &gt; で囲んだまま）。</summary>
        public string Target = string.Empty;
    }

    public sealed class FlowEdge
    {
        public string Target = string.Empty;

        /// <summary>線の名前（無ければ空）。分かれ道で、どちらへ進むかの目印。</summary>
        public string Label = string.Empty;
    }
}
