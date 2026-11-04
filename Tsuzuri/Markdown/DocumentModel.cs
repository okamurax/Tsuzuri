using System.Collections.Generic;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 文書1つぶんの解析結果。行ごとの種類と、行ごとの装飾範囲を持つ。
    /// 描画側（色付け・記号隠し・リンク判定・アウトライン）はすべてここを見る。
    /// </summary>
    public sealed class DocumentModel
    {
        public readonly LineInfo[] Lines;

        /// <summary>Lines と同じ並びの、行内の装飾範囲とリンク。</summary>
        public readonly InlineScan[] Inlines;

        /// <summary>文書の中で一番深い段。</summary>
        public int MaxDepth { get; private set; }

        /// <summary>
        /// 一部だけを解析し直してはいけない文書か（2026-11-04。MarkdownView の打った段落だけを解析し直す近道）。
        /// 空行をまたいで効く書き方（HTML のかたまり・リンクの定義 [名]: URL・脚注の定義・閉じていないコードフェンス）があると、段落だけを取り出して解析すると結果が変わるため。
        /// 解析に失敗したときも true。
        /// </summary>
        public bool Fragile { get; internal set; }

        public DocumentModel(LineInfo[] lines, InlineScan[] inlines, int maxDepth)
        {
            Lines = lines;
            Inlines = inlines;
            MaxDepth = maxDepth;
        }

        /// <summary>行の段差を、今の行の種類から決め直す（見出しの記号を付け外ししたとき。2026-11-04）。</summary>
        public void RecomputeDepths()
        {
            MaxDepth = MarkdownParser.AssignDepths(Lines);
        }

        public static DocumentModel Empty()
        {
            return new DocumentModel(new LineInfo[0], new InlineScan[0], 0);
        }

        public LineInfo LineAt(int lineNumber)
        {
            return (lineNumber >= 1 && lineNumber <= Lines.Length) ? Lines[lineNumber - 1] : null;
        }

        public InlineScan InlineAt(int lineNumber)
        {
            return (lineNumber >= 1 && lineNumber <= Inlines.Length) ? Inlines[lineNumber - 1] : null;
        }

        public List<OutlineHeading> Headings()
        {
            var result = new List<OutlineHeading>();

            for (var i = 0; i < Lines.Length; i++)
            {
                if (Lines[i].Kind != LineKind.Heading || Lines[i].ContinuesHeading) continue;

                // 2行以上の下線式の見出しは、続きの行を空白でつないで1つの名前にする。
                var text = Lines[i].Content.Trim();
                for (var j = i + 1; j < Lines.Length && Lines[j].ContinuesHeading; j++)
                    text += " " + Lines[j].Content.Trim();

                result.Add(new OutlineHeading(Lines[i].Level, text, i + 1));
            }

            return result;
        }
    }

    /// <summary>アウトラインの1項目（SPEC 7章）。</summary>
    public sealed class OutlineHeading
    {
        public readonly int Level;
        public readonly string Text;
        public int Line;

        public OutlineHeading(int level, string text, int line)
        {
            Level = level;
            Text = text;
            Line = line;
        }
    }

    /// <summary>
    /// 見出しの文字を、画面に出す形にする（2026-10-24）。リンク [名前](#p3)・画像 ![名前](a.png) は名前だけにする。
    /// サブページ作成で見出しの文字をリンクにするので、アウトラインやページの帯に [ ] や (#p3) が出てしまうため。
    /// 出すときだけに使う。ブックマークの照合・たたみ状態の鍵・名前の変更は、元の文字のまま扱う。
    /// </summary>
    public static class HeadingText
    {
        private static readonly System.Text.RegularExpressions.Regex Link =
            new System.Text.RegularExpressions.Regex(@"!?\[([^\]]*)\]\([^)]*\)", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string Display(string text)
        {
            return string.IsNullOrEmpty(text) ? text : Link.Replace(text, "$1");
        }
    }
}
