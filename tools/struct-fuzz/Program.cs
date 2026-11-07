// 見出しの節を動かす・回す・消す（MarkdownView.Sections.cs）で、文書の形が崩れないかの確かめ（tools/struct-fuzz.sh から動かす。2026-11-07）。
// でたらめな文書に操作をかけ、見出しの並び・親・置き場所（リスト・引用の中か）・ほかのブロックが変わらないかを見る。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Extensions.Tables;
using Tsuzuri.Markdown;

sealed class Analysis
{
    public string Text;
    public Doc Doc;
    public List<OutlineHeading> Heads;
    public Dictionary<string, string> Parent = new Dictionary<string, string>();   // heading text -> parent text ("" = none)
    public Dictionary<string, List<string>> Owned = new Dictionary<string, List<string>>(); // owner -> leaf sigs in order
    public bool Unique = true;
    public List<string> NonBlank = new List<string>();

    public static Analysis Of(string text)
    {
        var a = new Analysis { Text = text, Doc = new Doc(text), Heads = MarkdownParser.Headings(text) };
        var seen = new HashSet<string>();
        foreach (var h in a.Heads) if (!seen.Add(h.Text)) a.Unique = false;
        for (int i = 0; i < a.Heads.Count; i++)
        {
            string p = "";
            for (int k = i - 1; k >= 0; k--) if (a.Heads[k].Level < a.Heads[i].Level) { p = a.Heads[k].Text; break; }
            a.Parent[a.Heads[i].Text] = p;
        }
        var md = Markdig.Markdown.Parse(text, MarkdownParser.Pipeline);
        a.Walk(md, "");
        a.Finish();
        for (int ln = 1; ln <= a.Doc.LineCount; ln++) { var t = a.Doc.LineText(ln); if (t.Trim().Length > 0) a.NonBlank.Add(t); }
        return a;
    }

    string LinesOf(Block b)
    {
        int from = StartLine(b);
        int to = b.Span.End >= 0 ? Doc.LineNumberOfOffset(Math.Min(b.Span.End, Math.Max(0, Text.Length - 1))) : from;
        if (to < from) to = from;
        var sb = new StringBuilder();
        for (int l = from; l <= to && l <= Doc.LineCount; l++) { if (sb.Length > 0) sb.Append('/'); sb.Append(Doc.LineText(l).Trim()); }
        return sb.ToString().TrimEnd('/');
    }

    int StartLine(Block b) => b.Span.Start >= 0 ? Doc.LineNumberOfOffset(Math.Min(b.Span.Start, Text.Length)) : b.Line + 1;

    string Owner(int line1)
    {
        string o = "<pre>";
        foreach (var h in Heads) { if (h.Line <= line1) o = h.Text; else break; }
        return o;
    }

    readonly List<(string owner, int line, int seq, string sig)> raw = new List<(string, int, int, string)>();
    void Add(string owner, string sig) { Add(owner, sig, 0); }
    void Add(string owner, string sig, int line) { raw.Add((owner, line, raw.Count, sig)); }
    void Finish()
    {
        foreach (var r in raw.OrderBy(r => r.line).ThenBy(r => r.sig, StringComparer.Ordinal))
        {
            List<string> l; if (!Owned.TryGetValue(r.owner, out l)) Owned[r.owner] = l = new List<string>();
            l.Add(r.sig);
        }
    }

    void Walk(ContainerBlock c, string path)
    {
        foreach (var b in c)
        {
            if (b is LinkReferenceDefinitionGroup || b is Markdig.Extensions.Footnotes.FootnoteGroup) { Walk((ContainerBlock)b, path); continue; }
            if (b is ContainerBlock cb)
            {
                string first = Doc.LineText(StartLine(b)).Trim();
                string tag;
                if (b is ListBlock lb) tag = "L" + (lb.IsOrdered ? "o" : "u") + (lb.IsLoose ? "!" : "") + "(" + first + ")";
                else if (b is ListItemBlock) tag = "I(" + first + ")";
                else if (b is QuoteBlock) tag = "Q(" + first + ")";
                else if (b is Table) tag = "T(" + first + ")";
                else tag = b.GetType().Name;
                // container itself is attributed to its owner too (catches container merges/splits)
                Add(Owner(StartLine(b)), "C:" + path + "/" + tag, StartLine(b));
                Walk(cb, path + "/" + tag);
            }
            else
            {
                string sig;
                if (b is HeadingBlock)
                {
                    var h = Heads.FirstOrDefault(x => x.Line == StartLine(b));
                    sig = "HEAD:" + (h != null ? h.Text : "?" + LinesOf(b));
                }
                else sig = b.GetType().Name + ":" + LinesOf(b);
                Add(Owner(StartLine(b)), path + "|" + sig, StartLine(b));
            }
        }
    }
}

static class Gen
{
    public static bool NoIndent = Environment.GetEnvironmentVariable("NOINDENT") == "1";
    public static bool NoListCont = Environment.GetEnvironmentVariable("NOLISTCONT") == "1";
    static Random R;
    static int id;
    static string Id(string p) => p + (++id);
    static bool P(double p) => R.NextDouble() < p;

    static int Level()
    {
        double x = R.NextDouble();
        return x < 0.3 ? 1 : x < 0.6 ? 2 : x < 0.8 ? 3 : x < 0.9 ? 4 : x < 0.96 ? 5 : 6;
    }

    static List<string> Atx(int level)
    {
        string pre = !NoIndent && P(0.08) ? new string(' ', R.Next(1, 4)) : "";
        string sp = P(0.05) ? "\t" : P(0.05) ? "   " : " ";
        string t = pre + new string('#', level) + sp + Id("H");
        if (P(0.08)) t += " " + new string('#', R.Next(1, 4));
        if (P(0.05)) t += "  ";
        return new List<string> { t };
    }

    static List<string> Setext()
    {
        var l = new List<string> { Id("S") };
        if (P(0.1)) l.Add(Id("S"));
        l.Add(P(0.5) ? new string('=', R.Next(1, 5)) : new string('-', R.Next(1, 5)));
        return l;
    }

    static List<string> Para()
    {
        var l = new List<string>(); int n = R.Next(1, 4);
        for (int i = 0; i < n; i++) l.Add(Id("p") + (P(0.05) ? "  " : P(0.03) ? "\\" : ""));
        return l;
    }

    static List<string> ListBlock()
    {
        var l = new List<string>();
        bool ordered = P(0.35);
        string m = ordered ? (P(0.5) ? "1." : "1)") : new[] { "-", "*", "+" }[R.Next(3)];
        string ind = new string(' ', m.Length + 1);
        int n = R.Next(1, 4);
        for (int i = 0; i < n; i++)
        {
            double x = R.NextDouble();
            if (x < 0.15) l.Add(m + " " + new string('#', Level()) + " " + Id("H"));
            else l.Add(m + " " + Id("li"));
            if (P(0.3))
            {
                if (P(0.6)) l.Add("");
                if (NoListCont) { l.Add(ind + Id("lc")); continue; }
                double y = R.NextDouble();
                if (y < 0.35) l.Add(ind + new string('#', Level()) + " " + Id("H"));        // heading in item continuation (indented 2-3)
                else if (y < 0.5) { l.Add(ind + Id("S")); l.Add(ind + "---"); }
                else if (y < 0.6) l.Add("    " + new string('#', Level()) + " " + Id("H"));
                else if (y < 0.7) l.Add(Id("lazy"));                                           // lazy continuation
                else l.Add(ind + Id("lc"));
            }
            if (P(0.15)) l.Add("");
        }
        return l;
    }

    static List<string> Quote()
    {
        var l = new List<string>(); int n = R.Next(1, 4);
        for (int i = 0; i < n; i++)
        {
            double x = R.NextDouble();
            if (x < 0.25) l.Add("> " + new string('#', Level()) + " " + Id("H"));
            else if (x < 0.35) { l.Add("> " + Id("S")); l.Add("> ==="); }
            else if (x < 0.45) l.Add(">");
            else l.Add("> " + Id("q"));
        }
        if (P(0.1)) l.Add(Id("lazyq"));
        return l;
    }

    static List<string> Fence()
    {
        string f = P(0.7) ? "```" : "~~~";
        var l = new List<string> { f + (P(0.3) ? "md" : "") };
        int n = R.Next(0, 4);
        for (int i = 0; i < n; i++) l.Add(P(0.5) ? new string('#', Level()) + " " + Id("code") : P(0.3) ? "" : Id("c"));
        if (!P(0.05)) l.Add(f);
        return l;
    }

    static List<string> Indented() => new List<string> { "    # " + Id("ic"), "    " + Id("ic") };
    static List<string> Rule() => new List<string> { new[] { "---", "***", "___", "* * *", "- - -" }[R.Next(5)] };
    static List<string> Table()
    {
        var a = Id("t"); var b = Id("t");
        var l = new List<string> { "| " + a + " | " + b + " |", "|---|---|", "| " + Id("t") + " | " + Id("t") + " |" };
        if (P(0.3)) l.Add("| " + Id("t") + " | " + Id("t") + " |");
        return l;
    }
    static List<string> Html()
    {
        double x = R.NextDouble();
        if (x < 0.3) return new List<string> { "<div>", "# " + Id("inhtml"), "</div>" };
        if (x < 0.5) return new List<string> { "<!-- " + Id("cm") + " -->" };
        if (x < 0.7) return new List<string> { "<details>", "<summary>" + Id("sum") + "</summary>", "", "## " + Id("H"), "", "</details>" };
        if (x < 0.85) return new List<string> { "<span>" + Id("sp") + "</span>" };
        return new List<string> { "<!--", "## " + Id("cmH"), "-->" };
    }
    static List<string> Front() => new List<string> { "---", "title: " + Id("fm"), "---" };
    static List<string> LinkDef() => P(0.5) ? new List<string> { "[" + Id("r") + "]: http://example.com" } : new List<string> { "[^" + Id("f") + "]: " + Id("fn") };

    public static string Make(Random r)
    {
        R = r; id = 0;
        var blocks = new List<List<string>>();
        if (P(0.1)) blocks.Add(Front());
        int n = R.Next(2, 16);
        int baseShift = P(0.25) ? R.Next(1, 3) : 0;   // documents starting with deeper headings
        for (int i = 0; i < n; i++)
        {
            double x = R.NextDouble();
            List<string> b;
            if (x < 0.42) b = Atx(Math.Min(6, (i == 0 ? baseShift : 0) + Level()));
            else if (x < 0.48) b = Setext();
            else if (x < 0.66) b = Para();
            else if (x < 0.73) b = ListBlock();
            else if (x < 0.78) b = Quote();
            else if (x < 0.83) b = Fence();
            else if (x < 0.85) b = Indented();
            else if (x < 0.89) b = Rule();
            else if (x < 0.92) b = Table();
            else if (x < 0.96) b = Html();
            else b = LinkDef();
            blocks.Add(b);
        }
        var lines = new List<string>();
        if (P(0.05)) lines.Add("");
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                double x = R.NextDouble();
                if (x < 0.35) { }
                else if (x < 0.8) lines.Add("");
                else if (x < 0.93) { lines.Add(""); lines.Add(""); }
                else lines.Add("  ");
            }
            lines.AddRange(blocks[i]);
        }
        string nl = P(0.15) ? "\r\n" : "\n";
        var text = string.Join(nl, lines);
        double e = R.NextDouble();
        if (e < 0.7) text += nl; else if (e < 0.8) text += nl + nl;
        return text;
    }
}

sealed class Op
{
    public string Kind;      // rotate, delete, move, level, raw
    public string Head;      // heading text
    public string Anchor;    // anchor heading text (move)
    public Port.Drop Drop;
    public bool Up;
    public int Delta;        // level op
    public int RawLevel;     // raw
    public override string ToString()
    {
        switch (Kind)
        {
            case "rotate": return $"RotateSiblings(line of \"{Head}\", up={Up})";
            case "delete": return $"DeleteSection(line of \"{Head}\")";
            case "move": return $"UI drop {Drop} anchor=\"{Anchor}\" moving \"{Head}\" -> MoveSectionTo(line, beforeLine, level) via DropTarget";
            case "level": return $"ChangeHeadingLevel(\"{Head}\", delta={Delta}) -> MoveSectionTo(line, line, level+delta)";
            default: return $"MoveSectionTo(line of \"{Head}\", before={(Anchor ?? "-1")}, newLevel={RawLevel}) [raw]";
        }
    }
}

static class Program
{
    static readonly Regex AtxHashes = new Regex(@"^ {0,3}(#{1,6})(?=[ \t]|$)");

    sealed class Fail { public string Cls; public string Detail; }

    static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n\n");

    static int LineOfHead(List<OutlineHeading> hs, string t) { var h = hs.FirstOrDefault(x => x.Text == t); return h == null ? -1 : h.Line; }

    // Returns failures (class names). info: output text, return code
    static List<Fail> Run(string text, Op op, out int code, out string output, Dictionary<string, int> stats)
    {
        var fails = new List<Fail>();
        code = 0; output = text;
        var A = Analysis.Of(text);
        if (!A.Unique) { code = -100; return fails; }
        var hs = A.Heads;
        int hi = hs.FindIndex(h => h.Text == op.Head);
        if (hi < 0) { code = -100; return fails; }
        bool crlfOnly = text.Contains("\r\n") && !Regex.IsMatch(text, "(?<!\r)\n");

        void F(string cls, string d) => fails.Add(new Fail { Cls = cls, Detail = d });

        void CheckOwned(Analysis B, Func<string, bool> ownerKept, string pfx)
        {
            foreach (var kv in A.Owned)
            {
                if (!ownerKept(kv.Key)) continue;
                List<string> after;
                if (!B.Owned.TryGetValue(kv.Key, out after)) after = new List<string>();
                if (!after.SequenceEqual(kv.Value))
                    F(pfx + ":content-structure-changed/" + Sub(kv.Value, after), "owner " + kv.Key + "\n  before: " + string.Join(" ;; ", kv.Value) + "\n  after:  " + string.Join(" ;; ", after));
            }
            foreach (var kv in B.Owned)
                if (!A.Owned.ContainsKey(kv.Key) || !ownerKept(kv.Key))
                    if (A.Owned.ContainsKey(kv.Key) == false)
                        F(pfx + ":content-new-owner", "owner " + kv.Key + ": " + string.Join(" ;; ", kv.Value));
        }

        void CheckEol(string outp, string pfx)
        {
            if (crlfOnly && Regex.IsMatch(outp, "(?<!\r)\n")) F(pfx + ":mixed-line-endings(cosmetic)", "CRLF document got bare LF");
        }

        if (op.Kind == "rotate")
        {
            Port.RotateInfo info;
            code = Port.RotateSiblings(text, hs[hi].Line, op.Up, out output, out info);
            Bump(stats, "rotate rc=" + code);
            if (code == -2) F("rotate:INFO-refused-MoveChangesMeaning", "");
            if (code < 0) { if (output != text) F("rotate:error-but-changed", ""); return fails; }
            var B = Analysis.Of(output);
            // headings sequence
            var exp = new List<OutlineHeading>(hs.Take(info.Children[0]));
            foreach (var i in info.Order) { var to = i + 1 < info.Children.Count ? info.Children[i + 1] : info.Last; exp.AddRange(hs.Skip(info.Children[i]).Take(to - info.Children[i])); }
            exp.AddRange(hs.Skip(info.Last));
            if (!exp.Select(h => h.Level + ":" + h.Text).SequenceEqual(B.Heads.Select(h => h.Level + ":" + h.Text))) F("rotate:heading-sequence", "");
            foreach (var h in hs) { string p; if (!B.Parent.TryGetValue(h.Text, out p) || p != A.Parent[h.Text]) F("rotate:parent-changed", h.Text + ": " + A.Parent[h.Text] + " -> " + p); }
            CheckOwned(B, o => true, "rotate");
            if (!output.StartsWith(text.Substring(0, info.FirstStart))) F("rotate:prefix-bytes", "");
            if (!output.EndsWith(text.Substring(info.Stop))) F("rotate:suffix-bytes", "");
            // multiset of non-heading nonblank lines
            var hl = new HashSet<int>(hs.Select(h => h.Line));
            var ms1 = A.NonBlank.OrderBy(s => s, StringComparer.Ordinal).ToList();
            var ms2 = B.NonBlank.OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (!ms1.SequenceEqual(ms2)) F("rotate:line-multiset", "");
            CheckEol(output, "rotate");
            // inverse
            var hl2 = LineOfHead(B.Heads, op.Head);
            string back; Port.RotateInfo i2;
            var c2 = Port.RotateSiblings(output, hl2, !op.Up, out back, out i2);
            if (c2 < 0) F("rotate:inverse-refused", "rc=" + c2);
            else if (back != text)
            {
                var C = Analysis.Of(back);
                if (C.NonBlank.SequenceEqual(A.NonBlank)) F("rotate:inverse-not-byte-exact(normalized-equal)", "");
                else F("rotate:inverse-differs", "");
            }
            return fails;
        }

        if (op.Kind == "delete")
        {
            int s, e; string gap;
            Port.KeptDefs = 0;
            code = Port.DeleteSection(text, hs[hi].Line, out output, out s, out e, out gap);
            Bump(stats, "delete rc=" + code);
            if (code >= 0 && Port.KeptDefs > 0)
            {
                // definitions kept: only check the heading sequence and that every kept line came from the input
                var Bk = Analysis.Of(output);
                int endk = Port.SectionEnd(hs, hi); int lastk = endk >= 0 ? endk : hs.Count;
                var expk = hs.Where((h, k) => k < hi || k >= lastk).Select(h => h.Level + ":" + h.Text).ToList();
                if (!expk.SequenceEqual(Bk.Heads.Select(h => h.Level + ":" + h.Text))) F("delete-kept:heading-sequence", "");
                var inLines = new HashSet<string>(text.Replace("\r", "").Split('\n'));
                foreach (var l in output.Replace("\r", "").Split('\n')) if (l.Length > 0 && !inLines.Contains(l)) F("delete-kept:new-line", l);
                Bump(stats, "delete kept-defs");
                return fails;
            }
            if (code == -2) F("delete:INFO-refused-MoveChangesMeaning", "");
            if (code < 0) { if (output != text) F("delete:error-but-changed", ""); return fails; }
            var B = Analysis.Of(output);
            int end = Port.SectionEnd(hs, hi); int last = end >= 0 ? end : hs.Count;
            var removed = new HashSet<string>(hs.Skip(hi).Take(last - hi).Select(h => h.Text));
            var exp = hs.Where((h, k) => k < hi || k >= last).Select(h => h.Level + ":" + h.Text).ToList();
            if (!exp.SequenceEqual(B.Heads.Select(h => h.Level + ":" + h.Text))) F("delete:heading-sequence", "");
            foreach (var h in hs) if (!removed.Contains(h.Text)) { string p; if (!B.Parent.TryGetValue(h.Text, out p) || p != A.Parent[h.Text]) F("delete:parent-changed", h.Text + ": " + A.Parent[h.Text] + " -> " + p); }
            CheckOwned(B, o => !removed.Contains(o), "delete");
            // only that section's lines removed
            var la = A.Doc; int l0 = la.LineNumberOfOffset(s); int l1 = e >= text.Length ? la.LineCount + 1 : la.LineNumberOfOffset(e);
            var expLines = new List<string>();
            for (int l = 1; l <= la.LineCount; l++) if ((l < l0 || l >= l1) && la.LineText(l).Trim().Length > 0) expLines.Add(la.LineText(l));
            if (!expLines.SequenceEqual(B.NonBlank)) F("delete:lines", "");
            CheckEol(output, "delete");
            return fails;
        }

        // move / level / raw
        int beforeLine; int newLevel; string intendedParent = null; bool inPlaceLevel = false;
        if (op.Kind == "move")
        {
            int ai = op.Drop == Port.Drop.End ? -1 : hs.FindIndex(h => h.Text == op.Anchor);
            if (op.Drop != Port.Drop.End && ai < 0) { code = -100; return fails; }
            int bi, lv;
            if (!Port.DropTarget(hs, hi, op.Drop, ai, out bi, out lv)) { code = -101; return fails; }
            beforeLine = bi < hs.Count ? hs[bi].Line : -1; newLevel = lv;
            if (op.Drop == Port.Drop.Before || op.Drop == Port.Drop.After) intendedParent = A.Parent[hs[ai].Text];
            else if (op.Drop == Port.Drop.Into) intendedParent = hs[ai].Text;
            else intendedParent = "";
        }
        else if (op.Kind == "level")
        {
            beforeLine = hs[hi].Line; newLevel = hs[hi].Level + op.Delta; inPlaceLevel = true;
            if (newLevel < 1 || newLevel > 6) { code = -101; return fails; }
        }
        else
        {
            beforeLine = op.Anchor == null ? -1 : LineOfHead(hs, op.Anchor); newLevel = op.RawLevel;
        }

        code = Port.MoveSectionTo(text, hs[hi].Line, beforeLine, newLevel, out output);
        Bump(stats, op.Kind + " rc=" + (code >= 0 ? "ok" : code.ToString()));
        string pf = op.Kind;
        if (code == -2) F(pf + ":INFO-refused-MoveChangesMeaning", "");
        if (code < 0) { if (output != text) F(pf + ":error-but-changed", ""); return fails; }
        {
            var B = Analysis.Of(output);
            int end = Port.SectionEnd(hs, hi); int last = end >= 0 ? end : hs.Count;
            int delta = newLevel >= 1 && newLevel <= 6 ? newLevel - hs[hi].Level : 0;
            var sub = hs.Skip(hi).Take(last - hi).ToList();
            var subSet = new HashSet<string>(sub.Select(h => h.Text));
            var rest = hs.Where((h, k) => k < hi || k >= last).ToList();
            int bIdx = beforeLine < 0 ? -1 : hs.FindIndex(h => h.Line == beforeLine);
            int insertAt; // index in rest
            if (bIdx < 0) insertAt = rest.Count;
            else if (bIdx == hi || bIdx == last) insertAt = hi; // in place
            else insertAt = rest.FindIndex(h => h.Text == hs[bIdx].Text);
            var exp = new List<string>();
            exp.AddRange(rest.Take(insertAt).Select(h => h.Level + ":" + h.Text));
            exp.AddRange(sub.Select(h => (h.Level + delta) + ":" + h.Text));
            exp.AddRange(rest.Skip(insertAt).Select(h => h.Level + ":" + h.Text));
            var act = B.Heads.Select(h => h.Level + ":" + h.Text).ToList();
            if (!exp.SequenceEqual(act)) F(pf + ":heading-sequence", "expected " + string.Join(",", exp) + "\n  actual   " + string.Join(",", act));
            // parents
            string mp; B.Parent.TryGetValue(op.Head, out mp);
            if (intendedParent != null && mp != intendedParent) F(pf + ":moved-parent-not-target", "intended " + intendedParent + " actual " + mp);
            foreach (var h in hs)
            {
                if (h.Text == op.Head) continue;
                string p; B.Parent.TryGetValue(h.Text, out p);
                if (p != A.Parent[h.Text])
                {
                    if (subSet.Contains(h.Text)) F(pf + ":subtree-parent-changed", h.Text + ": " + A.Parent[h.Text] + " -> " + p);
                    else F(pf + (inPlaceLevel ? ":other-parent-changed(in-place level change, by design?)" : ":other-parent-changed"), h.Text + ": " + A.Parent[h.Text] + " -> " + p);
                }
            }
            CheckOwned(B, o => true, pf);
            // nonblank line sequence
            var la = A.Doc;
            int l0 = hs[hi].Line; int l1 = end >= 0 ? hs[end].Line : la.LineCount + 1;
            var secLines = new List<string>();
            var headLines = new HashSet<int>(sub.Select(h => h.Line));
            for (int l = l0; l < l1; l++)
            {
                var t = la.LineText(l);
                if (delta != 0 && headLines.Contains(l)) { var m = AtxHashes.Match(t); var lv = m.Groups[1].Length + delta; t = t.Substring(0, m.Groups[1].Index) + new string('#', lv) + t.Substring(m.Groups[1].Index + m.Groups[1].Length); }
                secLines.Add(t);
            }
            var others = new List<string>(); int insLine = -1;
            int bLine = (bIdx < 0) ? la.LineCount + 1 : hs[bIdx].Line;
            if (bIdx == hi || bIdx == last) bLine = l0;
            var expL = new List<string>();
            for (int l = 1; l <= la.LineCount + 1; l++)
            {
                if (l == bLine) expL.AddRange(secLines);
                if (l > la.LineCount) break;
                if (l >= l0 && l < l1) continue;
                expL.Add(la.LineText(l));
            }
            expL = expL.Where(t => t.Trim().Length > 0).ToList();
            if (!expL.SequenceEqual(B.NonBlank)) F(pf + ":lines", "");
            CheckEol(output, pf);
        }
        return fails;
    }

    static string Sub(List<string> a, List<string> b)
    {
        Func<string, string> unloose = x => x.Replace("!(", "(");
        if (a.Select(unloose).SequenceEqual(b.Select(unloose))) return "list-looseness-only";
        var ha = a.Where(x => x.Contains("|HEAD:")).ToList(); var hb = b.Where(x => x.Contains("|HEAD:")).ToList();
        if (!ha.Select(unloose).SequenceEqual(hb.Select(unloose)))
        {
            int da = ha.Sum(x => x.Split('|')[0].Length), db = hb.Sum(x => x.Split('|')[0].Length);
            return da > db ? "heading-left-container" : da < db ? "heading-entered-container" : "heading-container-changed";
        }
        if (a.Count != b.Count) return "block-count-changed";
        return "other";
    }

    static void Bump(Dictionary<string, int> d, string k) { if (d == null) return; int v; d.TryGetValue(k, out v); d[k] = v + 1; }

    static string Minimize(string text, Op op, string cls)
    {
        Func<string, bool> repro = t =>
        {
            try { int c; string o; return Run(t, op, out c, out o, null).Any(f => f.Cls == cls); } catch { return false; }
        };
        if (!repro(text)) return text;
        // try LF
        var lf = text.Replace("\r\n", "\n");
        if (lf != text && repro(lf)) text = lf;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        bool changed = true;
        while (changed)
        {
            changed = false;
            var lines = text.Split(new[] { nl }, StringSplitOptions.None).ToList();
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var l2 = new List<string>(lines); l2.RemoveAt(i);
                var t2 = string.Join(nl, l2);
                if (t2.Length < text.Length && repro(t2)) { text = t2; lines = l2; changed = true; }
            }
            // strip trailing newline / trailing blank
            foreach (var cand in new[] { text.TrimEnd('\n', '\r'), text.TrimEnd('\n', '\r') + nl })
                if (cand.Length < text.Length && repro(cand)) { text = cand; changed = true; }
        }
        return text;
    }

    static void Demo(string text, Op op)
    {
        int code; string outp; var fs = Run(text, op, out code, out outp, null);
        Console.WriteLine("op: " + op + "\n--- input ---\n" + Show(text) + "\n--- output rc=" + code + " ---\n" + Show(outp));
        foreach (var f in fs) Console.WriteLine("FAIL " + f.Cls + " " + f.Detail);
        Console.WriteLine();
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "demo")
        {
            Demo("# A\n\n- item\n\n  ## Note\n\n  more text\n\n## Other\n", new Op { Kind = "rotate", Head = "Other", Up = true });
            Demo("# A\n\n- item\n\n  ## Note\n\n  more text\n\n## Other\n", new Op { Kind = "delete", Head = "Note" });
            Demo("# A\n\n- item\n\n  ## Note\n\n  more text\n\n# B\n", new Op { Kind = "move", Head = "Note", Anchor = "B", Drop = Port.Drop.Into });
            Demo("# A\n\n- item\n\n  ## Note\n\n  more text\n\n# B\n", new Op { Kind = "level", Head = "Note", Delta = 1 });
            Demo("# A\n\n- item\n\n# B\n\n  # C\n\ntext\n", new Op { Kind = "delete", Head = "B" });
            Demo("# A\n\n- item\n\n# B\n\n  # C\n\ntext\n", new Op { Kind = "rotate", Head = "C", Up = true });
            Demo("p1\n## H2\n\nS3\n--\n", new Op { Kind = "rotate", Head = "H2", Up = true });
            Demo("## H2\nbody\nS3\n--\n", new Op { Kind = "rotate", Head = "H2", Up = true });
            // 前付け（YAML）は、Markdig では下線式の見出しに読まれる。回す・消すは取りやめになること（2026-11-07）。
            Demo("---\ntitle: x\ntags: y\n---\n\n## A\naa\n\n## B\nbb\n", new Op { Kind = "rotate", Head = "A", Up = true });
            Demo("---\ntitle: x\n---\n\n## A\naa\n", new Op { Kind = "delete", Head = "title: x" });
            // 末尾のリンクの定義・脚注は、最後の節を消しても残ること（2026-11-07）。
            Demo("# A\nsee [x][1] and [^n]\n\n# B\nbb\n\n[1]: https://example.com\n[^n]: note\n    more\n", new Op { Kind = "delete", Head = "B" });
            return 0;
        }
        int n = args.Length > 0 ? int.Parse(args[0]) : 5000;
        int seed = args.Length > 1 ? int.Parse(args[1]) : 1;
        var rnd = new Random(seed);
        var stats = new Dictionary<string, int>();
        var classes = new Dictionary<string, List<(string text, Op op, string detail)>>();
        int ops = 0, skippedDup = 0;
        for (int it = 0; it < n; it++)
        {
            var text = Gen.Make(rnd);
            var A = Analysis.Of(text);
            if (!A.Unique) { skippedDup++; continue; }
            if (A.Heads.Count == 0) continue;
            for (int j = 0; j < 8; j++)
            {
                var h = A.Heads[rnd.Next(A.Heads.Count)].Text;
                var a = A.Heads[rnd.Next(A.Heads.Count)].Text;
                Op op;
                double x = rnd.NextDouble();
                if (x < 0.3) op = new Op { Kind = "rotate", Head = h, Up = rnd.Next(2) == 0 };
                else if (x < 0.45) op = new Op { Kind = "delete", Head = h };
                else if (x < 0.8) op = new Op { Kind = "move", Head = h, Anchor = a, Drop = (Port.Drop)rnd.Next(4) };
                else if (x < 0.92) op = new Op { Kind = "level", Head = h, Delta = rnd.Next(2) == 0 ? -1 : 1 };
                else op = new Op { Kind = "raw", Head = h, Anchor = rnd.Next(4) == 0 ? null : a, RawLevel = rnd.Next(0, 7) };
                int code; string outp;
                List<Fail> fails;
                try { fails = Run(text, op, out code, out outp, stats); }
                catch (Exception ex) { fails = new List<Fail> { new Fail { Cls = op.Kind + ":exception " + ex.GetType().Name, Detail = ex.Message } }; }
                ops++;
                foreach (var f in fails.GroupBy(f => f.Cls).Select(g => g.First()))
                {
                    List<(string, Op, string)> l;
                    if (!classes.TryGetValue(f.Cls, out l)) classes[f.Cls] = l = new List<(string, Op, string)>();
                    l.Add((text, op, f.Detail));
                }
            }
        }
        Console.WriteLine($"docs={n} ops={ops} skippedDup={skippedDup}");
        foreach (var kv in stats.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        Console.WriteLine();
        foreach (var kv in classes.OrderBy(k => k.Key))
        {
            Console.WriteLine($"=== CLASS {kv.Key}  (hits {kv.Value.Count})");
            // minimize the smallest few
            var best = kv.Value.OrderBy(v => v.text.Length).Take(4).Select(v => (min: Minimize(v.text, v.op, kv.Key), v.op)).OrderBy(v => v.min.Length).First();
            int code; string outp;
            var fs = Run(best.min, best.op, out code, out outp, null);
            Console.WriteLine("op: " + best.op);
            Console.WriteLine("--- input ---\n" + Show(best.min));
            Console.WriteLine("--- output (rc=" + code + ") ---\n" + Show(outp));
            foreach (var f in fs.Where(f => f.Cls == kv.Key)) Console.WriteLine("detail: " + f.Detail);
            Console.WriteLine();
        }

        // 取りやめた（INFO）・見た目だけ（cosmetic）・決めてある動き（by design）・画面からは呼べない引数（raw）は、壊れではない。
        var broken = classes.Keys.Where(k => !k.Contains("INFO") && !k.Contains("cosmetic") && !k.Contains("by design") && !k.StartsWith("raw:")).ToList();
        if (broken.Count > 0)
        {
            Console.WriteLine("NG: " + string.Join(", ", broken));
            return 1;
        }

        Console.WriteLine("OK");
        return 0;
    }
}
