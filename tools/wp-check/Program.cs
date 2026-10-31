using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Tsuzuri.WordPress;

// WordPress の変換の確かめ（SPEC 20章。2026-10-31）。
// サイトの全記事の編集用の本文を読み（GET だけ。記事は変えない）、エディタと同じ変換で
//   ダウンロード（HTML → Markdown）→ アップロード（Markdown → HTML）→ もう一度ダウンロード
// をして、次を見る。
//   1. 送る HTML の見える文字が、元と同じか（コメント・タグを除き、文字参照を戻し、空白を除いて比べる。全角の空白は比べる）
//   2. もう一度ダウンロードしても Markdown が変わらないか
//   3. HTML のまま残ったタグ・属性・クラスの一覧（消すか残すかを決めるための材料）
// 1 か 2 で1件でも食い違えば、終了コード 1。
//
// 使い方: tools/wp-check.sh [wordpress.json]（既定は exe の隣の wordpress.json。sites の1つめのサイト）
static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var settingsPath = args.Length > 0 ? args[0] : null;
        if (settingsPath == null || !File.Exists(settingsPath))
        {
            Console.Error.WriteLine("wordpress.json が見つかりません: " + settingsPath);
            return 2;
        }

        var site = JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement.GetProperty("sites")[0];
        var url = site.GetProperty("url").GetString().TrimEnd('/');
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(site.GetProperty("user").GetString() + ":" + site.GetProperty("appPassword").GetString().Replace(" ", "")));

        var posts = Download(url, auth);
        Console.WriteLine("記事: " + posts.Count + " 件（" + url + "）");

        var failures = 0;
        failures += CheckCases();

        var textNg = new List<string>();
        var unstable = new List<string>();
        var tags = new Inventory();
        var attributes = new Inventory();
        var classes = new Inventory();
        var withRaw = 0;

        foreach (var post in posts)
        {
            var markdown = HtmlToMarkdown.Convert(post.Raw);
            var html = WordPressHtml.FromMarkdown(markdown);
            var again = HtmlToMarkdown.Convert(html);

            if (again != markdown) unstable.Add("#" + post.Id + " " + FirstDifference(markdown, again));

            var before = Visible(post.Raw);
            var after = Visible(html);
            if (before != after) textNg.Add("#" + post.Id + " " + FirstDifference(before, after));

            if (Collect(post.Id, markdown, tags, attributes, classes)) withRaw++;
        }

        Console.WriteLine();
        Console.WriteLine("1. 見える文字の食い違い: " + textNg.Count + " 件");
        foreach (var line in textNg.Take(20)) Console.WriteLine("   " + line);
        Console.WriteLine("2. もう一度ダウンロードして変わるもの: " + unstable.Count + " 件");
        foreach (var line in unstable.Take(20)) Console.WriteLine("   " + line);
        Console.WriteLine("3. HTML のまま残る記事: " + withRaw + " 件（ブロックエディタの印などのコメントは数えない）");
        tags.Print("タグ");
        attributes.Print("属性");
        classes.Print("クラス");

        failures += textNg.Count + unstable.Count;
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "OK" : "NG（" + failures + " 件）");
        return failures == 0 ? 0 : 1;
    }

    sealed class Post
    {
        public int Id;
        public string Raw;
    }

    static List<Post> Download(string url, string auth)
    {
        var posts = new List<Post>();
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tsuzuri-wp-check");

            for (var page = 1; ; page++)
            {
                var response = http.GetAsync(url + "/wp-json/wp/v2/posts?context=edit&per_page=100&page=" + page
                    + "&status=publish,future,draft,pending,private&_fields=id,content").Result;
                response.EnsureSuccessStatusCode();

                foreach (var item in JsonDocument.Parse(response.Content.ReadAsStringAsync().Result).RootElement.EnumerateArray())
                    posts.Add(new Post { Id = item.GetProperty("id").GetInt32(), Raw = item.GetProperty("content").GetProperty("raw").GetString() ?? string.Empty });

                IEnumerable<string> values;
                var totalPages = response.Headers.TryGetValues("X-WP-TotalPages", out values) ? int.Parse(values.First()) : page;
                if (page >= totalPages) break;
            }
        }

        return posts;
    }

    /// <summary>
    /// 前に壊れたことのある形（2026-10-30 のレビュー・2026-10-31 の全記事の点検）。どれも、送る HTML の見える文字が元と同じで、往復しても変わらないこと。
    /// </summary>
    static readonly string[] Cases =
    {
        "<!-- wp:paragraph -->\n<p>本文 <strong>太字</strong></p>\n<!-- /wp:paragraph -->\n\n<!-- wp:latest-posts {\"postsToShow\":3} /-->",
        "<p>[caption id=\"a\" align=\"alignnone\" width=\"300\"]<img src=\"x.png\" /> cap[/caption]</p>",
        "<pre class=\"wp-block-code\"><code>a\n\nb\n\n\nc</code></pre>",
        "<div class=\"box\"><pre>x\n\ny</pre></div>",
        "<p>https://example.com/a_b_c</p>",
        "<div class=\"blank-box\"><p>x</p></div><p><span class=\"marker-under\">y</span> <a class=\"btn\" href=\"https://x\">z</a></p>",
        "<table><thead><tr><th>a</th><th>b</th></tr></thead><tbody><tr><td><code>a|b</code></td><td>x|y</td></tr></tbody></table>",
        "<p><strong>c</strong><strong>d</strong></p>",
        "<p>2^10 and 2^20, x==y==z, ++a++</p>",
        "<p>a<br><br>b</p><p>&nbsp;</p><p>&nbsp;&nbsp;indent</p><h2>h<br>2</h2>",
        "<p>&amp;copy; is text, A &amp; B</p>",
        "<p><span class=\"x\">\\\\wsl$\\data\\_x ^.*abc.* \"q\"</span></p>",
        "テキスト\n-Q\n\n=x\n+y",
        "<ul><li>　(字下げ)</li></ul>",
        "<pre class=\"lang:default decode:true \">ip route default\n\npp select 1</pre>",
        "<div dir=\"auto\"><table><tr><th>a</th></tr><tr><td>1</td></tr></table></div>",
    };

    static int CheckCases()
    {
        var failures = 0;
        foreach (var html in Cases)
        {
            var markdown = HtmlToMarkdown.Convert(html);
            var sent = WordPressHtml.FromMarkdown(markdown);
            var again = HtmlToMarkdown.Convert(sent);
            if (Visible(html) != Visible(sent) || again != markdown)
            {
                failures++;
                Console.WriteLine("例で NG: " + html.Replace("\n", "\\n"));
                Console.WriteLine("   Markdown: " + markdown.Replace("\n", "\\n"));
                Console.WriteLine("   送る HTML: " + sent.Replace("\n", "\\n"));
            }
        }

        Console.WriteLine("前に壊れた形の例: " + Cases.Length + " 件中 NG " + failures + " 件");
        return failures;
    }

    /// <summary>見える文字。コメント・script・style・タグを除き、文字参照を戻し、空白（半角・改行・nbsp）を除く。全角の空白は残す。</summary>
    static string Visible(string html)
    {
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", "");
        html = Regex.Replace(html, @"<(script|style)\b[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = System.Net.WebUtility.HtmlDecode(html);
        return Regex.Replace(html, @"[ \t\r\n ]+", "");
    }

    static string FirstDifference(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        var from = Math.Max(0, i - 30);
        Func<string, string> part = s => s.Substring(Math.Min(from, s.Length), Math.Min(80, Math.Max(0, s.Length - from))).Replace("\n", "\\n");
        return "\n      前: " + part(a) + "\n      後: " + part(b);
    }

    static readonly Regex Tag = new Regex(@"<([a-zA-Z][\w-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>");
    static readonly Regex Attribute = new Regex(@"([\w:.-]+)(?:\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+))?");

    /// <summary>Markdown に HTML のまま残ったタグ・属性・クラスを数える。残っていれば true。</summary>
    static bool Collect(int id, string markdown, Inventory tags, Inventory attributes, Inventory classes)
    {
        var document = WordPressHtml.Parse(markdown);
        var fragments = document.Descendants<HtmlBlock>().Select(block => markdown.Substring(block.Span.Start, block.Span.Length))
            .Concat(document.Descendants<HtmlInline>().Select(inline => inline.Tag));

        var any = false;
        foreach (var fragment in fragments)
        {
            if (Regex.IsMatch(fragment.Trim(), @"^<!--[\s\S]*-->$")) continue;

            foreach (Match tag in Tag.Matches(fragment))
            {
                var name = tag.Groups[1].Value.ToLowerInvariant();
                any = true;
                tags.Add(name, id);

                foreach (Match attribute in Attribute.Matches(tag.Groups[2].Value))
                {
                    var key = attribute.Groups[1].Value.ToLowerInvariant();
                    var value = attribute.Groups[2].Value.Trim('"', '\'');
                    attributes.Add(name + "@" + key + (key == "style" ? "=" + value : ""), id);
                    if (key == "class")
                        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) classes.Add(name + "." + token, id);
                }
            }
        }

        return any;
    }

    sealed class Inventory
    {
        readonly Dictionary<string, SortedSet<int>> _ids = new Dictionary<string, SortedSet<int>>();

        public void Add(string key, int id)
        {
            SortedSet<int> ids;
            if (!_ids.TryGetValue(key, out ids)) _ids[key] = ids = new SortedSet<int>();
            ids.Add(id);
        }

        public void Print(string title)
        {
            if (_ids.Count == 0) return;
            Console.WriteLine("   [" + title + "]");
            foreach (var pair in _ids.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key).Take(40))
                Console.WriteLine("   " + pair.Value.Count.ToString().PadLeft(4) + " 記事  " + pair.Key + "  例: #" + string.Join(", #", pair.Value.Take(5)));
        }
    }
}
