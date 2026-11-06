using System;
using System.Collections.Generic;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// Markdig に解析させて、行ごとの種類と装飾範囲に落とす（SPEC 8章）。
    ///
    /// 記法の解釈は一切自前で持たない。ここがやるのは
    /// 「Markdig の文書ツリー」を「行ごとの描画情報」に並べ替えることだけ。
    /// </summary>
    public static class MarkdownParser
    {
        /// <summary>書き出し（Export）も同じ解釈で読むため、外から使えるようにしておく。</summary>
        internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()          // | a | b |
            .UseTaskLists()           // - [ ] / - [x]
            .UseAutoLinks()           // http://... をそのまま書いた場合
            .UseFootnotes()           // [^1]
            .UseEmphasisExtras()      // ~~打消し~~ など
            .UsePreciseSourceLocation()
            .Build();

        /// <summary>
        /// 画面に開いていないページの見出し。アウトラインと同じ解釈で数える。
        /// 見出しのブックマーク・見出しへのリンクを探すのと、節を動かす・同じ階層を回すときの確かめに使う。
        /// </summary>
        public static List<OutlineHeading> Headings(string text)
        {
            text = text ?? string.Empty;

            var texts = new List<string>();
            var offsets = new List<int>();
            var start = 0;

            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;

                var end = i;
                if (end > start && text[end - 1] == '\r') end--;

                texts.Add(text.Substring(start, end - start));
                offsets.Add(start);
                start = i + 1;
            }

            return Parse(text, texts, offsets).Headings();
        }

        public static DocumentModel Parse(string text, IList<string> lineTexts, IList<int> lineOffsets)
        {
            var count = lineTexts.Count;
            if (count == 0) return DocumentModel.Empty();

            var lines = new LineInfo[count];
            var inlines = new InlineScan[count];

            for (var i = 0; i < count; i++)
            {
                lines[i] = new LineInfo();
                lines[i].Source = lineTexts[i] ?? string.Empty;
                lines[i].Kind = lines[i].Source.Trim().Length == 0 ? LineKind.Blank : LineKind.Paragraph;
                inlines[i] = new InlineScan();
            }

            var fragile = true;
            var htmlRanges = new List<KeyValuePair<int, int>>();
            try
            {
                var document = Markdig.Markdown.Parse(text ?? string.Empty, Pipeline);
                var walker = new Walker(lines, inlines, lineTexts, lineOffsets);
                walker.Walk(document);
                fragile = walker.Fragile;
                htmlRanges = walker.HtmlRanges;
            }
            catch (Exception)
            {
                // 解析に失敗しても編集は止めない。素のテキストとして見せる。
            }

            var model = Finish(lines, inlines);
            model.Fragile = fragile;
            model.HtmlRanges = htmlRanges;
            return model;
        }

        // =====================================================================
        // 文書ツリーを行ごとに撒く
        // =====================================================================

        private sealed class Walker
        {
            private readonly LineInfo[] _lines;
            private readonly InlineScan[] _inlines;
            private readonly IList<string> _lineTexts;
            private readonly IList<int> _lineOffsets;

            public Walker(LineInfo[] lines, InlineScan[] inlines,
                IList<string> lineTexts, IList<int> lineOffsets)
            {
                _lines = lines;
                _inlines = inlines;
                _lineTexts = lineTexts;
                _lineOffsets = lineOffsets;
            }

            /// <summary>空行をまたいで効く書き方（リンクの定義・脚注の定義・閉じていないコードフェンス）か、行の分からない HTML のかたまりがあったか（DocumentModel.Fragile）。</summary>
            public bool Fragile;

            /// <summary>空行をまたいで続く HTML のかたまりの行（DocumentModel.HtmlRanges）。</summary>
            public readonly List<KeyValuePair<int, int>> HtmlRanges = new List<KeyValuePair<int, int>>();

            public void Walk(Block block)
            {
                Stamp(block);

                var leaf = block as LeafBlock;
                if (leaf != null && leaf.Inline != null) WalkInline(leaf.Inline.FirstChild);

                var container = block as ContainerBlock;
                if (container == null) return;

                foreach (var child in container) Walk(child);
            }

            /// <summary>親から順に撒くので、内側のブロックが外側を上書きする。</summary>
            private void Stamp(Block block)
            {
                if (block is MarkdownDocument) return;

                if (block is LinkReferenceDefinition || block is Footnote) Fragile = true;

                // 閉じていないコードフェンスは文書の終わりまで続き、後ろの行の解釈を変える。取り出した段落の ``` が、文書の中では前のフェンスを閉じる行のこともある。
                var open = block as FencedCodeBlock;
                if (open != null && open.ClosingFencedCharCount == 0) Fragile = true;

                var from = LineOf(block.Span.Start);
                if (from < 0)
                {
                    if (block is HtmlBlock) Fragile = true;   // 行が分からなければ、近道を使わない
                    return;
                }

                var to = LineOf(block.Span.End);
                if (to < from) to = from;

                // HTML のかたまりのうち、<script>・<pre>・<style>・<!-- -->・<? ?>・<!X>・CDATA は、閉じるまで空行をまたいで続く（2026-11-04）。
                // その行を覚えておき、打った段落だけを解析し直す近道を、その近くでは使わない。ほかの HTML（<div>・<a …> だけの行など）は空行で終わるので、段落と同じに扱える。
                // 前は HTML のかたまりが1つでもあると、文書のどこでも近道を使わなかった（<a …> の行がある大きな文書で、確定のたびに全体を解析していた）。
                var html = block as HtmlBlock;
                if (html != null && html.Type != HtmlBlockType.InterruptingBlock && html.Type != HtmlBlockType.NonInterruptingBlock)
                    HtmlRanges.Add(new KeyValuePair<int, int>(from, to));

                if (block is ThematicBreakBlock)
                {
                    SetRange(from, to, LineKind.Rule);
                    return;
                }

                var fenced = block as FencedCodeBlock;
                if (fenced != null)
                {
                    SetRange(from, to, LineKind.Code);
                    _lines[from].Kind = LineKind.Fence;
                    if (to > from) _lines[to].Kind = LineKind.Fence;
                    return;
                }

                if (block is CodeBlock)
                {
                    SetRange(from, to, LineKind.Code);
                    return;
                }

                var heading = block as HeadingBlock;
                if (heading != null)
                {
                    SetRange(from, to, LineKind.Heading);

                    for (var i = from; i <= to; i++) _lines[i].Level = heading.Level;

                    // 下線式（Setext）は本文行と下線行にまたがる。下線は見出しにしない。
                    // 本文が2行以上なら、2行目以降は1行目の続き（アウトラインでは1つの見出し）。
                    if (to > from)
                    {
                        _lines[to].Kind = LineKind.Setext;
                        for (var i = from + 1; i < to; i++) _lines[i].ContinuesHeading = true;
                    }
                    return;
                }

                if (block is Table)
                {
                    SetRange(from, to, LineKind.Table);
                    return;
                }

                if (block is QuoteBlock)
                {
                    SetRange(from, to, LineKind.Quote);
                    return;
                }

                var item = block as ListItemBlock;
                if (item != null)
                {
                    var list = item.Parent as ListBlock;
                    var ordered = (list != null && list.IsOrdered);

                    _lines[from].Kind = ordered ? LineKind.Ordered : LineKind.Bullet;
                    _lines[from].Level = NestDepth(_lineTexts[from]);
                }
            }

            private void SetRange(int from, int to, LineKind kind)
            {
                for (var i = from; i <= to && i < _lines.Length; i++)
                    _lines[i].Kind = kind;
            }

            // -----------------------------------------------------------------

            private void WalkInline(Inline inline)
            {
                while (inline != null)
                {
                    Handle(inline);

                    var container = inline as ContainerInline;
                    if (container != null) WalkInline(container.FirstChild);

                    inline = inline.NextSibling;
                }
            }

            private void Handle(Inline inline)
            {
                var emphasis = inline as EmphasisInline;
                if (emphasis != null)
                {
                    var count = emphasis.DelimiterCount;
                    var span = emphasis.Span;
                    if (count <= 0 || span.Length <= count * 2) return;

                    var kind = emphasis.DelimiterChar == '~'
                        ? SpanKind.Strike
                        : (count >= 2 ? SpanKind.Strong : SpanKind.Emphasis);

                    AddSpan(span.Start, count, SpanKind.Marker);
                    AddSpan(span.Start + count, span.Length - count * 2, kind);
                    AddSpan(span.End - count + 1, count, SpanKind.Marker);
                    return;
                }

                var code = inline as CodeInline;
                if (code != null)
                {
                    var span = code.Span;
                    var ticks = code.DelimiterCount > 0 ? code.DelimiterCount : 1;
                    if (span.Length <= ticks * 2) return;

                    AddSpan(span.Start, ticks, SpanKind.Marker);
                    AddSpan(span.Start + ticks, span.Length - ticks * 2, SpanKind.Code);
                    AddSpan(span.End - ticks + 1, ticks, SpanKind.Marker);
                    return;
                }

                var link = inline as LinkInline;
                if (link != null)
                {
                    HandleLink(link);
                    return;
                }

                var auto = inline as AutolinkInline;
                if (auto != null)
                {
                    var span = auto.Span;
                    if (span.IsEmpty || span.Length < 3) return;

                    AddSpan(span.Start, 1, SpanKind.Marker);          // "<"
                    AddSpan(span.Start + 1, span.Length - 2, SpanKind.LinkText);
                    AddSpan(span.End, 1, SpanKind.Marker);            // ">"
                    AddLink(span.Start, span.Length, auto.Url, auto.Url, false);
                    return;
                }

                var task = inline as TaskList;
                if (task != null)
                {
                    var line = LineOf(task.Span.Start);
                    if (line < 0) return;

                    _lines[line].Kind = LineKind.Task;
                    _lines[line].IsChecked = task.Checked;
                }
            }

            /// <summary>
            /// [名](先 "説明") を、記号・表示名・遷移先に切り分ける。
            /// タイトルは閉じ括弧と一緒に記号として扱う（遷移先に混ぜない）。
            ///
            /// Markdig の SourceSpan は構造体で、設定されていなくても既定値 (0,0) が入る。
            /// IsEmpty は「開始 > 終了」なので、既定値は「空」と判定されない。
            /// さらに自動リンクは LabelSpan を設定せず、参照リンクの UrlSpan は
            /// 定義行（このリンクの外）を指す。そのまま使うと本文を指す範囲ができ、
            /// 利用者が書いた文字が画面から消える。必ず内側にあることを確かめる。
            /// </summary>
            private void HandleLink(LinkInline link)
            {
                var span = link.Span;
                if (span.IsEmpty || span.Length <= 0) return;

                var label = link.LabelSpan;
                var url = link.UrlSpan;

                var usable = Inside(span, label)
                    && Inside(span, url)
                    && label.Start > span.Start
                    && url.Start > label.End
                    && url.End < span.End;

                if (!usable)
                {
                    // 自動リンクや参照リンク。隠せる記号が特定できないので、
                    // 何も消さず全体をリンクとして見せるだけにする。
                    AddSpan(span.Start, span.Length, SpanKind.LinkText);
                    AddLink(span.Start, span.Length, link.Label, link.Url, link.IsImage);
                    return;
                }

                AddSpan(span.Start, label.Start - span.Start, SpanKind.Marker);   // "[" または "!["
                AddSpan(label.Start, label.Length, SpanKind.LinkText);
                AddSpan(label.End + 1, url.Start - label.End - 1, SpanKind.Marker);   // "]("
                AddSpan(url.Start, url.Length, SpanKind.LinkDest);
                AddSpan(url.End + 1, span.End - url.End, SpanKind.Marker);        // タイトルと ")"

                AddLink(span.Start, span.Length, link.Label, link.Url, link.IsImage);
            }

            /// <summary>inner が outer の内側に収まっているか。</summary>
            private static bool Inside(SourceSpan outer, SourceSpan inner)
            {
                return !inner.IsEmpty
                    && inner.End >= inner.Start
                    && inner.Start >= outer.Start
                    && inner.End <= outer.End;
            }

            // -----------------------------------------------------------------

            private void AddSpan(int documentStart, int length, SpanKind kind)
            {
                int line, start, clipped;
                if (!Clip(documentStart, length, out line, out start, out clipped)) return;

                _inlines[line].Spans.Add(new InlineSpan(start, clipped, kind));
            }

            private void AddLink(int documentStart, int length, string label, string target, bool isImage)
            {
                if (string.IsNullOrEmpty(target)) return;

                int line, start, clipped;
                if (!Clip(documentStart, length, out line, out start, out clipped)) return;

                _inlines[line].Links.Add(new LinkSpan(start, clipped, label ?? string.Empty, target, isImage));
            }

            /// <summary>文書上の範囲を1行の中に収める。行をまたぐ分は切り落とす。</summary>
            private bool Clip(int documentStart, int length, out int line, out int start, out int clipped)
            {
                line = -1;
                start = 0;
                clipped = 0;

                if (length <= 0) return false;

                line = LineOf(documentStart);
                if (line < 0) return false;

                var lineLength = _lineTexts[line].Length;

                start = documentStart - _lineOffsets[line];
                if (start < 0)
                {
                    length += start;
                    start = 0;
                }

                if (start >= lineLength || length <= 0) return false;

                clipped = Math.Min(length, lineLength - start);
                return clipped > 0;
            }

            private int LineOf(int documentOffset)
            {
                if (documentOffset < 0) return -1;

                var low = 0;
                var high = _lineOffsets.Count - 1;
                var found = -1;

                while (low <= high)
                {
                    var middle = (low + high) / 2;

                    if (_lineOffsets[middle] <= documentOffset)
                    {
                        found = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                return found;
            }

            /// <summary>リストの入れ子の深さ。タブは4、空白2つで1段。</summary>
            private static int NestDepth(string text)
            {
                var width = 0;

                foreach (var c in text)
                {
                    if (c == ' ') width += 1;
                    else if (c == '\t') width += 4;
                    else break;
                }

                var depth = width / 2;
                return depth > 6 ? 6 : depth;
            }
        }

        // =====================================================================
        // 行頭の記号と段差を埋める
        // =====================================================================

        private static DocumentModel Finish(LineInfo[] lines, InlineScan[] inlines)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                FillMarker(lines[i]);
                inlines[i].Spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            }

            return new DocumentModel(lines, inlines, AssignDepths(lines));
        }

        /// <summary>
        /// 行の段差（Depth）を、前の見出しから決める。一番深い段を返す。
        /// 見出しの記号を付け外ししたとき、解析し直さずに段差だけを決め直すのにも使う（2026-11-04。DocumentModel.RecomputeDepths）。
        /// </summary>
        internal static int AssignDepths(LineInfo[] lines)
        {
            var headingLevel = 0;
            var maxDepth = 0;

            foreach (var info in lines)
            {
                if (info.Kind == LineKind.Heading)
                {
                    info.Depth = info.Level - 1;
                    headingLevel = info.Level;
                }
                else
                {
                    info.Depth = headingLevel;
                }

                if (info.Depth > maxDepth) maxDepth = info.Depth;
            }

            return maxDepth;
        }

        /// <summary>
        /// 行の種類が決まったあとで、行頭の記号を切り出す。
        /// 種類が分かっていれば境目は一意なので、ここは単純な走査でよい。
        /// </summary>
        private static void FillMarker(LineInfo info)
        {
            var text = info.Source;
            info.Marker = string.Empty;
            info.Content = text;

            var i = Skip(text, 0);

            switch (info.Kind)
            {
                case LineKind.Heading:
                    var hashes = i;
                    while (i < text.Length && text[i] == '#') i++;
                    if (i == hashes) return;                   // 下線式には # が無い
                    i = Skip(text, i);
                    break;

                case LineKind.Bullet:
                    if (i >= text.Length) return;
                    i++;                                        // - * +
                    i = Skip(text, i);
                    break;

                case LineKind.Task:
                    if (i >= text.Length) return;
                    i++;                                        // - * +
                    i = Skip(text, i);
                    if (i < text.Length && text[i] == '[') i = Math.Min(text.Length, i + 3);
                    i = Skip(text, i);
                    break;

                case LineKind.Ordered:
                    var digits = i;
                    while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
                    if (i == digits) return;
                    if (i < text.Length && (text[i] == '.' || text[i] == ')')) i++;
                    i = Skip(text, i);
                    break;

                case LineKind.Quote:
                    if (i >= text.Length || text[i] != '>') return;
                    i++;
                    if (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                    break;

                default:
                    return;
            }

            info.Marker = text.Substring(0, i);
            info.Content = text.Substring(i);
        }

        private static int Skip(string text, int from)
        {
            var i = from;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return i;
        }
    }
}
