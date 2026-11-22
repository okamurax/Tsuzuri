using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tsuzuri.Markdown;

// 検索の置換（SearchReplace）の確かめ（2026-11-22。tools/replace-fuzz.sh から動かす）。
// 本体の SearchReplace.cs をそのままコンパイルし、でたらめな本文・検索語・置き換える文字で、別に書いた素直な置換（答え）と比べる。
// 見るもの:
//  - 編集を本文に当てた結果が答えと1文字も違わない・件数が同じ
//  - 改行（\r\n・\n・\r）の並びが1つも変わらない（行の数も同じ）
//  - 編集の範囲が本文の中にあり、前後の同じ文字を含まず、サロゲートの対を割らない
//  - 改行の入った置き換える文字は断り、何も作らない
//  - 置き換えた本文を .md のページに入れて保存・読み直しても、ページの数・中身が変わらない（ページの印の形の文字を置き換えで作っても、ページに分かれない）
internal static class Program
{
    private static int _ng;
    private static string _folder;

    private static int Main(string[] args)
    {
        var count = args.Length > 0 ? int.Parse(args[0]) : 20000;
        var seed = args.Length > 1 ? int.Parse(args[1]) : 1;

        _folder = Path.Combine(Path.GetTempPath(), "tsuzuri-replace-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        try
        {
            Named();
            Random(count, seed);
        }
        finally
        {
            Directory.Delete(_folder, true);
        }

        Console.WriteLine(_ng == 0 ? "全部 OK（でたらめ " + count + " 個。種 " + seed + "）" : "NG: " + _ng + " 件");
        return _ng == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        if (ok) return;
        _ng++;
        if (_ng <= 30) Console.WriteLine("NG: " + what);
    }

    private static string Show(string text)
    {
        return text == null ? "(null)" : text.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    // --- 答え: 改行で切った行ごとに Regex.Replace（空の当たりは残す）。SearchReplace とは別の書き方にする -----------------

    private static readonly Regex Breaks = new Regex("(\r\n|\r|\n)");

    /// <summary>
    /// 置き換える文字の $ のうち、効かせるのは $数字・${…}・$$ だけ。ほかの $ は $$ にしてそのままの文字にする（SearchReplace.Substitution とは別の書き方）。
    /// </summary>
    private static readonly Regex Dollar = new Regex(@"\$\$|\$(?=[0-9{])|\$");

    private static string OracleSubstitution(string replacement)
    {
        return Dollar.Replace(replacement, match => match.Value == "$" && match.Index + 1 < replacement.Length
            && (char.IsAsciiDigit(replacement[match.Index + 1]) || replacement[match.Index + 1] == '{') ? "$" : "$$");
    }

    private static string Expected(string text, Regex search, string replacement, out int count)
    {
        replacement = OracleSubstitution(replacement);
        var parts = Breaks.Split(text);   // 中身, 改行, 中身, 改行, …, 中身
        var hits = 0;
        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                builder.Append(parts[i]);
                continue;
            }
            builder.Append(search.Replace(parts[i], match =>
            {
                if (match.Length == 0) return match.Value;
                hits++;
                return match.Result(replacement);
            }));
        }
        count = hits;
        return builder.ToString();
    }

    private static List<string> BreakList(string text)
    {
        return Breaks.Matches(text).Cast<Match>().Select(match => match.Value).ToList();
    }

    /// <summary>1組を確かめる。置き換えた本文を返す（断ったら null）。</summary>
    private static string One(string text, string pattern, string replacement, string what)
    {
        Regex search;
        try
        {
            search = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(1000));
        }
        catch (ArgumentException)
        {
            return null;   // 検索語として読めない（画面でも当たりを出さない）
        }

        var label = what + "（本文 " + Show(text) + " / 検索 " + Show(pattern) + " / 置換 " + Show(replacement) + "）";

        if (replacement.IndexOf('\r') >= 0 || replacement.IndexOf('\n') >= 0)
        {
            Check(SearchReplace.Refuse(replacement) != null, label + ": 改行のある置き換える文字を断らない");
            try
            {
                SearchReplace.Make(text, search, replacement);
                Check(false, label + ": 改行のある置き換える文字で編集を作った");
            }
            catch (ArgumentException)
            {
            }
            return null;
        }
        Check(SearchReplace.Refuse(replacement) == null, label + ": 改行の無い置き換える文字を断った");

        int expectedCount;
        string expected;
        try
        {
            expected = Expected(text, search, replacement, out expectedCount);
        }
        catch (ArgumentException)
        {
            expected = null;   // 置き換える文字が読めない（答えも作れない）
            expectedCount = 0;
        }
        // 単独の \r と \n の間の行が空になって改行がくっつく（行が減る）なら、断るのが正しい
        if (expected != null && !BreakList(expected).SequenceEqual(BreakList(text))) expected = null;

        SearchReplace.Plan plan;
        try
        {
            plan = SearchReplace.Make(text, search, replacement);
        }
        catch (ArgumentException ex)
        {
            Check(expected == null, label + ": 答えは作れるのに断った（" + ex.Message + "）");
            return null;
        }
        if (expected == null)
        {
            Check(false, label + ": 答えが作れない置き換える文字なのに編集を作った");
            return null;
        }

        Check(plan.Count == expectedCount, label + ": 件数 " + plan.Count + "（答え " + expectedCount + "）");

        string after;
        if (!plan.Changes)
        {
            after = text;
        }
        else
        {
            var inside = plan.Offset >= 0 && plan.Length >= 0 && plan.Offset + plan.Length <= text.Length && plan.Text != null;
            Check(inside, label + ": 編集の範囲が本文の外 " + plan.Offset + "+" + plan.Length);
            if (!inside) return null;
            after = text.Substring(0, plan.Offset) + plan.Text + text.Substring(plan.Offset + plan.Length);

            Check(plan.Text != text.Substring(plan.Offset, plan.Length), label + ": 何も変えない編集");
            // サロゲートの対を割らない（元の本文で、範囲の端が対の真ん中に来ない）
            Check(!(plan.Offset > 0 && plan.Offset < text.Length && char.IsHighSurrogate(text[plan.Offset - 1]) && char.IsLowSurrogate(text[plan.Offset])),
                  label + ": 範囲の始まりがサロゲートの対を割る");
            var end = plan.Offset + plan.Length;
            Check(!(end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])),
                  label + ": 範囲の終わりがサロゲートの対を割る");
        }

        Check(after == expected, label + ": 結果が違う " + Show(after) + "（答え " + Show(expected) + "）");
        Check(plan.Changes == (expected != text), label + ": 変わるかどうかが違う");
        Check(BreakList(after).SequenceEqual(BreakList(text)), label + ": 改行が変わった");
        return after;
    }

    // --- 決めた例 ---------------------------------------------------------------------------------------------

    private static void Named()
    {
        One("岡山工数PC入れ替え\n### PCパスワード変更\n## リモートPC\n", "PC", "端末", "画像の例");
        One("a\r\nb\rc\nd", "^", "X", "空の当たり（^）は置き換えない");
        One("abc\nabc", "b*", "-", "空の当たりと当たりの混ざり");
        One("2026年11月\n1999年", "(\\d+)年", "$1/", "$1");
        One("PC pc Pc", "pc", "[$0]", "$0 と大文字小文字");
        One("x", "x", "$$", "$$");
        One("ab\ncd", "b", "$_", "$_ は行（本文全体ではない）");
        One("ab\ncd", "b", "$`|$'", "$` と $' は行の中");
        One("ab", "(?<n>a)", "${n}${n}", "名前の括弧");
        One("ab", "a", "$9", "無い括弧の番号はそのままの文字");
        One("😀😁", "😀", "", "サロゲートの対を消す");
        One("😀😁", "😁", "😂", "サロゲートの対を替える（前後が同じ上位サロゲート）");
        One("a😀b", "😀", "😀", "同じ文字に置き換える（変わらない）");
        One("x\n", "x", "y", "最後に改行");
        One("", "x", "y", "空の本文");
        One("\n\n\n", "^$", "z", "空行だけ");
        One("a\r\n", "\r", "", "検索語の \\r は行の中に無い");
        One("a b", "\\s", "\n", "改行のある置き換える文字は断る");
        One("a b", "\\s", "\r", "\\r のある置き換える文字は断る");
        One("text", "t", "<!--page: p1-->", "ページの印の形を作る");
        One("x\rz\n", "z", "", "単独の CR と LF の間の行を空にする（くっつくので断る）");
        One("x\rz\ny", "z", "w", "単独の CR の後ろの行（空にしなければよい）");
        One("x\r\nz\n", "z", "", "CRLF の後ろの行を空にする（くっつかない）");
        One("x\rz\rw", "z", "", "単独の CR が2つ（くっつかない）");
        One("x\r\rz\n", "z", "", "CR が2つ続いた後ろの行を空にする（くっつくので断る）");
        One("x\rzz\r\n", "z", "", "単独の CR と CRLF の間の行を空にする（くっつくので断る）");
        One("z\n", "z", "", "1行目を空にする（前に改行が無いのでくっつかない）");
        One("x\rz\nz\n", "z", "", "単独の CR の後ろの行も、その次の行も空にする（くっつくので断る）");
        Check(Saved("$_POST", "POST", "$_GET") == "$_$_GET", "$_ はそのままの文字");
        Check(Saved("US5", "US", "US$$") == "US$5", "$$ は $");
        Check(Saved("price", "price", "$") == "$", "$ だけ");
        Check(Saved("ab", "(a)", "$1$1") == "aab", "$1");
        Check(Saved("ab", "(a)", "$10") == "$10b", "無い番号の $10 はそのまま");
        Check(Saved("ab", "(?<n>a)", "${n}!") == "a!b", "${名前}");
        Check(Saved("ab", "a", "$0$0") == "aab", "$0");
        Check(Saved("ab", "a", "$`$'$+$&") == "$`$'$+$&b", "$` $' $+ $& はそのまま");

        // ページの印の形の文字を置き換えで作っても、保存して読み直すとページに分かれない
        PageRoundTrip("見出し\nPAGE\n本文\n", "PAGE", "<!--page: p1-->", "印の形の行を作る");
        PageRoundTrip("x\nPAGE\n", "PAGE", "\\<!--page: p1-->", "\\ 付きの印の形の行を作る");
        PageRoundTrip("x\nP\ny\n", "^P$", "```", "コードフェンスの形の行を作る");
    }

    /// <summary>1行の置換の結果（断ったら null）。</summary>
    private static string Saved(string text, string pattern, string replacement)
    {
        var after = One(text, pattern, replacement, "$ の例");
        return after;
    }

    // --- 保存と読み直し ------------------------------------------------------------------------------------------

    /// <summary>ルートと子ページのある .md を作り、ルートの中身を置き換えて保存・読み直し、ページの数・中身が同じか。</summary>
    private static void PageRoundTrip(string rootText, string pattern, string replacement, string what)
    {
        var after = One(rootText, pattern, replacement, what);
        if (after == null) return;
        CheckSaved(after, what + "（本文 " + Show(rootText) + " → " + Show(after) + "）");
    }

    private static int _files;

    private static void CheckSaved(string rootText, string what)
    {
        var path = Path.Combine(_folder, "p" + (_files++) + ".md");
        File.WriteAllText(path, "root\n<!--page: kid-->\nchild\n", new UTF8Encoding(false));
        var document = PageDocument.Load(path);
        var before = document.Pages.Select(page => page.Id).ToList();
        document.SetText(PageDocument.RootId, rootText);
        document.Save();

        var reloaded = PageDocument.Load(path);
        var ids = reloaded.Pages.Select(page => page.Id).ToList();
        Check(ids.SequenceEqual(before), what + ": 保存して読み直すとページが変わった " + string.Join(",", ids));
        Check(SameSaved(rootText, reloaded.TextOf(PageDocument.RootId)), what + ": 保存して読み直すとルートの中身が変わった " + Show(reloaded.TextOf(PageDocument.RootId)));
        Check(reloaded.TextOf("kid") == "child\n" || reloaded.TextOf("kid") == "child", what + ": 子ページの中身が変わった " + Show(reloaded.TextOf("kid")));
    }

    /// <summary>
    /// 保存して読み直した中身が同じか。違ってよいのは、ページの印の形の行の頭に \ が付くことだけ
    /// （手で打った印の形の行と同じく、ページに分けないように PageDocument がそう書く。SPEC 4章）。
    /// </summary>
    private static bool SameSaved(string written, string read)
    {
        if (written == read) return true;
        if (read == null) return false;
        var before = written.Split('\n');
        var after = read.Split('\n');
        if (before.Length != after.Length) return false;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i]) continue;
            if (!(MarkerForm.IsMatch(before[i]) && after[i] == "\\" + before[i].TrimStart(' '))) return false;
        }
        return true;
    }

    private static readonly Regex MarkerForm = new Regex(@"^\s*<!--\s*page:");

    // --- でたらめ ------------------------------------------------------------------------------------------------

    private static readonly string[] Pieces =
    {
        "PC", "pc", "Pc", "ＰＣ", "a", "aa", "b", "ab", " ", "\t", "\n", "\n", "\r\n", "\r", "\r", "\rz\n", "😀", "😁", "日本語", "ｶﾅ", "é", "ß", "İ", "ı", "K", "\u212A",
        "<!--page: p1-->", "\\<!--page: p1-->", "<!-- page: kid -->", "# ", "## 見出し", "- [ ] ", "[名前](#abc123)", "```", "~~~", "|", "$", "\\", "(", ")", "1", "2026",
    };

    private static readonly string[] Patterns =
    {
        "PC", "pc", "a", "a+", "a*", "b?", "^", "$", "^a", "a$", "(a)(b)?", "(a)|(b)", "\\s+", "\\s", ".", ".*", "😀", "[\\uD83D]", "[\\uDE00]",
        "(?<n>p)c", "\\b", "a|", "|", "^$", "\\d+", "(\\d)(\\d)", "<!--page: (\\w+)-->", "page", "#+ ", "\\[.*?\\]", "\\(#\\w+\\)", "k", "i", "ß", "\\$", "\\\\",
        "日本", "[ｶﾅ]", "(?m)^a", "(?s).", "(?=a)", "(?<=a)", "x{0}", "((a))", "\\p{L}+", "[^a]", "\\r", "\\n", "\\r?\\n", ".$",
    };

    private static readonly string[] Replacements =
    {
        "", "X", "端末", "$1", "$2", "$0", "$$", "${n}", "$_", "$`", "$'", "$9", "$", "$+", "$&", "$_GET", "US$$5", "$10", "${1", "${}", "$$1", "a$1b", "\\", "\\n", "😀", "<!--page: p1-->", "\\<!--page: kid-->",
        "# ", "```", "$1$1", "[$0]", "${1}0", "${", "$1$", "\n", "\r", "a\r\nb", "x\ny",
    };

    private static void Random(int count, int seed)
    {
        var random = new Random(seed);
        for (var n = 0; n < count; n++)
        {
            var builder = new StringBuilder();
            var length = random.Next(0, 16);
            for (var i = 0; i < length; i++) builder.Append(Pieces[random.Next(Pieces.Length)]);
            var text = builder.ToString();

            string pattern;
            if (random.Next(4) == 0)
            {
                // 本文の一部をそのまま（エスケープして）検索語にする
                var start = text.Length == 0 ? 0 : random.Next(text.Length);
                var take = Math.Min(text.Length - start, random.Next(1, 6));
                pattern = Regex.Escape(text.Substring(start, take));
            }
            else
            {
                pattern = Patterns[random.Next(Patterns.Length)];
            }

            var replacement = random.Next(5) == 0
                ? Replacements[random.Next(Replacements.Length)] + Replacements[random.Next(Replacements.Length)]
                : Replacements[random.Next(Replacements.Length)];

            var before = _ng;
            var after = One(text, pattern, replacement, "でたらめ " + n);

            // 置き換えた本文をページに入れて保存・読み直し（ページの中身は \n だけ。画面のページの本文と同じ）
            if (after != null && n % 4 == 0 && after.IndexOf('\r') < 0 && !after.Contains("\uFFFD") && !HasLoneSurrogate(after))
                CheckSaved(after, "でたらめ " + n + "（保存）");

            if (_ng > before && _ng > 30) return;
        }
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                return true;
            }
            if (char.IsLowSurrogate(text[i])) return true;
        }
        return false;
    }
}
