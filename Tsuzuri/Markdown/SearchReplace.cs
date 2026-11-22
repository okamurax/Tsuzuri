using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 検索の当たりを全部置き換える（2026-11-22。SPEC 12章「置換」）。画面に依らない部分だけを置き、tools/replace-fuzz からそのままコンパイルして確かめる。
    /// 照合は検索（MarkdownView.RecomputeMatches）と同じ: 行ごと（改行は \r\n・\n・\r。AvalonEdit と同じ）・空の当たりは数えず置き換えない。
    /// 改行そのものは決して変えない（置き換える文字に改行を入れさせず、$1 なども行の中の文字しか持たない）。
    /// 返すのは本文への1つの編集（変わる所の最初から最後まで。前後の同じ文字は含めない）で、元に戻すは1回で戻る。
    /// </summary>
    public static class SearchReplace
    {
        public sealed class Plan
        {
            /// <summary>置き換えた当たりの数（置き換えても同じ文字になったものも数える）。</summary>
            public int Count;

            /// <summary>本文が変わるか。false なら Offset 以下は意味を持たない。</summary>
            public bool Changes;

            public int Offset;
            public int Length;
            public string Text;
        }

        /// <summary>置き換える文字として使えないなら、その理由。使えるなら null。</summary>
        public static string Refuse(string replacement)
        {
            if (replacement == null) return "置き換える文字がありません";
            if (replacement.IndexOf('\r') >= 0 || replacement.IndexOf('\n') >= 0) return "置き換える文字に改行は入れられません";
            return null;
        }

        /// <summary>
        /// 置き換える文字の $ のうち、$数字（括弧の中。$0 は当たり全体）・${名前}・$$（$ そのもの）だけを効かせ、ほかの $ はそのままの文字にした形
        /// （.NET の $_（行全体）・$`・$'・$+・$&amp; は効かせない。PHP の $_GET や値段の $ を打ったら、そのまま入るように）。
        /// </summary>
        public static string Substitution(string replacement)
        {
            var builder = new StringBuilder(replacement.Length + 8);
            for (var i = 0; i < replacement.Length; i++)
            {
                var c = replacement[i];
                if (c != '$')
                {
                    builder.Append(c);
                    continue;
                }
                var next = i + 1 < replacement.Length ? replacement[i + 1] : '\0';
                if (next == '$')
                {
                    builder.Append("$$");
                    i++;
                }
                else if ((next >= '0' && next <= '9') || next == '{')
                {
                    builder.Append('$');
                }
                else
                {
                    builder.Append("$$");
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// text の中の search の当たりを replacement（$1 などが使える）に置き換える編集を作る。text は変えない。
        /// RegexMatchTimeoutException（照合が長すぎる）と ArgumentException（置き換える文字が読めない・改行がある・改行がくっつく）はそのまま投げる。
        /// どちらのときも何も作らない（本文は変えない）。
        /// </summary>
        public static Plan Make(string text, Regex search, string replacement)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (search == null) throw new ArgumentNullException("search");
            var refused = Refuse(replacement);
            if (refused != null) throw new ArgumentException(refused);

            var substitution = Substitution(replacement);
            var plan = new Plan();
            var result = new StringBuilder(text.Length);
            var position = 0;
            var afterLoneCr = false;   // 前の行の後ろの改行が単独の \r か
            while (position <= text.Length)
            {
                // 行の中身 [position, end) と、その後ろの改行 [end, next)
                var end = position;
                while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
                var next = end;
                if (next < text.Length) next += text[next] == '\r' && next + 1 < text.Length && text[next + 1] == '\n' ? 2 : 1;

                var line = text.Substring(position, end - position);
                var hits = 0;
                var replaced = search.Replace(line, match =>
                {
                    if (match.Length == 0) return match.Value;
                    hits++;
                    return match.Result(substitution);
                });
                if (replaced.IndexOf('\r') >= 0 || replaced.IndexOf('\n') >= 0)
                    throw new ArgumentException("置き換えた結果に改行が入りました");   // 起きないはず。起きたら何も変えない

                // 単独の \r と \n に挟まれた行が空になると、2つの改行が \r\n の1つにくっつき、行が1つ消える。何も変えない
                if (hits > 0 && replaced.Length == 0 && afterLoneCr && end < text.Length && text[end] == '\n')
                    throw new ArgumentException("置き換えると行がなくなり、前後の改行（CR と LF）が1つにくっつきます");

                plan.Count += hits;
                result.Append(hits > 0 ? replaced : line);
                result.Append(text, end, next - end);

                afterLoneCr = next - end == 1 && text[end] == '\r';
                if (next == text.Length && end == text.Length) break;   // 最後の行（改行の後ろの空の行も1行）を済ませた
                position = next;
            }

            var after = result.ToString();
            if (after == text) return plan;

            // 前後の同じ文字を除いて、変わる所だけを1つの編集にする（サロゲートの対は割らない）
            var prefix = 0;
            var shorter = Math.Min(text.Length, after.Length);
            while (prefix < shorter && text[prefix] == after[prefix]) prefix++;
            if (prefix > 0 && char.IsHighSurrogate(text[prefix - 1])) prefix--;

            var suffix = 0;
            while (suffix < shorter - prefix && text[text.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
            if (suffix > 0 && char.IsLowSurrogate(text[text.Length - suffix])) suffix--;

            plan.Changes = true;
            plan.Offset = prefix;
            plan.Length = text.Length - prefix - suffix;
            plan.Text = after.Substring(prefix, after.Length - prefix - suffix);
            return plan;
        }
    }
}
