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
    // MarkdownView の一部: 見出しの節を動かす・レベルを変える・同じ階層を回す・消す（アウトラインから呼ぶ）。
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

            List<KeyValuePair<int, int>> definitions;
            var places = PlacesNow(entries, text, out definitions);
            if (places == null) return -1;

            // 引用・リスト・前付けの中の見出しが切れ目になるなら動かさない。引用やリストが途中で切れて、形が変わるため。
            if (!Plain(places, entries[index].Line) || (end >= 0 && !Plain(places, entries[end].Line))
                || (beforeLine > 0 && !Plain(places, beforeLine))) return LevelNotPlain;

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

            // 動かしたあとの見出しの顔ぶれ（置き場所も）が、レベルをずらした分だけ変わった形になっているか確かめる。
            if (!SameHeadings(expected, places, candidate)) return MoveChangesMeaning;

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

        // 見出しのレベルを変える（アウトラインの右クリック）は、MoveSectionTo をその場に置いたまま使う（MainWindow.ChangeHeadingLevel）。
        // その行の # だけを変える ChangeHeadingLevel は、使われなくなったので外した（2026-11-06）。

        // ---------------------------------------------------------------
        // 同じ階層の見出しを回す・見出しを消す（SPEC 7章 アウトライン）
        //
        // どれも書き換えたあとの文書を読み直し、見出しの顔ぶれが思ったとおりでなければ取りやめる。
        // 1回の書き換えで済ませるので、元に戻す1回で戻る。
        // 見出しを足す（InsertHeading）・名前を変える（RenameHeading）は、アウトラインの名前の入力と一緒に外した（2026-11-06）。
        // 子を名前で並べ替える（SortChildren。右クリックの昇順・降順）は、使われなかったので回すボタンに置き換えた（2026-11-06）。
        // ---------------------------------------------------------------

        /// <summary>引用・リストの中の見出しが節の切れ目になるので、消せない（DeleteSection）。</summary>
        public const int HeadingNotEditable = -8;

        /// <summary>回す見出しが2つ未満。</summary>
        public const int SiblingsTooFew = -9;

        /// <summary>
        /// その見出しと同じ階層の見出し（同じ親の直下の子。一番上の階層なら文書の一番上の見出したち）を、1つずつ回す（SPEC 7章。2026-11-06）。
        /// up なら先頭が末尾へ（みな1つ上がる）、そうでなければ末尾が先頭へ。孫の見出しと本文は、それぞれの見出しと一緒に動く。
        /// 同じ階層の決め方はアウトラインの木と同じ（親は自分より浅い直前の見出し。その節の中で、それより前のどの見出しよりも浅いか同じ見出し）。
        /// 木の上で同じ段に並んでいても、レベルの違う見出しは一緒に回さない（2026-11-07）。
        /// 「## IPv6」のあとに「# 次にやること」「# プライベート」…と続くと、木ではどれも一番上の段に並ぶ。
        /// これを一緒に回すと、IPv6 が末尾の「#」の見出しの後ろへ行って、その子になってしまう（並びは確かめで通ってしまう）。
        /// 同じ段の見出しは、後ろほど浅いか同じなので、同じレベルが続く所だけを回す。後ろの浅い見出しからは動かさない。
        /// 最初の見出しより前の本文（親の本文・文書の頭の文章）は、その場に残す。
        /// 回したあとの見出しの並びが思ったとおりでなければ取りやめる（MoveChangesMeaning）。
        /// 2つ未満なら SiblingsTooFew、引用・リストの中の見出しが境目になるなら LevelNotPlain。
        /// 変わった部分だけを1回で置き換えるので、元に戻すも1回で戻る。成功なら、回したあとの先頭の見出しの行番号。
        /// </summary>
        public int RotateSiblings(int headingLine, bool up)
        {
            if (IsReadOnly) return -1;

            var entries = EnsureOutline();
            var index = IndexOfHeading(entries, headingLine);
            if (index < 0) return -1;

            var parent = -1;
            for (var k = index - 1; k >= 0; k--)
            {
                if (entries[k].Level < entries[index].Level) { parent = k; break; }
            }

            var end = parent >= 0 ? SectionEnd(entries, parent) : -1;
            var last = end >= 0 ? end : entries.Count;

            var children = new List<int>();
            var shallowest = int.MaxValue;
            for (var k = parent + 1; k < last; k++)
            {
                if (entries[k].Level <= shallowest) children.Add(k);
                shallowest = Math.Min(shallowest, entries[k].Level);
            }

            if (!children.Contains(index)) return -1;

            // 同じレベルの見出しだけにする。その後ろに浅い見出しがあれば、そこを終わりにする（そこから後ろは動かさない）。
            var level = entries[index].Level;
            var shallower = children.Where(k => entries[k].Level < level).ToList();
            children = children.Where(k => entries[k].Level == level).ToList();
            if (shallower.Count > 0)
            {
                end = shallower[0];
                last = end;
            }

            if (children.Count < 2) return SiblingsTooFew;

            var text = Document.Text;
            List<KeyValuePair<int, int>> definitions;
            var places = PlacesNow(entries, text, out definitions);
            if (places == null) return -1;

            // 引用・リスト・前付けの中の見出しが切れ目になるなら動かさない。引用やリストが途中で切れて、形が変わるため。
            if (children.Any(k => !Plain(places, entries[k].Line)) || (end >= 0 && !Plain(places, entries[end].Line))) return LevelNotPlain;
            var stop = end >= 0 ? Document.GetLineByNumber(entries[end].Line).Offset : text.Length;
            var starts = children.Select(k => Document.GetLineByNumber(entries[k].Line).Offset).ToList();

            // 見出しごとの塊（その見出しから、次の同じ階層の見出しの手前まで）。後ろの改行は、塊ではなく場所のものとして分ける。
            var count = children.Count;
            var bodies = new string[count];
            var gaps = new string[count];
            for (var i = 0; i < count; i++)
            {
                var to = i + 1 < count ? starts[i + 1] : stop;
                SplitTrailingBreaks(text.Substring(starts[i], to - starts[i]), out bodies[i], out gaps[i]);
            }

            var order = up
                ? Enumerable.Range(1, count - 1).Concat(new[] { 0 }).ToList()
                : new[] { count - 1 }.Concat(Enumerable.Range(0, count - 1)).ToList();

            var rotated = new StringBuilder();
            for (var slot = 0; slot < count; slot++)
            {
                var gap = gaps[slot];
                if (slot < count - 1 && gap.IndexOf('\n') < 0) gap = "\n";   // 塊のあいだは、少なくとも改行で区切る
                rotated.Append(bodies[order[slot]]).Append(gap);
            }

            var candidate = text.Substring(0, starts[0]) + rotated + text.Substring(stop);

            // 回したあとの見出しの並び（順番も）が、塊ごと回した形になっているか確かめる。
            var expected = new List<OutlineHeading>(entries.Take(children[0]));
            foreach (var i in order)
            {
                var to = i + 1 < count ? children[i + 1] : last;
                expected.AddRange(entries.Skip(children[i]).Take(to - children[i]));
            }
            expected.AddRange(entries.Skip(last));

            // 置き場所（文書の直下・リストの中・引用の中…）も比べる。並びとレベルが同じでも、見出しがリストに入ったり出たりすることがあるため（2026-11-07）。
            var started = PerfLog.Start();
            Dictionary<int, string> after;
            List<KeyValuePair<int, int>> unused;
            var actual = MarkdownParser.Headings(candidate, out after, out unused);
            PerfLog.Step("確かめの解析", started);
            if (actual.Count != expected.Count) return MoveChangesMeaning;
            for (var i = 0; i < actual.Count; i++)
            {
                if (actual[i].Level != expected[i].Level || actual[i].Text != expected[i].Text) return MoveChangesMeaning;
                if (PlaceOf(after, actual[i].Line) != PlaceOf(places, expected[i].Line)) return MoveChangesMeaning;
            }

            // 変わった部分だけを置き換える。
            var prefix = 0;
            var limit = Math.Min(text.Length, candidate.Length);
            while (prefix < limit && text[prefix] == candidate[prefix]) prefix++;

            var suffix = 0;
            while (suffix < limit - prefix
                   && text[text.Length - 1 - suffix] == candidate[candidate.Length - 1 - suffix]) suffix++;

            PerfLog.StepNote("塊", count);
            PerfLog.StepNote("書き換える文字", text.Length - prefix - suffix);
            started = PerfLog.Start();
            Document.Replace(prefix, text.Length - prefix - suffix,
                candidate.Substring(prefix, candidate.Length - prefix - suffix));
            PerfLog.Step("書き換え", started);   // 書き換えの後始末（OnTextChanged）も含む

            // 先頭の見出しの手前は書き換えていないので、先頭の行番号は回す前と同じ。
            return entries[children[0]].Line;
        }

        /// <summary>
        /// 見出しの節（中身と小見出しも含む）を消す。消せたら 0、リンクの定義・脚注の定義を残して消したら DefinitionsKept。
        /// 節の中のリンクの定義（[1]: …）と脚注の定義（[^1]: …）は、消した跡に残す（2026-11-07）。
        /// 文書の末尾にまとめて書いた定義は最後の節の中身になり、最後の節を消すと、ほかの節のリンクがただの文字になっていたため。
        /// </summary>
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

            List<KeyValuePair<int, int>> definitions;
            var places = PlacesNow(entries, text, out definitions);
            if (places == null) return -1;

            // 引用・リスト・前付けの中の見出しが切れ目になるなら消さない。引用やリストが途中で切れて、形が変わるため。
            if (!Plain(places, entries[index].Line) || (end >= 0 && !Plain(places, entries[end].Line))) return HeadingNotEditable;

            // 節の中のリンクの定義・脚注の定義は残す（書いてあった順に、空行で区切って）。
            var kept = definitions
                .Where(each => each.Key >= start && each.Key < stop)
                .GroupBy(each => each.Key).Select(each => each.First())
                .OrderBy(each => each.Key)
                .Select(each => text.Substring(each.Key, Math.Min(each.Value, stop - each.Key)).TrimEnd('\r', '\n'))
                .ToList();

            // 消した跡で、前の文章と後ろの文章がくっつかないよう、あいだに空行を残す
            // （くっつくと、後ろの下線式の見出しが前の段落まで取り込んでしまう）。
            string gap;
            if (kept.Count == 0)
            {
                gap = start > 0 && stop < text.Length && !PrecededByBlankLine(start) ? "\n" : string.Empty;
            }
            else
            {
                gap = (start > 0 && !PrecededByBlankLine(start) ? "\n" : string.Empty)
                      + string.Join("\n\n", kept) + "\n" + (stop < text.Length ? "\n" : string.Empty);
            }

            // 消したあとに残るはずの見出し（消す範囲の外のもの。置き場所も）と、実際に読み直した見出しが同じか確かめる。
            var last = end >= 0 ? end : entries.Count;
            var expected = entries.Where((entry, k) => k < index || k >= last).ToList();
            var candidate = text.Substring(0, start) + gap + text.Substring(stop);
            if (!SameHeadings(expected, places, candidate)) return MoveChangesMeaning;

            Document.Replace(start, stop - start, gap);
            return kept.Count == 0 ? 0 : DefinitionsKept;
        }

        /// <summary>節を消した。中にあったリンクの定義・脚注の定義は残した。</summary>
        public const int DefinitionsKept = 1;

        /// <summary>
        /// 今の本文の見出しの置き場所（行番号 → 置き場所。MarkdownParser.Headings）。
        /// 本文を読み直した見出しが、アウトライン（entries）と行番号まで同じでなければ null（2026-11-07）。
        /// アウトラインは近道の解析で直したものなので、もし食い違っていれば、ずれた行で切り貼りして本文を壊すため。
        /// </summary>
        private static Dictionary<int, string> PlacesNow(List<OutlineHeading> entries, string text, out List<KeyValuePair<int, int>> definitions)
        {
            Dictionary<int, string> places;
            var now = MarkdownParser.Headings(text, out places, out definitions);
            if (now.Count != entries.Count) return null;

            for (var i = 0; i < now.Count; i++)
            {
                if (now[i].Level != entries[i].Level || now[i].Text != entries[i].Text || now[i].Line != entries[i].Line) return null;
            }

            return places;
        }

        /// <summary>その行の見出しが文書の直下にあるか（リスト・引用・脚注・前付けの中ではない）。</summary>
        private static bool Plain(Dictionary<int, string> places, int line)
        {
            return PlaceOf(places, line).Length == 0;
        }

        private static string PlaceOf(Dictionary<int, string> places, int line)
        {
            string place;
            return places.TryGetValue(line, out place) ? place : "?";
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

        /// <summary>
        /// 書き換えたあと（candidate）の見出しの顔ぶれ（レベル・名前・置き場所）が、並び順を問わず expected と同じか。
        /// expected の置き場所は、書き換える前の行番号で places から引く。
        /// 置き場所も比べるのは、字下げした見出しがリストの項目に入ったり出たりしても、レベルと名前は変わらないため（2026-11-07）。
        /// </summary>
        private static bool SameHeadings(List<OutlineHeading> expected, Dictionary<int, string> places, string candidate)
        {
            Dictionary<int, string> after;
            List<KeyValuePair<int, int>> unused;
            var actual = MarkdownParser.Headings(candidate, out after, out unused);
            if (expected.Count != actual.Count) return false;

            var names = new List<string>();
            foreach (var entry in expected) names.Add(entry.Level + ":" + PlaceOf(places, entry.Line) + ":" + entry.Text);

            foreach (var entry in actual)
            {
                if (!names.Remove(entry.Level + ":" + PlaceOf(after, entry.Line) + ":" + entry.Text)) return false;
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
