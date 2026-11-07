// MarkdownView.Sections.cs の写し（AvalonEdit の文書を文字列に置き換えたもの）。本体を変えたら、ここも合わせる。
// Port of Tsuzuri/Views/MarkdownView.Sections.cs (MoveSectionTo, Relocate, RotateSiblings, DeleteSection + helpers)
// onto a plain string. AvalonEdit TextDocument is replaced by Doc (same line model: delimiters \r\n, \n, \r;
// line Length excludes the delimiter; GetLineByOffset of an offset inside a delimiter belongs to that line).
// EnsureOutline() is replaced by MarkdownParser.Headings(text) (the view's Model is a full parse or a patch that
// parse-fuzz proves equal to the full parse; MarkdownParser.Headings splits only on '\n', which matches AvalonEdit as
// long as there is no lone '\r' in the text -- the generator never produces lone '\r').
// Document.Replace(prefix, len, mid) of the real code always yields exactly `candidate`; the port returns candidate.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tsuzuri.Markdown;

public sealed class Doc
{
    public readonly string Text;
    public readonly List<int> Starts = new List<int>();
    public readonly List<int> Lengths = new List<int>();
    public Doc(string text)
    {
        Text = text;
        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                Starts.Add(start); Lengths.Add(i - start);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                start = i;
                continue;
            }
            i++;
        }
        Starts.Add(start); Lengths.Add(text.Length - start);
    }
    public int LineCount => Starts.Count;
    public int TextLength => Text.Length;
    public int Offset(int lineNumber) => Starts[lineNumber - 1];
    public int Length(int lineNumber) => Lengths[lineNumber - 1];
    public string LineText(int lineNumber) => Text.Substring(Starts[lineNumber - 1], Lengths[lineNumber - 1]);
    public int LineNumberOfOffset(int offset)
    {
        int lo = 0, hi = Starts.Count - 1;
        while (lo < hi) { int m = (lo + hi + 1) / 2; if (Starts[m] <= offset) lo = m; else hi = m - 1; }
        return lo + 1;
    }
}

public static class Port
{
    public const int MoveChangesMeaning = -2;
    public const int MoveUnchanged = -3;
    public const int LevelNotAtx = -4;
    public const int LevelOutOfRange = -5;
    public const int LevelNotPlain = -6;
    public const int HeadingNotEditable = -8;
    public const int SiblingsTooFew = -9;

    static readonly Regex QuoteOrListStart = new Regex(@"^( {0,3}(>|[-*+][ \t]|\d{1,9}[.)][ \t])| {4,}|\t)", RegexOptions.Compiled);
    static readonly Regex AtxHashes = new Regex(@"^ {0,3}(#{1,6})(?=[ \t]|$)", RegexOptions.Compiled);

    static int NotAtxReason(string lineText) => QuoteOrListStart.IsMatch(lineText) ? LevelNotPlain : LevelNotAtx;

    public static int MoveSectionTo(string text0, int headingLine, int beforeLine, int newLevel, out string result)
    {
        result = text0;
        var Document = new Doc(text0);
        var entries = MarkdownParser.Headings(text0);
        var index = IndexOfHeading(entries, headingLine);
        if (index < 0) return -1;

        var end = SectionEnd(entries, index);
        var text = Document.Text;
        var start = Document.Offset(entries[index].Line);
        var stop = end >= 0 ? Document.Offset(entries[end].Line) : text.Length;

        int target;
        if (beforeLine < 0) target = text.Length;
        else
        {
            var before = IndexOfHeading(entries, beforeLine);
            if (before < 0) return -1;
            target = Document.Offset(entries[before].Line);
        }

        if (target > start && target < stop) return -1;

        List<KeyValuePair<int,int>> defs0; var places = PlacesNow(entries, text0, out defs0); if (places == null) return -1;
        if (!Plain(places, entries[index].Line) || (end >= 0 && !Plain(places, entries[end].Line))
            || (beforeLine > 0 && !Plain(places, beforeLine))) return LevelNotPlain;

        var inPlace = target == start || target == stop;
        if (inPlace && (newLevel < 1 || newLevel > 6 || newLevel == entries[index].Level)) return MoveUnchanged;

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

                var lo = Document.Offset(entries[k].Line); var ll = Document.Length(entries[k].Line);
                var match = AtxHashes.Match(text.Substring(lo, ll));
                if (!match.Success) return NotAtxReason(text.Substring(lo, ll));

                var hashes = match.Groups[1];
                section.Remove(lo - start + hashes.Index, hashes.Length);
                section.Insert(lo - start + hashes.Index, new string('#', level));

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

        if (!SameHeadings(expected, places, candidate)) return MoveChangesMeaning;

        result = candidate;
        var nd = new Doc(candidate);
        return nd.LineNumberOfOffset(Math.Min(moved, nd.TextLength));
    }

    public static string Relocate(string text, string sectionText, int start, int stop, int target, out int moved)
    {
        string body, gap;
        SplitTrailingBreaks(sectionText, out body, out gap);

        var head = text.Substring(0, start);
        var tail = text.Substring(stop);
        if (head.Length > 0 && tail.Length > 0) head += BreaksToBlankLine(head);

        var rest = head + tail;
        var at = target < start ? target : head.Length + (target - stop);

        var before2 = rest.Substring(0, at);
        var lead = at > 0 ? BreaksToBlankLine(before2) : string.Empty;
        var after = at < rest.Length ? "\n\n" : "\n";

        moved = at + lead.Length;
        return before2 + lead + body + after + rest.Substring(at);
    }

    public static int RotateSiblings(string text0, int headingLine, bool up, out string result, out RotateInfo info)
    {
        result = text0; info = null;
        var Document = new Doc(text0);
        var entries = MarkdownParser.Headings(text0);
        var index = IndexOfHeading(entries, headingLine);
        if (index < 0) return -1;

        var parent = -1;
        for (var k = index - 1; k >= 0; k--)
            if (entries[k].Level < entries[index].Level) { parent = k; break; }

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

        var level = entries[index].Level;
        var shallower = children.Where(k => entries[k].Level < level).ToList();
        children = children.Where(k => entries[k].Level == level).ToList();
        if (shallower.Count > 0) { end = shallower[0]; last = end; }

        if (children.Count < 2) return SiblingsTooFew;

        List<KeyValuePair<int,int>> defs0; var places = PlacesNow(entries, text0, out defs0); if (places == null) return -1;
        if (children.Any(k => !Plain(places, entries[k].Line)) || (end >= 0 && !Plain(places, entries[end].Line))) return LevelNotPlain;

        var text = Document.Text;
        var stop = end >= 0 ? Document.Offset(entries[end].Line) : text.Length;
        var starts = children.Select(k => Document.Offset(entries[k].Line)).ToList();

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
            if (slot < count - 1 && gap.IndexOf('\n') < 0) gap = "\n";
            rotated.Append(bodies[order[slot]]).Append(gap);
        }

        var candidate = text.Substring(0, starts[0]) + rotated + text.Substring(stop);

        var expected = new List<OutlineHeading>(entries.Take(children[0]));
        foreach (var i in order)
        {
            var to = i + 1 < count ? children[i + 1] : last;
            expected.AddRange(entries.Skip(children[i]).Take(to - children[i]));
        }
        expected.AddRange(entries.Skip(last));

        info = new RotateInfo { Children = children, Last = last, Stop = stop, FirstStart = starts[0], Order = order, Entries = entries };

        Dictionary<int,string> after; List<KeyValuePair<int,int>> un;
        var actual = MarkdownParser.Headings(candidate, out after, out un);
        if (actual.Count != expected.Count) return MoveChangesMeaning;
        for (var i = 0; i < actual.Count; i++)
        {
            if (actual[i].Level != expected[i].Level || actual[i].Text != expected[i].Text) return MoveChangesMeaning;
            if (PlaceOf(after, actual[i].Line) != PlaceOf(places, expected[i].Line)) return MoveChangesMeaning;
        }

        result = candidate;
        return entries[children[0]].Line;
    }

    public sealed class RotateInfo
    {
        public List<int> Children; public int Last; public int Stop; public int FirstStart; public List<int> Order; public List<OutlineHeading> Entries;
    }

    public static int DeleteSection(string text0, int headingLine, out string result, out int startOut, out int stopOut, out string gapOut)
    {
        result = text0; startOut = stopOut = -1; gapOut = null;
        var Document = new Doc(text0);
        var entries = MarkdownParser.Headings(text0);
        var index = IndexOfHeading(entries, headingLine);
        if (index < 0) return -1;

        var end = SectionEnd(entries, index);
        var text = Document.Text;
        var start = Document.Offset(entries[index].Line);
        var stop = end >= 0 ? Document.Offset(entries[end].Line) : text.Length;

        List<KeyValuePair<int,int>> definitions; var places = PlacesNow(entries, text0, out definitions); if (places == null) return -1;
        if (!Plain(places, entries[index].Line) || (end >= 0 && !Plain(places, entries[end].Line))) return HeadingNotEditable;
        var kept = definitions.Where(each => each.Key >= start && each.Key < stop).GroupBy(each => each.Key).Select(each => each.First())
            .OrderBy(each => each.Key).Select(each => text.Substring(each.Key, Math.Min(each.Value, stop - each.Key)).TrimEnd('\r', '\n')).ToList();
        string gap;
        if (kept.Count == 0) gap = start > 0 && stop < text.Length && !PrecededByBlankLine(Document, start) ? "\n" : string.Empty;
        else gap = (start > 0 && !PrecededByBlankLine(Document, start) ? "\n" : string.Empty) + string.Join("\n\n", kept) + "\n" + (stop < text.Length ? "\n" : string.Empty);
        KeptDefs = kept.Count;

        var last = end >= 0 ? end : entries.Count;
        var expected = entries.Where((entry, k) => k < index || k >= last).ToList();
        var candidate = text.Substring(0, start) + gap + text.Substring(stop);
        if (!SameHeadings(expected, places, candidate)) return MoveChangesMeaning;

        startOut = start; stopOut = stop; gapOut = gap;
        result = candidate;
        return 0;
    }

    static bool InsideContainer(Doc Document, int lineNumber)
    {
        if (lineNumber < 1 || lineNumber > Document.LineCount) return false;
        return QuoteOrListStart.IsMatch(Document.LineText(lineNumber));
    }

    public static string BreaksToBlankLine(string text)
    {
        var breaks = 0;
        for (var i = text.Length - 1; i >= 0 && (text[i] == '\n' || text[i] == '\r'); i--)
            if (text[i] == '\n') breaks++;
        return breaks >= 2 ? string.Empty : new string('\n', 2 - breaks);
    }

    static bool PrecededByBlankLine(Doc Document, int start)
    {
        var ln = Document.LineNumberOfOffset(start);
        if (ln <= 1) return true;   // PreviousLine == null
        return Document.LineText(ln - 1).Trim().Length == 0;
    }

    public static int KeptDefs;
    static Dictionary<int,string> PlacesNow(List<OutlineHeading> entries, string text, out List<KeyValuePair<int,int>> definitions)
    {
        Dictionary<int,string> places;
        var now = MarkdownParser.Headings(text, out places, out definitions);
        if (now.Count != entries.Count) return null;
        for (var i = 0; i < now.Count; i++)
            if (now[i].Level != entries[i].Level || now[i].Text != entries[i].Text || now[i].Line != entries[i].Line) return null;
        return places;
    }
    static bool Plain(Dictionary<int,string> p, int line) => PlaceOf(p, line).Length == 0;
    static string PlaceOf(Dictionary<int,string> p, int line) { string x; return p.TryGetValue(line, out x) ? x : "?"; }
    static bool SameHeadings(List<OutlineHeading> expected, Dictionary<int,string> places, string candidate)
    {
        Dictionary<int,string> after; List<KeyValuePair<int,int>> un;
        var actual = MarkdownParser.Headings(candidate, out after, out un);
        if (expected.Count != actual.Count) return false;
        var names = new List<string>();
        foreach (var e in expected) names.Add(e.Level + ":" + PlaceOf(places, e.Line) + ":" + e.Text);
        foreach (var e in actual) if (!names.Remove(e.Level + ":" + PlaceOf(after, e.Line) + ":" + e.Text)) return false;
        return true;
    }
    static bool SameHeadingsOld(List<OutlineHeading> before, List<OutlineHeading> after)
    {
        if (before.Count != after.Count) return false;
        var names = new List<string>();
        foreach (var entry in before) names.Add(entry.Level + ":" + entry.Text);
        foreach (var entry in after)
            if (!names.Remove(entry.Level + ":" + entry.Text)) return false;
        return true;
    }

    public static int IndexOfHeading(List<OutlineHeading> entries, int line)
    {
        for (var i = 0; i < entries.Count; i++) if (entries[i].Line == line) return i;
        return -1;
    }

    public static int SectionEnd(List<OutlineHeading> entries, int index)
    {
        for (var j = index + 1; j < entries.Count; j++) if (entries[j].Level <= entries[index].Level) return j;
        return -1;
    }

    public static void SplitTrailingBreaks(string text, out string body, out string gap)
    {
        var cut = text.Length;
        while (cut > 0 && (text[cut - 1] == '\n' || text[cut - 1] == '\r')) cut--;
        body = text.Substring(0, cut);
        gap = text.Substring(cut);
    }

    // ---- MainWindow.OutlineDrag.DropTarget / SectionEndIndex / ShallowestOutside (UI argument derivation) ----
    public enum Drop { Before, After, Into, End }

    public static bool DropTarget(List<OutlineHeading> entries, int from, Drop kind, int anchor, out int beforeIndex, out int level)
    {
        beforeIndex = entries.Count;
        level = entries[from].Level;
        var end = SectionEndIndex(entries, from);
        switch (kind)
        {
            case Drop.Before: beforeIndex = anchor; level = entries[anchor].Level; break;
            case Drop.After:
                if (anchor > from && anchor < end) return false;
                beforeIndex = SectionEndIndex(entries, anchor); level = entries[anchor].Level; break;
            case Drop.Into:
                if (anchor >= from && anchor < end) return false;
                beforeIndex = SectionEndIndex(entries, anchor); level = entries[anchor].Level + 1; break;
            default: level = ShallowestOutside(entries, from); break;
        }
        if (level < 1 || level > 6) return false;
        if (beforeIndex > from && beforeIndex < end) return false;
        if ((beforeIndex == from || beforeIndex == end) && level == entries[from].Level) return false;
        return true;
    }

    public static int SectionEndIndex(List<OutlineHeading> entries, int index)
    {
        for (var k = index + 1; k < entries.Count; k++) if (entries[k].Level <= entries[index].Level) return k;
        return entries.Count;
    }

    public static int ShallowestOutside(List<OutlineHeading> entries, int index)
    {
        var end = entries.Count;
        for (var k = index + 1; k < entries.Count; k++) if (entries[k].Level <= entries[index].Level) { end = k; break; }
        var shallowest = 0;
        for (var k = 0; k < entries.Count; k++)
        {
            if (k >= index && k < end) continue;
            if (shallowest == 0 || entries[k].Level < shallowest) shallowest = entries[k].Level;
        }
        return shallowest == 0 ? entries[index].Level : shallowest;
    }
}
