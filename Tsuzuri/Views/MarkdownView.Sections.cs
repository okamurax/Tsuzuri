using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    // MarkdownView の一部: 見出しの節を動かす・レベルを変える・足す・消す・名前を変える（アウトラインから呼ぶ）。
    // MarkdownView.cs が大きくなりすぎたので分けた（2026-10-14。中身は変えていない）。
    public partial class MarkdownView
    {
        // =====================================================================
        // 節を動かす（SPEC 7章 アウトラインのドラッグ）
        //
        // 節 = 見出しから、同じかそれより浅い次の見出しの手前まで。配下の小見出しも含む。
        // 見出しの判定は Markdig の解釈（アウトラインと同じ）なので、コードの中の # 行は数えない。
        // 付箋ボードの並べ替え・▲▼ の入れ替えに使っていた MoveSectionBefore・MoveSection は、付箋ボードと一緒に外した（2026-10-14）。
        // =====================================================================

        /// <summary>入れ替えた結果、Markdown の読み方が変わるので取りやめた。</summary>
        public const int MoveChangesMeaning = -2;

        /// <summary>
        /// その位置へ動かしても並びが変わらないので、何もしなかった。
        /// 成功（行番号）と区別する。
        /// </summary>
        public const int MoveUnchanged = -3;

        /// <summary>
        /// 節（見出しから、同じかそれより浅い次の見出しの手前まで。配下の小見出しも含む）を、
        /// before の見出しの手前へ動かす。before が -1 なら文書の最後へ（SPEC 7章 アウトラインのドラッグ）。
        /// 親の外へも、レベルの違う見出しのあいだへも動かす。
        /// newLevel が 1〜6 なら、動かした見出しをそのレベルにし、配下の見出しも同じだけずらす（親子の形を保つ）。
        /// 0 ならレベルは変えない。ずらすと 6 より深くなる見出しがあれば LevelOutOfRange、
        /// ずらす必要のある下線式の見出しがあれば LevelNotAtx（# が無いので変えられない）、
        /// 引用やリストの中の見出しがあれば LevelNotPlain。
        /// 自分の配下の見出しの手前へは動かさない（-1）。並びもレベルも変わらなければ MoveUnchanged。
        /// 並びが変わらずレベルだけ変わるとき（すぐ上の見出しの子に入れる等）は、その場でレベルだけずらす。
        /// 動かしたあとの文書を読み直し、見出しの顔ぶれが変わるなら取りやめる（MoveChangesMeaning）。
        /// 変わった部分だけを1回で置き換えるので、元に戻すも1回で戻る。動かした見出しの新しい行番号を返す。
        /// </summary>
        public int MoveSectionTo(int headingLine, int beforeLine, int newLevel = 0)
        {
            if (IsReadOnly) return -1;

            var entries = EnsureOutline();
            var index = IndexOfHeading(entries, headingLine);
            if (index < 0) return -1;

            var end = SectionEnd(entries, index);
            var text = Document.Text;
            var start = Document.GetLineByNumber(entries[index].Line).Offset;
            var stop = end >= 0 ? Document.GetLineByNumber(entries[end].Line).Offset : text.Length;

            int target;
            if (beforeLine < 0)
            {
                target = text.Length;
            }
            else
            {
                var before = IndexOfHeading(entries, beforeLine);
                if (before < 0) return -1;
                target = Document.GetLineByNumber(entries[before].Line).Offset;
            }

            if (target > start && target < stop) return -1;           // 自分の配下へは動かさない

            // 引用・リストの中の見出しが切れ目になるなら動かさない。引用やリストが途中で切れて、形が変わるため。
            if (InsideContainer(entries[index].Line) || (end >= 0 && InsideContainer(entries[end].Line))
                || (beforeLine > 0 && InsideContainer(beforeLine))) return LevelNotPlain;

            // 自分の手前・自分の節のすぐ後ろは、並びが変わらない。レベルも変わらなければ何もしない。
            // レベルだけ変わるなら（すぐ上の見出しの子に入れる等）、その場でレベルだけずらす。
            var inPlace = target == start || target == stop;
            if (inPlace && (newLevel < 1 || newLevel > 6 || newLevel == entries[index].Level)) return MoveUnchanged;

            // 動かす節の見出しのレベルをずらす（配下も同じだけ）。後ろの見出しから直すので、前の位置はずれない。
            var section = new StringBuilder(text.Substring(start, stop - start));
            var last = end >= 0 ? end : entries.Count;
            var delta = newLevel >= 1 && newLevel <= 6 ? newLevel - entries[index].Level : 0;
            var expected = new List<OutlineHeading>(entries);

            if (delta != 0)
            {
                for (var k = last - 1; k >= index; k--)
                {
                    var level = entries[k].Level + delta;
                    if (level > 6 || level < 1) return LevelOutOfRange;

                    var line = Document.GetLineByNumber(entries[k].Line);
                    var match = AtxHashes.Match(Document.GetText(line.Offset, line.Length));
                    if (!match.Success) return NotAtxReason(Document.GetText(line.Offset, line.Length));

                    var hashes = match.Groups[1];
                    section.Remove(line.Offset - start + hashes.Index, hashes.Length);
                    section.Insert(line.Offset - start + hashes.Index, new string('#', level));

                    expected[k] = new OutlineHeading(level, entries[k].Text, entries[k].Line);
                }
            }

            string candidate;
            int moved;

            if (inPlace)
            {
                candidate = text.Substring(0, start) + section + text.Substring(stop);
                moved = start;
            }
            else
            {
                candidate = Relocate(text, section.ToString(), start, stop, target, out moved);
            }

            // 動かしたあとの見出しの顔ぶれが、レベルをずらした分だけ変わった形になっているか確かめる。
            if (!SameHeadings(expected, MarkdownParser.Headings(candidate))) return MoveChangesMeaning;

            // 変わった部分だけを置き換える。
            var prefix = 0;
            var limit = Math.Min(text.Length, candidate.Length);
            while (prefix < limit && text[prefix] == candidate[prefix]) prefix++;

            var suffix = 0;
            while (suffix < limit - prefix
                   && text[text.Length - 1 - suffix] == candidate[candidate.Length - 1 - suffix]) suffix++;

            Document.Replace(prefix, text.Length - prefix - suffix,
                candidate.Substring(prefix, candidate.Length - prefix - suffix));

            return Document.GetLineByOffset(Math.Min(moved, Document.TextLength)).LineNumber;
        }

        /// <summary>text の start〜stop にあった節を抜き、target（抜く前の位置）へ section を入れた文書。moved は入れた節の頭の位置。</summary>
        private static string Relocate(string text, string sectionText, int start, int stop, int target, out int moved)
        {
            string body, gap;
            SplitTrailingBreaks(sectionText, out body, out gap);

            // 抜く。抜いた跡で、前の文章と次の見出しがくっつかないよう、見出しの前は空行で区切る。
            var head = text.Substring(0, start);
            var tail = text.Substring(stop);
            if (head.Length > 0 && tail.Length > 0) head += BreaksToBlankLine(head);

            var rest = head + tail;

            // 抜いたあとの文書での、入れる位置。
            var at = target < start ? target : head.Length + (target - stop);

            // 入れる。見出しの前は空行で区切り、後ろに見出しが続くならそこも空行で区切る。
            var before2 = rest.Substring(0, at);
            var lead = at > 0 ? BreaksToBlankLine(before2) : string.Empty;
            var after = at < rest.Length ? "\n\n" : "\n";

            moved = at + lead.Length;
            return before2 + lead + body + after + rest.Substring(at);
        }

        /// <summary>見出しのレベルを変えられなかった。下線式（=== / ---）の見出しで、# が無い。</summary>
        public const int LevelNotAtx = -4;

        /// <summary>レベルをずらすと、6 より深くなる（または 1 より浅くなる）見出しがある。</summary>
        public const int LevelOutOfRange = -5;

        /// <summary>見出しのレベルを変えられなかった。引用（&gt;）やリストの中の見出しで、行の頭が # ではない。</summary>
        public const int LevelNotPlain = -6;

        /// <summary>引用・リストの印で始まる行か、4つ以上の空白で字下げした行（リストの続きの中の見出し）。</summary>
        private static readonly Regex QuoteOrListStart = new Regex(@"^( {0,3}(>|[-*+][ \t]|\d{1,9}[.)][ \t])| {4,}|\t)", RegexOptions.Compiled);

        /// <summary># の数を変えられない見出しの行の、理由。引用・リストの中なら LevelNotPlain、それ以外は下線式（LevelNotAtx）。</summary>
        private static int NotAtxReason(string lineText)
        {
            return QuoteOrListStart.IsMatch(lineText) ? LevelNotPlain : LevelNotAtx;
        }

        private static readonly Regex AtxHashes = new Regex(@"^ {0,3}(#{1,6})(?=[ \t]|$)", RegexOptions.Compiled);

        /// <summary>
        /// 見出しの行の # の数を delta だけ増やす・減らす（SPEC 7章 アウトラインの ◀ ▶）。
        /// 変えるのはその行の # だけで、位置も配下の見出しもそのまま。1〜6 の外へは変えない（LevelOutOfRange）。
        /// 下線式の見出しは # が無いので変えない（LevelNotAtx）。引用・リストの中の見出しも変えない（LevelNotPlain）。
        /// 見出しの行でなければ -1。1回の置き換えなので元に戻す1回で戻る。新しいレベルを返す。
        /// </summary>
        public int ChangeHeadingLevel(int headingLine, int delta)
        {
            if (IsReadOnly || headingLine < 1 || headingLine > Document.LineCount) return -1;

            var info = GetLineInfo(headingLine);
            if (info == null || info.Kind != LineKind.Heading || info.ContinuesHeading) return -1;

            var line = Document.GetLineByNumber(headingLine);
            var match = AtxHashes.Match(Document.GetText(line.Offset, line.Length));
            if (!match.Success) return NotAtxReason(Document.GetText(line.Offset, line.Length));

            var hashes = match.Groups[1];
            var level = hashes.Length + delta;
            if (level < 1 || level > 6) return LevelOutOfRange;

            Document.Replace(line.Offset + hashes.Index, hashes.Length, new string('#', level));
            return level;
        }

        // ---------------------------------------------------------------
        // 見出しを足す・消す・名前を変える（SPEC 7章 アウトラインの右クリック）
        //
        // どれも書き換えたあとの文書を読み直し、見出しの顔ぶれが思ったとおりでなければ取りやめる。
        // 1回の書き換えで済ませるので、元に戻す1回で戻る。
        // ---------------------------------------------------------------

        /// <summary>名前が空、または書き換えると見出しとして読めない・ほかの見出しが変わる。</summary>
        public const int HeadingNameInvalid = -7;

        /// <summary>この見出しの名前はここでは変えられない（引用・リストの中、2行以上の下線式の見出し）。</summary>
        public const int HeadingNotEditable = -8;

        /// <summary>
        /// 見出しを足す。headingLine の見出しの節のすぐ後ろに、asChild なら1つ下のレベル（その見出しの中の最後）、
        /// そうでなければ同じレベル（すぐ後ろの兄弟）で。headingLine が 0 以下なら文書の最後に、文書の中で一番浅いレベル（無ければ #）で。
        /// 足した見出しの行番号を返す。
        /// </summary>
        public int InsertHeading(int headingLine, bool asChild, string name)
        {
            if (IsReadOnly) return -1;

            name = OneLine(name);
            if (name.Length == 0) return HeadingNameInvalid;

            var entries = EnsureOutline();
            var text = Document.Text;

            int at, level;
            if (headingLine <= 0)
            {
                at = text.Length;
                level = entries.Count > 0 ? entries.Min(entry => entry.Level) : 1;
            }
            else
            {
                var index = IndexOfHeading(entries, headingLine);
                if (index < 0) return -1;

                var end = SectionEnd(entries, index);
                at = end >= 0 ? Document.GetLineByNumber(entries[end].Line).Offset : text.Length;
                level = entries[index].Level + (asChild ? 1 : 0);
                if (level > 6) return LevelOutOfRange;
            }

            // 足す位置のすぐ後ろが引用・リストの中の見出しなら足さない。引用やリストが途中で切れるため。
            if (at < text.Length && InsideContainer(Document.GetLineByOffset(at).LineNumber)) return HeadingNotEditable;

            if (EndsWithClosingHashes(name)) return HeadingNameInvalid;

            // 足す見出しは、前を空行で区切る。後ろに見出しが続くなら、そこも空行で区切る。
            var before = text.Substring(0, at);
            var lead = at == 0 ? string.Empty : BreaksToBlankLine(before);
            var follows = at < text.Length;
            var inserted = lead + new string('#', level) + " " + name + "\n" + (follows ? "\n" : string.Empty);

            // 足したあとの見出しが、前の見出しに新しい1つを足したものになっているか確かめる。
            // 名前の読み方（** などの書式を外した名前）は、その名前だけの見出しを読んで決める。
            var parsed = MarkdownParser.Headings(new string('#', level) + " " + name);
            if (parsed.Count != 1 || parsed[0].Text.Length == 0) return HeadingNameInvalid;

            var expected = new List<OutlineHeading>(entries) { new OutlineHeading(level, parsed[0].Text, 0) };
            var candidate = before + inserted + text.Substring(at);
            if (!SameHeadings(expected, MarkdownParser.Headings(candidate))) return HeadingNameInvalid;

            Document.Insert(at, inserted);
            return Document.GetLineByOffset(at + lead.Length).LineNumber;
        }

        /// <summary>並べ替える子の見出しが2つ未満。</summary>
        public const int SortTooFew = -9;

        /// <summary>
        /// 見出しの直下の子の見出しを、名前で並べ替える（SPEC 7章。2026-10-27）。孫の見出しと本文は、それぞれの子と一緒に動く。
        /// 直下の子は、アウトラインの木と同じ決め方（親の節の中で、それより前のどの見出しよりも浅いか同じ見出し）。
        /// 最初の子より前の本文は、親のものとしてその場に残す。
        /// 並べる順は CompareNames（数字は数として比べる。ひらがなとカタカナ・全角と半角は区別しない）。同じ名前は元の並びのまま。
        /// 並べ替えたあとの見出しの並びが思ったとおりでなければ取りやめる（MoveChangesMeaning）。
        /// 子が2つ未満なら SortTooFew、引用・リストの中の見出しが境目になるなら LevelNotPlain、並びが変わらなければ MoveUnchanged。
        /// 変わった部分だけを1回で置き換えるので、元に戻すも1回で戻る。成功なら親の見出しの行番号。
        /// </summary>
        public int SortChildren(int headingLine, bool descending)
        {
            if (IsReadOnly) return -1;

            var entries = EnsureOutline();
            var index = IndexOfHeading(entries, headingLine);
            if (index < 0) return -1;

            var end = SectionEnd(entries, index);
            var last = end >= 0 ? end : entries.Count;

            var children = new List<int>();
            var shallowest = int.MaxValue;
            for (var k = index + 1; k < last; k++)
            {
                if (entries[k].Level <= shallowest) children.Add(k);
                shallowest = Math.Min(shallowest, entries[k].Level);
            }

            if (children.Count < 2) return SortTooFew;

            // 引用・リストの中の見出しが切れ目になるなら動かさない。引用やリストが途中で切れて、形が変わるため。
            if (children.Any(k => InsideContainer(entries[k].Line)) || (end >= 0 && InsideContainer(entries[end].Line))) return LevelNotPlain;

            var text = Document.Text;
            var stop = end >= 0 ? Document.GetLineByNumber(entries[end].Line).Offset : text.Length;
            var starts = children.Select(k => Document.GetLineByNumber(entries[k].Line).Offset).ToList();

            // 子ごとの塊（その子の見出しから、次の子の手前まで）。後ろの改行は、塊ではなく場所のものとして分ける。
            var count = children.Count;
            var bodies = new string[count];
            var gaps = new string[count];
            for (var i = 0; i < count; i++)
            {
                var to = i + 1 < count ? starts[i + 1] : stop;
                SplitTrailingBreaks(text.Substring(starts[i], to - starts[i]), out bodies[i], out gaps[i]);
            }

            var names = children.Select(k => HeadingText.Display(entries[k].Text)).ToList();
            var order = descending
                ? Enumerable.Range(0, count).OrderByDescending(i => names[i], NameComparer.Instance).ToList()
                : Enumerable.Range(0, count).OrderBy(i => names[i], NameComparer.Instance).ToList();

            if (order.SequenceEqual(Enumerable.Range(0, count))) return MoveUnchanged;

            var sorted = new StringBuilder();
            for (var slot = 0; slot < count; slot++)
            {
                var gap = gaps[slot];
                if (slot < count - 1 && gap.IndexOf('\n') < 0) gap = "\n";   // 塊のあいだは、少なくとも改行で区切る
                sorted.Append(bodies[order[slot]]).Append(gap);
            }

            var candidate = text.Substring(0, starts[0]) + sorted + text.Substring(stop);

            // 並べ替えたあとの見出しの並び（順番も）が、子の塊ごと並べ替えた形になっているか確かめる。
            var expected = new List<OutlineHeading>(entries.Take(children[0]));
            foreach (var i in order)
            {
                var to = i + 1 < count ? children[i + 1] : last;
                expected.AddRange(entries.Skip(children[i]).Take(to - children[i]));
            }
            expected.AddRange(entries.Skip(last));

            var actual = MarkdownParser.Headings(candidate);
            if (actual.Count != expected.Count) return MoveChangesMeaning;
            for (var i = 0; i < actual.Count; i++)
            {
                if (actual[i].Level != expected[i].Level || actual[i].Text != expected[i].Text) return MoveChangesMeaning;
            }

            // 変わった部分だけを置き換える。
            var prefix = 0;
            var limit = Math.Min(text.Length, candidate.Length);
            while (prefix < limit && text[prefix] == candidate[prefix]) prefix++;

            var suffix = 0;
            while (suffix < limit - prefix
                   && text[text.Length - 1 - suffix] == candidate[candidate.Length - 1 - suffix]) suffix++;

            Document.Replace(prefix, text.Length - prefix - suffix,
                candidate.Substring(prefix, candidate.Length - prefix - suffix));

            return headingLine;
        }

        /// <summary>
        /// 見出しの名前の比べ方（並べ替え）。数字の続きは数として比べる（「2」が「10」より先）。
        /// それ以外は日本語の並び（ひらがなとカタカナ・全角と半角・大文字と小文字は区別しない）。漢字は読みでは並ばない（読み仮名が無いため）。
        /// </summary>
        private sealed class NameComparer : IComparer<string>
        {
            public static readonly NameComparer Instance = new NameComparer();

            private static readonly System.Globalization.CompareInfo Japanese = new System.Globalization.CultureInfo("ja-JP").CompareInfo;

            private const System.Globalization.CompareOptions Options = System.Globalization.CompareOptions.IgnoreCase
                | System.Globalization.CompareOptions.IgnoreKanaType | System.Globalization.CompareOptions.IgnoreWidth;

            public int Compare(string a, string b)
            {
                a = a ?? string.Empty;
                b = b ?? string.Empty;

                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    var digitA = char.IsDigit(a[i]);
                    var digitB = char.IsDigit(b[j]);

                    var endA = i;
                    while (endA < a.Length && char.IsDigit(a[endA]) == digitA) endA++;
                    var endB = j;
                    while (endB < b.Length && char.IsDigit(b[endB]) == digitB) endB++;

                    int result;
                    if (digitA && digitB)
                    {
                        result = CompareNumbers(a.Substring(i, endA - i), b.Substring(j, endB - j));
                    }
                    else
                    {
                        result = Japanese.Compare(a.Substring(i, endA - i), b.Substring(j, endB - j), Options);
                    }

                    if (result != 0) return result;

                    i = endA;
                    j = endB;
                }

                return (a.Length - i).CompareTo(b.Length - j);
            }

            /// <summary>数字の続きを、数として比べる（全角の数字も）。桁が多いほうが大きい。</summary>
            private static int CompareNumbers(string a, string b)
            {
                var x = Digits(a);
                var y = Digits(b);
                if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
                return string.CompareOrdinal(x, y);
            }

            private static string Digits(string run)
            {
                var digits = new StringBuilder(run.Length);
                foreach (var c in run) digits.Append((char)('0' + (int)char.GetNumericValue(c)));
                var text = digits.ToString().TrimStart('0');
                return text.Length == 0 ? "0" : text;
            }
        }

        /// <summary>見出しの節（中身と小見出しも含む）を消す。消せたら 0。</summary>
        public int DeleteSection(int headingLine)
        {
            if (IsReadOnly) return -1;

            var entries = EnsureOutline();
            var index = IndexOfHeading(entries, headingLine);
            if (index < 0) return -1;

            var end = SectionEnd(entries, index);
            var text = Document.Text;
            var start = Document.GetLineByNumber(entries[index].Line).Offset;
            var stop = end >= 0 ? Document.GetLineByNumber(entries[end].Line).Offset : text.Length;

            // 引用・リストの中の見出しが切れ目になるなら消さない。引用やリストが途中で切れて、形が変わるため。
            if (InsideContainer(entries[index].Line) || (end >= 0 && InsideContainer(entries[end].Line))) return HeadingNotEditable;

            // 消した跡で、前の文章と後ろの文章がくっつかないよう、あいだに空行を残す
            // （くっつくと、後ろの下線式の見出しが前の段落まで取り込んでしまう）。
            var gap = start > 0 && stop < text.Length && !PrecededByBlankLine(start) ? "\n" : string.Empty;

            // 消したあとに残るはずの見出し（消す範囲の外のもの）と、実際に読み直した見出しが同じか確かめる。
            var last = end >= 0 ? end : entries.Count;
            var expected = entries.Where((entry, k) => k < index || k >= last).ToList();
            var candidate = text.Substring(0, start) + gap + text.Substring(stop);
            if (!SameHeadings(expected, MarkdownParser.Headings(candidate))) return MoveChangesMeaning;

            Document.Replace(start, stop - start, gap);
            return 0;
        }

        /// <summary>
        /// 見出しの名前の部分（# と空白の後ろ、閉じの # の手前）。本文に書いてあるとおりの文字（** などの書式も含む）。
        /// # の見出しと、1行の下線式の見出しだけ。それ以外は null（HeadingNotEditable）。
        /// </summary>
        public string HeadingName(int headingLine)
        {
            int start, length;
            return HeadingNameRange(headingLine, out start, out length);
        }

        /// <summary>見出しの名前を書き換える。ほかの見出しもレベルも変わらないときだけ。見出しの行番号を返す。</summary>
        public int RenameHeading(int headingLine, string name)
        {
            if (IsReadOnly) return -1;

            name = OneLine(name);
            if (name.Length == 0) return HeadingNameInvalid;

            var entries = EnsureOutline();
            var index = IndexOfHeading(entries, headingLine);
            if (index < 0) return -1;

            int start, length;
            if (HeadingNameRange(headingLine, out start, out length) == null) return HeadingNotEditable;

            if (EndsWithClosingHashes(name)) return HeadingNameInvalid;

            // 下線式の見出しの名前を、行の頭が別の意味になる文字（# > - + * 1.）で始めると、見出しの形が変わる
            // （「## 名前」にすると # の見出しと横線になる）。# の見出しなら、名前の中なのでかまわない。
            var line = Document.GetLineByNumber(headingLine);
            if (!AtxHeadingLine.IsMatch(Document.GetText(line.Offset, line.Length)) && BlockStart.IsMatch(name)) return HeadingNameInvalid;

            var text = Document.Text;
            if (start > 0 && text[start - 1] == '#') name = " " + name;   // 名前の無かった見出し。# の直後に空白が要る

            var candidate = text.Substring(0, start) + name + text.Substring(start + length);
            var after = MarkdownParser.Headings(candidate);
            if (after.Count != entries.Count) return HeadingNameInvalid;

            for (var i = 0; i < after.Count; i++)
            {
                if (after[i].Level != entries[i].Level) return HeadingNameInvalid;
                if (i != index && after[i].Text != entries[i].Text) return HeadingNameInvalid;
                if (i == index && after[i].Text.Length == 0) return HeadingNameInvalid;
            }

            Document.Replace(start, length, name);
            return headingLine;
        }

        private string HeadingNameRange(int headingLine, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (headingLine < 1 || headingLine > Document.LineCount) return null;

            var line = Document.GetLineByNumber(headingLine);
            var text = Document.GetText(line.Offset, line.Length);

            var atx = AtxHeadingLine.Match(text);
            if (atx.Success)
            {
                var content = atx.Groups[2];
                if (!content.Success)
                {
                    // 名前の無い見出し（「##」だけ）。# の後ろ全部を名前の場所にする（書くときに空白を足す）。
                    start = line.Offset + atx.Groups[1].Length;
                    length = text.Length - atx.Groups[1].Length;
                    return string.Empty;
                }

                start = line.Offset + content.Index;
                length = content.Length;
                return content.Value;
            }

            var next = line.NextLine;
            if (next != null && SetextUnderline.IsMatch(Document.GetText(next.Offset, next.Length))
                && !QuoteOrListStart.IsMatch(text))
            {
                // 1行の下線式の見出し。前の行が見出しの続き（2行以上）なら変えない。
                var info = GetLineInfo(headingLine);
                if (info != null && info.ContinuesHeading) return null;

                var trimmed = text.Trim();
                if (trimmed.Length == 0) return null;
                start = line.Offset + text.IndexOf(trimmed, StringComparison.Ordinal);
                length = trimmed.Length;
                return trimmed;
            }

            return null;
        }

        /// <summary># の見出しの行。2つ目のかたまりが名前（閉じの # と前後の空白は含めない）。</summary>
        private static readonly Regex AtxHeadingLine = new Regex(@"^( {0,3}#{1,6})(?:[ \t]+(.*?))??(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);

        private static readonly Regex SetextUnderline = new Regex(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.Compiled);

        /// <summary>行の頭に置くと、段落ではなく別のもの（見出し・引用・リスト）になる書き出し。</summary>
        private static readonly Regex BlockStart = new Regex(@"^(#|>|[-*+]([ \t]|$)|\d{1,9}[.)]([ \t]|$))", RegexOptions.Compiled);

        /// <summary>
        /// 名前の終わりが「空白 + #」（または # だけ）。# の見出しでは閉じの # とみなされて消えるので、その名前では作らない・変えない。
        /// </summary>
        private static bool EndsWithClosingHashes(string name)
        {
            return Regex.IsMatch(name, @"(^|[ \t])#+$");
        }

        /// <summary>その行が引用・リストの中か（印で始まる・4つ以上字下げしている）。</summary>
        private bool InsideContainer(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Document.LineCount) return false;

            var line = Document.GetLineByNumber(lineNumber);
            return QuoteOrListStart.IsMatch(Document.GetText(line.Offset, line.Length));
        }

        /// <summary>見出しの名前は1行。改行が入っていたら空白にし、前後の空白を外す。</summary>
        private static string OneLine(string name)
        {
            return (name ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        /// <summary>text の後ろに見出しを置くとき、空行1つ（改行2つ）にするために足りない改行。</summary>
        private static string BreaksToBlankLine(string text)
        {
            var breaks = 0;
            for (var i = text.Length - 1; i >= 0 && (text[i] == '\n' || text[i] == '\r'); i--)
            {
                if (text[i] == '\n') breaks++;
            }

            return breaks >= 2 ? string.Empty : new string('\n', 2 - breaks);
        }

        /// <summary>start の直前の行が空行か（文書の先頭なら真）。</summary>
        private bool PrecededByBlankLine(int start)
        {
            var line = Document.GetLineByOffset(start).PreviousLine;
            return line == null || Document.GetText(line.Offset, line.Length).Trim().Length == 0;
        }

        /// <summary>見出しの顔ぶれ（レベルと名前）が、並び順を問わず同じか。</summary>
        private static bool SameHeadings(List<OutlineHeading> before, List<OutlineHeading> after)
        {
            if (before.Count != after.Count) return false;

            var names = new List<string>();
            foreach (var entry in before) names.Add(entry.Level + ":" + entry.Text);

            foreach (var entry in after)
            {
                if (!names.Remove(entry.Level + ":" + entry.Text)) return false;
            }

            return true;
        }

        private static int IndexOfHeading(List<OutlineHeading> entries, int line)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Line == line) return i;
            }

            return -1;
        }

        /// <summary>節の終わり。同じかそれより浅い次の見出しの番号。文書の終わりまでなら -1。</summary>
        private static int SectionEnd(List<OutlineHeading> entries, int index)
        {
            for (var j = index + 1; j < entries.Count; j++)
            {
                if (entries[j].Level <= entries[index].Level) return j;
            }

            return -1;
        }

        private static void SplitTrailingBreaks(string text, out string body, out string gap)
        {
            var cut = text.Length;
            while (cut > 0 && (text[cut - 1] == '\n' || text[cut - 1] == '\r')) cut--;

            body = text.Substring(0, cut);
            gap = text.Substring(cut);
        }
    }
}
