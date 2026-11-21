using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tsuzuri.Markdown;

// テキストファイル（.txt）の読み書きの確かめ（2026-11-21。tools/text-file.sh から動かす）。
// 本体の PageDocument・MarkdownParser をそのままコンパイルし、一時フォルダのファイルで読み書きする。
internal static class Program
{
    private static int _ng;
    private static string _folder;
    private static Encoding _sjis;

    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // .NET 8 には Shift_JIS が入っていないので足す（本体の .NET Framework には入っている）
        _sjis = Encoding.GetEncoding(932);

        var count = args.Length > 0 ? int.Parse(args[0]) : 2000;
        var seed = args.Length > 1 ? int.Parse(args[1]) : 1;

        _folder = Path.Combine(Path.GetTempPath(), "tsuzuri-text-file-" + Guid.NewGuid().ToString("N"));
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
        Console.WriteLine("NG: " + what);
    }

    private static string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>読んで、直さずに保存して、バイト列が変わらないか。読めた文書を返す。</summary>
    private static PageDocument RoundTrip(string name, byte[] bytes, string what)
    {
        var path = Write(name, bytes);
        PageDocument document;
        try
        {
            document = PageDocument.Load(path);
        }
        catch (Exception ex)
        {
            Check(false, what + ": 読めない（" + ex.Message + "）");
            return null;
        }

        document.SetText(PageDocument.RootId, document.TextOf(PageDocument.RootId) + "x");
        document.SetText(PageDocument.RootId, document.TextOf(PageDocument.RootId).Substring(0, document.TextOf(PageDocument.RootId).Length - 1));
        try
        {
            document.Save();
        }
        catch (Exception ex)
        {
            Check(false, what + ": 保存できない（" + ex.Message + "）");
            return document;
        }
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), what + ": 保存でバイト列が変わった");
        return document;
    }

    private static byte[] Utf8(string text, bool bom = false)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    private static void Named()
    {
        // ページの印・フェンス・見出しの形の行も本文のまま（ページに分けない・\ を付けない・フェンスを閉じない）
        var tricky = "# 見出しではない\r\n<!--page: p1-->\r\n```\r\n閉じないフェンス\r\n<!--page: p2-->\r\n- [ ] x\r\n";
        var document = RoundTrip("tricky.txt", Utf8(tricky), "印とフェンスのある UTF-8（CRLF）");
        if (document != null)
        {
            Check(document.IsPlain && document.Pages.Count == 1, "テキストファイルなのにページに分けた: " + document.Pages.Count);
            Check(document.TextOf(PageDocument.RootId) == tricky.Replace("\r\n", "\n"), "中身が違う");
            Check(document.EncodingName == "UTF-8", "文字コードの名前: " + document.EncodingName);
            int first, last;
            bool toEnd;
            Check(document.LineRangeOf(PageDocument.RootId, out first, out last, out toEnd) && first == 1 && last == 7 && toEnd,
                  "行の範囲が全部でない: " + first + "〜" + last);
            Check(!document.LineRangeOf("p1", out first, out last, out toEnd), "無いページの行の範囲を返した");
            try
            {
                document.AddPage("x");
                Check(false, "テキストファイルにページを足せた");
            }
            catch (InvalidOperationException)
            {
            }
        }

        var bom = RoundTrip("bom.txt", Utf8("あいう\n", true), "BOM 付きの UTF-8");
        if (bom != null) Check(bom.EncodingName == "UTF-8（BOM 付き）", "BOM の名前: " + bom.EncodingName);

        RoundTrip("empty.txt", new byte[0], "空のファイル");
        RoundTrip("noeol.txt", Utf8("最後に改行が無い"), "最後に改行が無い");
        RoundTrip("lf.txt", Utf8("a\n\n\nb\n\n"), "空行の続く LF");

        // Shift_JIS
        var japanese = "設定=値\r\n表示＝ソ・十・能（2バイト目が \\ の字）\r\n半角ｶﾅ\r\n";
        var sjisBytes = _sjis.GetBytes(japanese);
        var sjis = RoundTrip("sjis.txt", sjisBytes, "Shift_JIS");
        if (sjis != null)
        {
            Check(sjis.EncodingName == "Shift_JIS", "Shift_JIS の名前: " + sjis.EncodingName);
            Check(sjis.TextOf(PageDocument.RootId) == japanese.Replace("\r\n", "\n"), "Shift_JIS の中身が違う");

            // 足した文字も Shift_JIS で書く
            sjis.SetText(PageDocument.RootId, sjis.TextOf(PageDocument.RootId) + "追加");
            sjis.Save();
            Check(File.ReadAllBytes(sjis.FilePath).SequenceEqual(_sjis.GetBytes(japanese + "追加")), "足した文字を Shift_JIS で書かない");

            // 書けない文字（絵文字）があれば保存をやめ、ファイルは変えない
            var before = File.ReadAllBytes(sjis.FilePath);
            sjis.SetText(PageDocument.RootId, sjis.TextOf(PageDocument.RootId) + "😀");
            try
            {
                sjis.Save();
                Check(false, "Shift_JIS で書けない文字を保存した");
            }
            catch (IOException ex)
            {
                Check(ex.Message.Contains("😀"), "書けない文字を知らせない: " + ex.Message);
            }
            Check(File.ReadAllBytes(sjis.FilePath).SequenceEqual(before), "保存をやめたのにファイルが変わった");
        }

        // 読めない文字コードは開かない（UTF-16 の BOM・Shift_JIS としても壊れたバイト）
        foreach (var bad in new[] { new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, new byte[] { 0x41, 0x00, 0x42, 0x00 }, new byte[] { 0x82, 0x0A }, new byte[] { 0x41, 0xA0, 0xFD }, new byte[] { 0x82, 0xA0, 0xFF } })
        {
            var path = Write("bad.txt", bad);
            try
            {
                PageDocument.Load(path);
                Check(false, "読めないバイト列を開いた: " + BitConverter.ToString(bad));
            }
            catch (IOException)
            {
            }
        }

        // .md は今までどおり（Shift_JIS は開かない・印でページに分ける・テキストファイルではない）
        try
        {
            PageDocument.Load(Write("sjis.md", sjisBytes));
            Check(false, ".md を Shift_JIS で開いた");
        }
        catch (IOException)
        {
        }
        var md = PageDocument.Load(Write("pages.md", Utf8("root\n<!--page: p1-->\nsub\n")));
        Check(!md.IsPlain && md.Pages.Count == 2, ".md のページの分け方が変わった");
        Check(PageDocument.IsPlainText("a.TXT") && !PageDocument.IsPlainText("a.md") && !PageDocument.IsPlainText("a.log") && !PageDocument.IsPlainText(null), "IsPlainText");

        // 解析: どの行もただの段落（見出し・リスト・記号が無い）
        var lines = new List<string> { "# 見出しの形", "- [x] チェック", "", "**太字**", "| a | b |" };
        var model = MarkdownParser.ParsePlain(lines);
        Check(model.Headings().Count == 0, "テキストファイルの解析に見出しがある");
        for (var i = 0; i < lines.Count; i++)
        {
            var info = model.LineAt(i + 1);
            var expected = lines[i].Length == 0 ? LineKind.Blank : LineKind.Paragraph;
            Check(info.Kind == expected && info.Content == lines[i] && info.Marker.Length == 0 && info.Depth == 0
                  && model.InlineAt(i + 1).Spans.Count == 0, "解析が素でない: " + lines[i]);
        }
    }

    private static readonly string[] Pieces =
    {
        "<!--page: p1-->", "<!--page: x-->", "```", "~~~", "# h", "- [ ] t", "\\<!--page: p1-->", "日本語", "ｶﾅ", "表", "ソ", "\t", "  ", "a", "=", "|", "\\",
    };

    private static void Random(int count, int seed)
    {
        var random = new Random(seed);
        for (var n = 0; n < count; n++)
        {
            var newLine = random.Next(2) == 0 ? "\n" : "\r\n";
            var lines = new List<string>();
            var lineCount = random.Next(0, 12);
            for (var i = 0; i < lineCount; i++)
            {
                var line = new StringBuilder();
                var pieces = random.Next(0, 4);
                for (var j = 0; j < pieces; j++) line.Append(Pieces[random.Next(Pieces.Length)]);
                lines.Add(line.ToString());
            }
            var text = string.Join(newLine, lines) + (random.Next(2) == 0 ? newLine : string.Empty);

            var kind = random.Next(3);
            var bytes = kind == 0 ? Utf8(text) : kind == 1 ? Utf8(text, true) : _sjis.GetBytes(text);
            var name = "r" + n + ".txt";
            var before = _ng;
            var document = RoundTrip(name, bytes, "でたらめ " + n + "（" + (kind == 0 ? "UTF-8" : kind == 1 ? "BOM" : "Shift_JIS") + "）");
            if (document != null)
            {
                Check(document.Pages.Count == 1, "でたらめ " + n + ": ページに分けた");
                Check(document.TextOf(PageDocument.RootId) == text.Replace("\r\n", "\n"), "でたらめ " + n + ": 中身が違う");
            }
            if (_ng > before)
            {
                Console.WriteLine("  中身: " + text.Replace("\r", "\\r").Replace("\n", "\\n\n         "));
                if (_ng > 20) return;
            }
        }
    }
}
