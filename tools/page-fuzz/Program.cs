// ページの読み書き（PageDocument）と、それを使う操作で、崩れた Markdown の文書が壊れないかの確かめ（tools/page-fuzz.sh から動かす。2026-11-09）。
//
// 崩れた文書（閉じていないコードフェンス・長さの合わないフェンス・閉じていない HTML コメント・壊れた／重なったページの印・
// 行き先の無いリンク・自分へのリンク・列のそろわない表・CR/LF/CRLF の混在・BOM・タブ・制御文字・長い行など）をでたらめに作り、次を見る。
//
//   roundtrip  開いて何も変えずに保存し、開き直す。開き直した中身が保存前と同じか（2回目からはバイト単位で同じか）
//   edit       1ページだけ書き換えて保存し、開き直す。ほかのページが1文字も変わらず、書き換えたページがそのとおりか
//   subpage    サブページ作成（範囲をページへ移してリンクを残す）→（保存して開き直す）→ サブページ解除で、1文字も変わらずに戻るか
//   orphan     孤立ページ（FindUnreferenced）に、画面でリンクを押せば開けるページ（Markdig がリンクと読むもの）が混ざらないか
//   lines      AI に知らせる「このページはファイルの何行目から何行目か」（LinesBefore・LineRangeOf）が本当の位置と合うか
//   table      表エディタで何も変えずに保存したとき（TableModel の Parse → ToExchange → ReplaceWith → Serialize）、表示（HTML）が変わらないか
//
// 本体のコードのうち、PageDocument・TableModel・MarkdownParser はそのままコンパイルする。
// サブページ作成と解除は MainWindow（WPF）にあるので、ここに写してある（Extract・Restore）。
// **MainWindow.Pages.cs の Extract_Click（63〜111行）・Inline_Click（171〜240行）を変えたら、ここの写しも合わせる。**
// 表の書き戻しは MarkdownView.EditTable（1650〜1700行）の、表エディタを開いて何も変えずに閉じたときの道筋の写し。
//
//   引数: [文書の数（既定 3000）] [乱数の種（既定 1）]   または  demo（名前を付けた例だけ）
//   崩れが見つかれば種類ごとに一番短い例を出す。壊れ（BUG）が1つでもあれば終了コード 1。
//   決めてある動き（by design）・既知（known。TODO.md 1章）は数えるだけで、終了コードには入れない。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tsuzuri.Markdown;

static class Gen
{
    static Random R;
    static bool P(double p) => R.NextDouble() < p;
    static T Pick<T>(params T[] a) => a[R.Next(a.Length)];
    static int n;
    static string W(string p) => p + (++n);

    public static List<string> Ids = new List<string>();

    static string Ref() => Ids.Count == 0 || P(0.15) ? Pick("p9", "zz", "p7") : Ids[R.Next(Ids.Count)];

    /// <summary>ページの印の形の行（崩れたものも）。</summary>
    public static string MarkerLike(string id)
    {
        switch (R.Next(12))
        {
            case 0: return "<!--page: " + id + "-->";
            case 1: return " <!--page: " + id + "-->";
            case 2: return "   <!--page:" + id + " -->  ";
            case 3: return "    <!--page: " + id + "-->";      // 字下げコード
            case 4: return "\t<!--page: " + id + "-->";
            case 5: return "<!--page: " + id;                  // --> が無い
            case 6: return "<!-- page: " + id + " -->";
            case 7: return "<!--page: " + id + ".x-->";        // 使えない文字
            case 8: return "> <!--page: " + id + "-->";
            case 9: return "- <!--page: " + id + "-->";
            case 10: return "\\<!--page: " + id + "-->";
            default: return "<!--page: " + id + "--> x";
        }
    }

    public static List<string> Block()
    {
        var l = new List<string>();
        switch (R.Next(22))
        {
            case 0: l.Add(W("text")); if (P(0.3)) l.Add(W("text") + "  "); break;
            case 1: l.Add(new string('#', R.Next(1, 4)) + " " + W("H")); break;
            case 2: l.Add(W("S")); l.Add(Pick("---", "===", "--", "- - -")); break;
            case 3: l.Add(Pick("---", "***", "|", "| |", "||")); break;
            case 4:
            {
                // コードフェンス。閉じない・長さの違う閉じ・印の形の行を中に含む
                var open = Pick("```", "````", "~~~", "   ```", "``` a`b", "```md", "~~~~");
                l.Add(open);
                for (int i = R.Next(0, 3); i > 0; i--) l.Add(P(0.4) ? MarkerLike(Ref()) : W("code"));
                var mark = open.Trim()[0];
                double x = R.NextDouble();
                if (x < 0.5) l.Add(new string(mark, open.Trim().TakeWhile(c => c == mark).Count()));
                else if (x < 0.6) l.Add(new string(mark, 3));
                else if (x < 0.7) l.Add(new string(mark == '`' ? '~' : '`', 3));
                else if (x < 0.8) l.Add(new string(mark, 5));
                break;
            }
            case 5: l.Add(Pick("<!--", "<!-- " + W("c"), "<!-- x -->", "-->")); break;
            case 6: l.Add("<div>"); l.Add(Pick("```", W("d"), MarkerLike(Ref()))); if (P(0.6)) l.Add("</div>"); break;
            case 7: l.Add("[" + W("L") + "](#" + Ref() + ")"); break;
            case 8:
                l.Add(Pick("[a](<#" + Ref() + ">)", "[a](#" + Ref() + " \"t\")", "[a][" + W("r") + "]", "[a](#" + Ref(), "[a](", "[a]", "![i](#" + Ref() + ")",
                    "`[a](#" + Ref() + ")`", "[a [b] c](#" + Ref() + ")", "[a\\]](#" + Ref() + ")", "<a href=\"#" + Ref() + "\">a</a>"));
                break;
            case 9:
            {
                // リンクの定義（Markdig は読むが、行の形では見落としやすいもの）
                var id = Ref();
                var r = W("r");
                l.Add("see [x][" + r + "]");
                l.Add("");
                l.Add(Pick("[" + r + "]: #" + id, "> [" + r + "]: #" + id, "- [" + r + "]: #" + id, "[" + r + "]:", "[" + r + "]: <#" + id + ">"));
                if (l[2].EndsWith(":")) l.Add("#" + id);
                break;
            }
            case 10: l.Add("[x](" ); l.Add("#" + Ref() + ")"); break;
            case 11:
            {
                // 表（列のそろわない・区切り行が無い・はみ出す・\| ・リストや引用の中）
                var pre = Pick("", "", "", "- ", "> ", "  ", "1. ");
                var cont = pre == "- " ? "  " : pre == "1. " ? "   " : pre == "> " ? "> " : pre;
                l.Add(pre + Pick("| a | b |", "a | b", "| a | b | c |", "| a |", "| a \\| x | b |", "| a\\\\| b |", "| `a|b` | c |"));
                if (!P(0.1)) l.Add(cont + Pick("|---|---|", "---|---", "|:-|-:|", "|---|", "|---|---|---|", "| - | - |"));
                for (int i = R.Next(0, 3); i > 0; i--)
                    l.Add(cont + Pick("| 1 | 2 |", "| 1 |", "| 1 | 2 | 3 |", "1 | 2", "|", "| 1\t| 2 |", "| " + W("v") + " | x |", "lazy"));
                break;
            }
            case 12: l.Add(Pick("\t" + W("tab"), W("ws") + " \t ", W("nul") + "\0x", W("ctl") + "\u0001\u001f", W("ls") + "\u2028y", "\uFEFF" + W("bom"), W("cr") + "\r" + W("cr"))); break;
            case 13: l.Add(new string('x', R.Next(1000, 6000))); break;
            case 14: l.Add("> " + W("q")); if (P(0.5)) l.Add(Pick("> ```", "```", W("lazy"))); break;
            case 15: l.Add("- " + W("li")); if (P(0.5)) l.Add(Pick("  ```", "    ```", "  " + MarkerLike(Ref()), "  ## " + W("H"))); break;
            case 16: l.Add(MarkerLike(Ref())); break;
            case 17: l.Add(""); break;
            case 18: l.Add("    " + W("ic")); break;
            case 19: l.Add("[^" + W("f") + "]: note"); break;
            case 20: l.Add("$$"); l.Add(W("m")); break;
            default: l.Add(W("p") + " [x](#" + Ref() + ") " + W("p")); break;
        }
        return l;
    }

    public static string Page()
    {
        var lines = new List<string>();
        for (int i = R.Next(0, 7); i > 0; i--)
        {
            if (lines.Count > 0 && P(0.6)) lines.Add("");
            lines.AddRange(Block());
        }
        return string.Join("\n", lines);
    }

    /// <summary>ファイル全体。返すのは書き込むバイト列。</summary>
    public static byte[] File(Random r)
    {
        R = r; n = 0;
        Ids.Clear();
        int pages = R.Next(0, 5);
        for (int i = 0; i < pages; i++) Ids.Add(P(0.85) ? "p" + (i + 1) : Pick("a-b", "A_1", "p1", "x9"));

        var sb = new StringBuilder();
        string nl = P(0.2) ? "\r\n" : P(0.05) ? "\r" : "\n";
        string Nl() => P(0.03) ? Pick("\r\n", "\n", "\r") : nl;

        if (!P(0.05))
        {
            foreach (var line in Page().Split('\n')) sb.Append(line).Append(Nl());
        }

        foreach (var id in Ids)
        {
            sb.Append(P(0.8) ? "<!--page: " + id + "-->" : MarkerLike(id)).Append(Nl());
            foreach (var line in Page().Split('\n')) sb.Append(line).Append(Nl());
        }

        var text = sb.ToString();
        if (P(0.3) && text.Length > 0) text = text.Substring(0, text.Length - (text.EndsWith("\r\n") ? 2 : 1));
        if (P(0.02)) text = "";

        var body = new UTF8Encoding(false).GetBytes(text);
        if (!P(0.1)) return body;
        return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
    }

    public static Random Rnd { get { return R; } }
}

static class Program
{
    static string Dir;
    static int fileNo;

    sealed class Hit { public string Cls; public string Input; public string Detail; }
    static readonly Dictionary<string, List<Hit>> Hits = new Dictionary<string, List<Hit>>();

    static void Report(string cls, string input, string detail)
    {
        List<Hit> l;
        if (!Hits.TryGetValue(cls, out l)) Hits[cls] = l = new List<Hit>();
        l.Add(new Hit { Cls = cls, Input = input, Detail = detail });
    }

    static string Show(string s)
    {
        if (s == null) return "(null)";
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '\n') sb.Append("\\n\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c == '\u2028' || c == '\uFEFF') sb.Append("\\u" + ((int)c).ToString("x4"));
            else sb.Append(c);
        }
        var t = sb.ToString();
        return t.Length > 1500 ? t.Substring(0, 1500) + "…(" + t.Length + ")" : t;
    }

    static string Path1() => Path.Combine(Dir, "doc" + (++fileNo) + ".md");

    static PageDocument Open(byte[] bytes)
    {
        var path = Path1();
        File.WriteAllBytes(path, bytes);
        return PageDocument.Load(path);
    }

    static string Text(byte[] bytes)
    {
        int o = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, o, bytes.Length - o);
    }

    static byte[] Save(PageDocument d)
    {
        d.Save(true);
        return File.ReadAllBytes(d.FilePath);
    }

    static List<KeyValuePair<string, string>> Snap(PageDocument d) => d.Pages.Select(p => new KeyValuePair<string, string>(p.Id, p.Text)).ToList();

    static string Diff(List<KeyValuePair<string, string>> a, List<KeyValuePair<string, string>> b)
    {
        if (a.Count != b.Count || !a.Select(x => x.Key).SequenceEqual(b.Select(x => x.Key)))
            return "pages [" + string.Join(",", a.Select(x => x.Key)) + "] -> [" + string.Join(",", b.Select(x => x.Key)) + "]";
        for (int i = 0; i < a.Count; i++)
            if (a[i].Value != b[i].Value) return "page '" + a[i].Key + "':\n  expected: " + Show(a[i].Value) + "\n  actual:   " + Show(b[i].Value);
        return null;
    }

    static readonly Regex MarkerRe = new Regex(@"^ {0,3}<!--page:[ \t]*([A-Za-z0-9_-]+)[ \t]*-->[ \t]*$");
    static readonly Regex FenceRe = new Regex(@"^ {0,3}(`{3,}|~{3,})");

    /// <summary>ページの文字の違いを、既知・決めてある動きに分ける。</summary>
    static string Classify(string expected, string actual)
    {
        if (expected == actual) return "same";
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        // 印の形の行の頭に \ が付いた（字下げも消えた）
        if (e.Length == a.Length)
        {
            bool onlyEscape = true, lostIndent = false;
            for (int i = 0; i < e.Length; i++)
            {
                if (e[i] == a[i]) continue;
                if (MarkerRe.IsMatch(e[i]) && a[i] == "\\" + e[i].TrimStart(' ')) { if (e[i].StartsWith(" ")) lostIndent = true; continue; }
                onlyEscape = false;
            }
            if (onlyEscape) return lostIndent ? "escape+indent" : "escape";
        }
        // 末尾にフェンスの閉じが1行足された
        if (a.Length == e.Length + 1 && FenceRe.IsMatch(a[a.Length - 1]) && Classify(expected, string.Join("\n", a.Take(e.Length))) is string c0 && (c0 == "same" || c0.StartsWith("escape")))
            return "closer" + (c0 == "same" ? "" : "+" + c0);
        return expected == actual ? "same" : "other";
    }

    // ---------------------------------------------------------------
    // サブページ作成・解除の写し（MainWindow.Pages.cs）
    // ---------------------------------------------------------------

    /// <summary>Extract_Click（63〜111行）: 選んだ範囲をページにして、その場に [>>](#ID) を残す。</summary>
    static string Extract(PageDocument d, string pageId, int start, int length)
    {
        var text = d.TextOf(pageId);
        var body = text.Substring(start, length);
        var id = d.AddPage(body);
        d.SetText(pageId, text.Substring(0, start) + "[>>](#" + id + ")" + text.Substring(start + length));
        return id;
    }

    /// <summary>Inline_Click（171〜240行）: 親（preferred を先に探す）のリンクを本文に置き換え、ページを消す。</summary>
    static bool Restore(PageDocument d, string id, string preferred, out string parent)
    {
        int offset, length;
        if (!d.FindLinkTo(id, preferred, out parent, out offset, out length)) return false;
        var link = d.TextOf(parent).Substring(offset, length);
        if (link != "[>>](#" + id + ")") Overreach = link;
        var body = d.TextOf(id) ?? string.Empty;
        if (!d.ReplaceIn(parent, offset, length, body)) return false;
        d.RemovePage(id);
        return true;
    }

    // ---------------------------------------------------------------
    // 1つの文書の確かめ
    // ---------------------------------------------------------------

    static void Check(byte[] original, Random r)
    {
        var input = Text(original);
        PageDocument d0;
        try { d0 = Open(original); }
        catch (IOException ex) { Report("known load refused (unclosed fence before a later marker)", input, ex.Message); return; }

        // ---- roundtrip: 何も変えずに保存して開き直す
        var s0 = Snap(d0);
        byte[] saved;
        try { saved = Save(d0); }
        catch (IOException ex) { Report("BUG roundtrip: save refused without edits", input, ex.Message); return; }

        PageDocument d1;
        try { d1 = Open(saved); }
        catch (IOException ex) { Report("BUG roundtrip: saved file does not reopen", input, ex.Message + "\nsaved:\n" + Show(Text(saved))); return; }

        var s1 = Snap(d1);
        var diff = Diff(s0, s1);
        if (diff != null)
        {
            var cls = s0.Count == s1.Count && s0.Select(x => x.Key).SequenceEqual(s1.Select(x => x.Key))
                ? Enumerable.Range(0, s0.Count).Select(i => Classify(s0[i].Value, s1[i].Value)).Where(c => c != "same").Distinct().OrderBy(c => c).Aggregate((x, y) => x + "," + y)
                : "pages";
            var label = cls == "closer" ? "by design roundtrip: unclosed fence closed at page end" : cls.StartsWith("escape") || cls.StartsWith("closer") ? "known roundtrip: " + cls : "BUG roundtrip: " + cls;
            Report(label, input, diff);
        }
        else if (!original.SequenceEqual(saved))
        {
            var o = input; var s = Text(saved);
            string why = o.Replace("\r\n", "\n").Replace("\r", "\n") == s.Replace("\r\n", "\n") ? "newlines unified" :
                         o.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n') == s.Replace("\r\n", "\n").TrimEnd('\n') ? "trailing newline" : "marker spelling / leading newline";
            Report("INFO bytes differ after no-edit save (" + why + ")", input, "saved:\n" + Show(s));
        }

        // 2回目からは1バイトも変わらないこと
        try
        {
            var again = Save(d1);
            if (!again.SequenceEqual(saved)) Report("BUG roundtrip: second save differs", input, Show(Text(saved)) + "\n---\n" + Show(Text(again)));
        }
        catch (IOException ex) { Report("BUG roundtrip: second save refused", input, ex.Message); }

        // 以下は、保存して開き直したもの（落ち着いた形）から始める
        var baseBytes = saved;
        var baseText = Text(saved);

        CheckOrphans(d1, baseText);
        CheckLines(d1, baseText);
        CheckTables(d1, baseText);
        CheckEdit(baseBytes, baseText, r);
        for (int k = 0; k < 4; k++) CheckSubpage(baseBytes, baseText, r);
    }

    static void CheckEdit(byte[] baseBytes, string baseText, Random r)
    {
        var d = Open(baseBytes);
        var before = Snap(d);
        var k = r.Next(before.Count);
        var lines = before[k].Value.Split('\n').ToList();
        var add = Gen.Page().Replace("\r", "");   // 貼り付けの改行は画面でそろえられる
        lines.Insert(r.Next(lines.Count + 1), add);
        var now = string.Join("\n", lines);
        d.SetText(before[k].Key, now);
        var expect = Snap(d);

        byte[] saved;
        try { saved = Save(d); }
        catch (IOException ex) { Report("BUG edit: save refused", baseText, "page " + before[k].Key + " <- " + Show(now) + "\n" + ex.Message); return; }
        PageDocument back;
        try { back = Open(saved); }
        catch (IOException ex) { Report("BUG edit: saved file does not reopen", baseText, "page " + before[k].Key + " <- " + Show(now) + "\n" + ex.Message); return; }
        var got = Snap(back);
        var diff = Diff(expect, got);
        if (diff == null) return;

        var cls = expect.Count == got.Count && expect.Select(x => x.Key).SequenceEqual(got.Select(x => x.Key))
            ? string.Join(",", Enumerable.Range(0, got.Count).Select(i => (i == k ? "edited:" : "other:") + Classify(expect[i].Value, got[i].Value)).Where(c => !c.EndsWith(":same")).SelectMany(c => c.Split(':')[1].Split('+').Select(x => c.Split(':')[0] + ":" + x)).Distinct().OrderBy(c => c))
            : "pages";
        var label = cls.Contains("other:") ? "BUG edit: another page changed (" + cls + ")"
                  : cls == "edited:closer" ? "by design edit: unclosed fence closed at page end"
                  : cls.Contains("escape") || cls.Contains("closer") ? "known edit: " + cls : "BUG edit: " + cls;
        Report(label, baseText, "edited page " + before[k].Key + " to:\n" + Show(now) + "\n" + diff);
    }

    static void CheckSubpage(byte[] baseBytes, string baseText, Random r)
    {
        var d = Open(baseBytes);
        var orig = Snap(d);
        Overreach = null;
        var page = orig[r.Next(orig.Count)];
        var text = page.Value;
        if (text.Trim().Length == 0) return;

        // 範囲: 行の単位（見出しから数行など）か、でたらめな位置
        int s, e;
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        if (r.NextDouble() < 0.6)
        {
            int a = r.Next(starts.Count), b = r.Next(a, starts.Count + 1);
            s = starts[a];
            e = b < starts.Count ? starts[b] : text.Length;
            if (r.NextDouble() < 0.3 && e > s && text[e - 1] == '\n') e--;
        }
        else
        {
            s = r.Next(text.Length + 1); e = r.Next(s, text.Length + 1);
        }
        if (text.Substring(s, e - s).Trim().Length == 0) return;   // 選んでいない扱い（Extract_Click 64行）

        // 新しい Id が、どこかに書かれている行き先の無いリンクと同じになるか（作る前のリンクの行き先と比べる）
        var linkedBefore = new List<KeyValuePair<string, HashSet<string>>>();
        foreach (var p in d.Pages) linkedBefore.Add(new KeyValuePair<string, HashSet<string>>(p.Id, LinkTargets(p.Text)));

        var id = Extract(d, page.Key, s, e - s);
        foreach (var p in linkedBefore)
            if (p.Value.Contains(id)) { Report("BUG subpage: new page id reuses a dangling link target", baseText, "new id " + id + " is already linked from page '" + p.Key + "'"); break; }
        string desc = "extract page '" + page.Key + "' [" + s + "," + e + ") = " + Show(text.Substring(s, e - s));

        // もう1つ作る（作ったページが最後のページでなくなる）
        string id2 = null;
        if (r.NextDouble() < 0.5)
        {
            var other = d.Pages[r.Next(d.Pages.Count)];
            if (other.Text.Trim().Length > 0)
            {
                var t2 = other.Text; int s2 = r.Next(t2.Length), e2 = r.Next(s2, t2.Length + 1);
                if (t2.Substring(s2, e2 - s2).Trim().Length > 0)
                {
                    desc += "\nthen extract page '" + other.Id + "' [" + s2 + "," + e2 + ")";
                    id2 = Extract(d, other.Id, s2, e2 - s2);
                    // 2つめの親を覚える
                    parent2 = other.Id;
                }
            }
        }

        bool reopened = r.NextDouble() < 0.7;
        if (reopened)
        {
            byte[] saved;
            try { saved = Save(d); }
            catch (IOException ex) { Report("BUG subpage: save refused after extract", baseText, desc + "\n" + ex.Message); return; }
            try { d = Open(saved); }
            catch (IOException ex) { Report("BUG subpage: saved file does not reopen", baseText, desc + "\n" + ex.Message + "\n" + Show(Text(saved))); return; }
            desc += "\nsave + reopen";
        }

        string parent;
        if (id2 != null)
        {
            if (!Restore(d, id2, parent2, out parent)) { Report("BUG subpage: second link not found on restore", baseText, desc); return; }
            if (parent != parent2) { Report("BUG subpage: restored into a different page", baseText, desc + "\nrestored " + id2 + " into '" + parent + "' (expected '" + parent2 + "')"); return; }
        }
        if (!Restore(d, id, page.Key, out parent))
        {
            Report("INFO subpage: link not found on restore", baseText, desc);
            return;
        }
        if (parent != page.Key) { Report("BUG subpage: restored into a different page", baseText, desc + "\nrestored into '" + parent + "' (expected '" + page.Key + "')"); return; }

        var got = Snap(d);
        var diff = Diff(orig, got);
        if (diff == null) return;
        if (Overreach != null)
        {
            Report("BUG subpage: restore replaced more than the link (text before it is lost)", baseText, desc + "\nreplaced: " + Show(Overreach) + "\n" + diff);
            return;
        }
        string cls = orig.Count == got.Count ? string.Join(",", Enumerable.Range(0, got.Count).Select(i => Classify(orig[i].Value, got[i].Value)).Where(c => c != "same").SelectMany(c => c.Split('+')).Distinct().OrderBy(c => c)) : "pages";
        Report("BUG subpage: not restored exactly" + (reopened ? " after save+reopen" : " (in memory)") + " (" + cls + ")", baseText, desc + "\n" + diff);
    }
    static string parent2;
    static string Overreach;

    /// <summary>Markdig がリンクと読む、# で始まる行き先（画面で押せば開くもの）。</summary>
    static HashSet<string> LinkTargets(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var md = Markdig.Markdown.Parse(text ?? "", MarkdownParser.Pipeline);
        foreach (var l in md.Descendants<LinkInline>()) if (l.Url != null && l.Url.StartsWith("#") && !l.IsImage) set.Add(l.Url.Substring(1));
        return set;
    }

    static void CheckOrphans(PageDocument d, string input)
    {
        var flagged = new HashSet<string>(d.FindUnreferenced().Select(p => p.Id));
        // 画面でリンクを押して辿れるページ（ルートから）
        var reached = new HashSet<string> { "" };
        var q = new Queue<string>(); q.Enqueue("");
        while (q.Count > 0)
        {
            var from = q.Dequeue();
            foreach (var t in LinkTargets(d.TextOf(from)))
                if (d.Contains(t) && reached.Add(t)) q.Enqueue(t);
        }
        foreach (var id in flagged.Where(reached.Contains))
            Report("BUG orphan: page reachable by clicking a link is listed as orphan (would be deleted)", input, "page '" + id + "'");
    }

    static void CheckLines(PageDocument d, string input)
    {
        var lines = d.ComposeText().Replace("\r\n", "\n").Split('\n');
        foreach (var p in d.Pages)
        {
            var pl = p.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int first, last; bool toEnd;
            if (!d.LineRangeOf(p.Id, out first, out last, out toEnd)) { Report("BUG lines: LineRangeOf finds no range", input, "page '" + p.Id + "'"); continue; }
            var before = d.LinesBefore(p.Id);
            bool ok = true;
            for (int i = 0; i < pl.Length && ok; i++)
            {
                int at = first - 1 + i;
                if (at >= lines.Length || (lines[at] != pl[i] && lines[at] != "\\" + pl[i].TrimStart(' '))) ok = false;
            }
            if (!ok) { Report("BUG lines: LineRangeOf points at other lines (AI would edit the wrong place)", input, "page '" + p.Id + "' first=" + first); continue; }
            if (before + 1 != first) Report("BUG lines: LinesBefore disagrees with LineRangeOf", input, "page '" + p.Id + "' before=" + before + " first=" + first);
            if (last < first + pl.Length - 1) Report("BUG lines: LineRangeOf ends before the page ends (AI is told the rest is another page)", input, "page '" + p.Id + "' first=" + first + " last=" + last + " page lines=" + pl.Length);
        }
    }

    // ---------------------------------------------------------------
    // 表: MarkdownView.EditTable の、表エディタで何も変えずに保存したときの道筋
    // ---------------------------------------------------------------

    static void CheckTables(PageDocument d, string input)
    {
        foreach (var p in d.Pages) CheckTablesIn(p.Text, input, p.Id);
    }

    public static string CheckTableText(string text, out string detail)
    {
        detail = null;
        var lines = text.Split('\n').ToList();
        var offs = new List<int>(); int o = 0; foreach (var l in lines) { offs.Add(o); o += l.Length + 1; }
        var model = MarkdownParser.Parse(text, lines, offs);
        for (int i = 0; i < lines.Count; i++)
        {
            if (model.Lines[i].Kind != LineKind.Table || (i > 0 && model.Lines[i - 1].Kind == LineKind.Table)) continue;
            int last = i; while (last + 1 < lines.Count && model.Lines[last + 1].Kind == LineKind.Table) last++;
            var original = lines.GetRange(i, last - i + 1);
            var tm = TableModel.Parse(original);
            if (tm == null) continue;
            if (tm.ExtraCellRows > 0) continue;   // 開く前に知らせて選ばせる（MarkdownView.EditTable 1658行）
            var exchange = tm.ToExchange();
            if (!tm.ReplaceWith(exchange)) continue;
            var replaced = tm.Serialize("\n");
            var after = string.Join("\n", lines.Take(i)) + (i > 0 ? "\n" : "") + replaced + (last + 1 < lines.Count ? "\n" + string.Join("\n", lines.Skip(last + 1)) : "");
            var h0 = Html(text); var h1 = Html(after);
            if (h0 != h1)
            {
                detail = "table at line " + (i + 1) + ":\n" + Show(string.Join("\n", original)) + "\n-> written back as:\n" + Show(replaced) + "\nhtml before: " + Show(h0) + "\nhtml after:  " + Show(h1);
                return original.Any(l => l.Contains('\t')) ? "tab" : "html";
            }
        }
        return null;
    }

    static void CheckTablesIn(string text, string input, string id)
    {
        string detail;
        var cls = CheckTableText(text, out detail);
        if (cls == "tab") Report("by design table: tab in a cell becomes a space", input, "page '" + id + "' " + detail);
        else if (cls != null) Report("BUG table: no-change table edit changes the rendering", input, "page '" + id + "' " + detail);
    }

    static string Html(string text) => Regex.Replace(Markdig.Markdown.ToHtml(text, MarkdownParser.Pipeline), @"\s+", " ").Trim();

    // ---------------------------------------------------------------

    static void Demo()
    {
        void Case(string title, string file, Action<PageDocument> act)
        {
            Console.WriteLine("=== " + title);
            Console.WriteLine("--- file ---\n" + Show(file));
            try
            {
                var d = Open(new UTF8Encoding(false).GetBytes(file));
                act(d);
            }
            catch (Exception ex) { Console.WriteLine("exception: " + ex.GetType().Name + ": " + ex.Message); }
            Console.WriteLine();
        }
        void Pages(PageDocument d) { foreach (var p in d.Pages) Console.WriteLine("  page '" + p.Id + "': " + Show(p.Text)); }
        PageDocument Reopen(PageDocument d) { var b = Save(d); Console.WriteLine("--- saved ---\n" + Show(Text(b))); return Open(b); }

        Case("0. 角かっこの中の文字をサブページにして、すぐ解除（保存しない）", "メモ [ここを詳しく書く] 続き\n", d =>
        {
            var t = d.TextOf("");
            var s = t.IndexOf("ここ");
            var id = Extract(d, "", s, "ここを詳しく書く".Length);
            Console.WriteLine("after extract: " + Show(d.TextOf("")));
            string parent; Restore(d, id, "", out parent);
            Console.WriteLine("after restore: " + Show(d.TextOf("")) + (d.TextOf("") == t ? "  same" : "  CHANGED"));
        });
        Case("1. コードの例の途中（印の形の行から）をサブページにして、保存・開き直し・解除", "## 形式\n\n```markdown\n# 親\n<!--page: p9-->\n## 子\n```\n\n後ろの文\n", d =>
        {
            var t = d.TextOf("");
            var s = t.IndexOf("<!--page"); var e = t.IndexOf("後ろ");
            var id = Extract(d, "", s, e - s);
            d = Reopen(d);
            string parent; Restore(d, id, "", out parent);
            Console.WriteLine("--- after restore ---"); Pages(d);
            Console.WriteLine(d.TextOf("") == t ? "same" : "CHANGED");
        });
        Case("2. 見出しからコードブロックの途中までをサブページにし、もう1つ作ってから保存・開き直し・解除", "## A\n説明\n```\nx = 1\ny = 2\n```\n本文\n\n## B\nbb\n", d =>
        {
            var t = d.TextOf("");
            var id = Extract(d, "", 0, t.IndexOf("y = 2"));             // "## A\n説明\n```\nx = 1\n"
            var b = d.TextOf(""); var id2 = Extract(d, "", b.IndexOf("bb"), 2);
            d = Reopen(d);
            string parent; Restore(d, id2, "", out parent); Restore(d, id, "", out parent);
            Console.WriteLine("--- after restore ---"); Pages(d);
            Console.WriteLine(d.TextOf("") == t ? "same" : "CHANGED");
        });
        Case("2b. 最後が閉じていないコードブロックの文書で、前のほうをサブページにして保存・開き直し・解除", "## A\naa\n\n```\ncode", d =>
        {
            var t = d.TextOf("");
            var id = Extract(d, "", t.IndexOf("aa"), 2);
            d = Reopen(d);
            string parent; Restore(d, id, "", out parent);
            Console.WriteLine("after restore: " + Show(d.TextOf("")) + (d.TextOf("") == t ? "  same" : "  CHANGED"));
        });
        Case("2c. 自分へのリンクを含む範囲をサブページにし、解除（別のページに古いリンク #p1 が残っている）", "[前に消したページ](#p1)\n[子](#p2)\n<!--page: p2-->\n## 子\n[このページの先頭へ](#p2)\n本文\n", d =>
        {
            var t = d.TextOf("p2");
            var s = t.IndexOf("[この");
            var id = Extract(d, "p2", s, t.Length - s);
            Console.WriteLine("new page id: " + id);
            string parent; var ok = Restore(d, id, "p2", out parent);
            Console.WriteLine("restored=" + ok + " into '" + parent + "' (expected 'p2')"); Pages(d);
        });
        Case("3. 閉じていないフェンスのあとに、後ろのページのコードブロック", "intro\n```\nunclosed\n<!--page: p1-->\n# P1\n```js\nx\n```\n<!--page: p2-->\n# P2\n", d => { Pages(d); });
        Case("4. 同じ Id の印が2つ。行き先の無いリンク (#p2) がある", "[a](#p1) [b](#p2)\n<!--page: p1-->\none\n<!--page: p1-->\ntwo\n", d => { Pages(d); });
        Case("5. リスト・引用の中のリンクの定義", "see [x][r]\n\n> [r]: #p1\n<!--page: p1-->\nbody\n", d =>
        {
            Console.WriteLine("orphans: " + string.Join(",", d.FindUnreferenced().Select(p => p.Id)));
        });
        Case("6. コードの中に印の形の行があるとき、AI に知らせる行の範囲", "```\n<!--page: p1-->\n```\n<!--page: p1-->\nP1 line1\n```\n<!--page: p2-->\n```\nP1 line5\n<!--page: p2-->\nP2\n", d =>
        {
            int f, l; bool te; d.LineRangeOf("p1", out f, out l, out te);
            Console.WriteLine("LinesBefore(p1)=" + d.LinesBefore("p1") + " LineRangeOf(p1)=" + f + ".." + l + " (本当は 5..9)");
        });
        Case("7. リストの中の表を表エディタで何も変えずに保存", "- | a | b |\n  |---|---|\n  | 1 | 2 |\n- next\n", d =>
        {
            string detail; var c = CheckTableText(d.TextOf(""), out detail); Console.WriteLine(c == null ? "same" : detail);
        });
        foreach (var t in new[] { "[x](\n#p1)", "[x][r]\n\n> [r]: #p1", "[x][r]\n\n- [r]: #p1", "[x][r]\n\n[r]:\n#p1", "[x](#p&#49;)", "[x](#a\\-b)", "[x](<#p1>)",
            "[x][r]\n\n[r]: #p1", "[x][R]\n\n[r]: #p1", "[x]\n\n[x]: #p1", "<a href=\"#p1\">x</a>", "[x](#p1 'title')", "[x](%23p1)" })
        {
            var d = Open(new UTF8Encoding(false).GetBytes(t + "\n<!--page: p1-->\nP1\n<!--page: a-b-->\nAB\n"));
            Console.WriteLine("orphan " + Show(t).Replace("\n\n", "\\n") + " => listed: " + string.Join(",", d.FindUnreferenced().Select(p => p.Id)) + "  clickable: " + string.Join(",", LinkTargets(t)));
        }
        foreach (var t in new[] {
            "| a\\\\| b |\n|---|---|\n| 1 | 2 |", "| a | b |\n|---|\n| 1 | 2 |", "a | b\n---|---\n1 | 2\nlazy", "| `a|b` | c |\n|---|---|\n| 1 | 2 |",
            "| `a\\|b` | c |\n|---|---|\n| 1 | 2 |", "| a\\ | b |\n|---|---|\n| 1 | 2 |", "| a | b |\n|---|---|\n| 1 | 2 |\n> q", "| a | b |\n| - | - |\n| *x* | [l](#p1) |",
            "|a|b|\n|-|-|\n|1|2|\n|3", "| a | b |\n|---|---|\n| 1 | 2 | 3 |", "* | a | b |\n  |---|---|", "> | a | b |\n> |---|---|", "- x\n\n  | a | b |\n  |---|---|\n  | 1 | 2 |" })
        {
            string detail; var c = CheckTableText(t, out detail);
            Console.WriteLine("table " + Show(t).Replace("\n\n", "\\n") + " => " + (c == null ? "same" : detail));
        }
    }

    static int Main(string[] args)
    {
        Dir = Path.Combine(Path.GetTempPath(), "page-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        try
        {
            if (args.Length > 0 && args[0] == "demo") { Demo(); return 0; }

            int n = args.Length > 0 ? int.Parse(args[0]) : 3000;
            int seed = args.Length > 1 ? int.Parse(args[1]) : 1;
            var rnd = new Random(seed);
            for (int i = 0; i < n; i++)
            {
                var bytes = Gen.File(rnd);
                try { Check(bytes, rnd); }
                catch (Exception ex) { Report("BUG exception " + ex.GetType().Name, Text(bytes), ex.ToString()); }
                if (fileNo > 200) { foreach (var f in Directory.GetFiles(Dir)) File.Delete(f); fileNo = 0; }
            }

            Console.WriteLine("files=" + n + " seed=" + seed);
            foreach (var kv in Hits.OrderBy(k => k.Key))
            {
                var best = kv.Value.OrderBy(h => h.Input.Length).First();
                Console.WriteLine("=== " + kv.Key + "  (hits " + kv.Value.Count + ")");
                Console.WriteLine("--- input ---\n" + Show(best.Input));
                Console.WriteLine("--- detail ---\n" + best.Detail);
                Console.WriteLine();
            }
            var bugs = Hits.Keys.Where(k => k.StartsWith("BUG")).ToList();
            Console.WriteLine(bugs.Count > 0 ? "NG: " + bugs.Count + " kinds" : "OK");
            return bugs.Count > 0 ? 1 : 0;
        }
        finally
        {
            try { Directory.Delete(Dir, true); } catch (Exception) { }
        }
    }
}
