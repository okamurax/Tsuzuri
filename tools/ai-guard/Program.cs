using System;
using System.Collections.Generic;
using System.IO;
using Tsuzuri.Markdown;

// AI に渡さない場所（AiGuard。2026-11-09）の確かめ。作業用のフォルダに「目印のある金庫のフォルダ」と「ふつうのフォルダ」を作り、
// Claude のツールの呼び出しを断るか・通すかを見る。Mac で動かすので場所は / で書く（\ と Git Bash の書き方は文字での照合だけを見る）。
internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-guard-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "Vault");
        var plain = Path.Combine(root, "Notes");
        Directory.CreateDirectory(Path.Combine(vault, ".secret", "git"));
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(vault + "2");   // 名前が金庫で始まる、ふつうのフォルダ
        File.WriteAllText(Path.Combine(vault + "2", "memo.md"), "x");
        File.WriteAllText(Path.Combine(vault, AiGuard.MarkerName), string.Empty);
        File.WriteAllText(Path.Combine(vault, "secret.md"), "x");
        File.WriteAllText(Path.Combine(plain, "memo.md"), "x");

        try
        {
            // まだ目印のフォルダを知らない間。Bash に書いた場所も上へたどる（2026-11-15）
            Expect("Bash cat 金庫（まだ知らない）", true, "Bash", plain, ("command", "cat " + vault + "/secret.md"));
            Expect("Bash cat 金庫（引用符・まだ知らない）", true, "Bash", plain, ("command", "cat \"" + vault + "/secret.md\""));
            Expect("Read 金庫の文書", true, "Read", plain, ("file_path", Path.Combine(vault, "secret.md")));
            Expect("Read 履歴の git の中", true, "Read", plain, ("file_path", Path.Combine(vault, ".secret", "git", "HEAD")));
            Expect("Read ふつうの文書", false, "Read", plain, ("file_path", Path.Combine(plain, "memo.md")));
            Expect("Read 作業のフォルダからの相対", true, "Read", vault, ("file_path", "secret.md"));
            Expect("Write 金庫に書く", true, "Write", plain, ("file_path", Path.Combine(vault, "new.md")), ("content", "x"));
            Expect("Edit ふつうの文書（中身に金庫の場所）", false, "Edit", plain, ("file_path", Path.Combine(plain, "memo.md")),
                   ("old_string", "x"), ("new_string", vault));
            Expect("Grep 金庫で検索", true, "Grep", plain, ("pattern", "bank"), ("path", vault));
            Expect("Grep 場所なし・作業のフォルダが金庫", true, "Grep", vault, ("pattern", "bank"));
            Expect("Glob 場所なし・ふつうのフォルダ", false, "Glob", plain, ("pattern", "*.md"));

            // 上で目印のフォルダを見つけたので、ここからは知っている
            Expect("Bash cat 金庫", true, "Bash", plain, ("command", "cat " + vault + "/secret.md"));
            Expect("Bash 大文字・// で書く", true, "Bash", plain, ("command", "cat " + vault.ToUpperInvariant().Replace("/", "//") + "//secret.md"));
            Expect("Bash \\ で書く", true, "Bash", plain, ("command", "type " + vault.Replace('/', '\\') + "\\secret.md"));
            Expect("Bash 関係のないコマンド", false, "Bash", plain, ("command", "git status"));
            Expect("Grep 上のフォルダから検索", true, "Grep", plain, ("pattern", "bank"), ("path", root));
            Expect("Glob 上のフォルダから・場所なし", true, "Glob", root, ("pattern", "**/*.md"));
            Expect("Glob パターンに金庫の場所", true, "Glob", plain, ("pattern", vault + "/**/*.md"));
            Expect("Grep 隣のふつうのフォルダ", false, "Grep", root, ("pattern", "bank"), ("path", plain));

            // 2026-11-15 に足したもの
            Expect("Grep path に .（作業のフォルダが上）", true, "Grep", root, ("pattern", "bank"), ("path", "."));
            Expect("Grep path に ..（ふつうのフォルダから上へ）", true, "Grep", plain, ("pattern", "bank"), ("path", ".."));
            Expect("Bash 名前が金庫で始まる別のフォルダ", false, "Bash", plain, ("command", "cat " + vault + "2/memo.md"));
            Expect("Read 名前が金庫で始まる別のフォルダ", false, "Read", plain, ("file_path", Path.Combine(vault + "2", "memo.md")));
            Expect("Bash 関係のないコマンド（コロン入り）", false, "Bash", plain, ("command", "git commit -m \"fix: ok\" && echo type: done"));

            // ドライブの一番上に目印を置いたとき（文字での照合だけ。Mac にドライブは無いので Mentions を直に見る）
            ExpectMentions("e: を含むだけのコマンド", false, "git commit -m \"fix: ok\"; echo $env:PATH; type: x", "E:\\");
            ExpectMentions("file:/// の URL", false, "curl file:///c:/x", "E:\\");
            ExpectMentions("E:\\ の下を読む", true, "type E:\\pw.md", "E:\\");
            ExpectMentions("E:/ の下を読む", true, "cat \"E:/pw.md\"", "E:\\");
            ExpectMentions("Git Bash の /e/", true, "cat /e/pw.md", "E:\\");
            ExpectMentions("Git Bash の /etc は別", false, "cat /etc/hosts", "E:\\");
            ExpectMentions("フォルダの名前の続き（vault2）", false, "cat c:/x/vault2/a.md", "C:\\x\\vault");
            ExpectMentions("フォルダそのもの（末尾）", true, "dir C:\\x\\vault", "C:\\x\\vault");
            ExpectMentions("Git Bash の /c/x/vault", true, "ls /c/x/vault/", "C:\\x\\vault");

            // 目印を消したら、覚えを捨てたあとは断らない（前は再起動するまで断り続けた）
            File.Delete(Path.Combine(vault, AiGuard.MarkerName));
            AiGuard.ForgetMarkers();
            Expect("目印を消したあとの Bash", false, "Bash", plain, ("command", "cat " + vault + "/secret.md"));
            Expect("目印を消したあとの Grep（上から）", false, "Grep", plain, ("pattern", "bank"), ("path", root));
        }
        finally
        {
            Directory.Delete(root, true);
        }

        Console.WriteLine(_failed == 0 ? "OK" : "NG（" + _failed + " 件）");
        return _failed == 0 ? 0 : 1;
    }

    private static void ExpectMentions(string name, bool mentions, string text, string folder)
    {
        if (AiGuard.Mentions(text, new[] { folder }) == mentions) return;
        _failed++;
        Console.WriteLine("NG  " + name + ": " + (mentions ? "当たるはずが当たらない" : "当たらないはずが当たった"));
    }

    private static void Expect(string name, bool refuse, string tool, string cwd, params (string Key, string Value)[] items)
    {
        var input = new List<KeyValuePair<string, string>>();
        foreach (var item in items) input.Add(new KeyValuePair<string, string>(item.Key, item.Value));

        var refused = AiGuard.Check(tool, cwd, input) != null;
        if (refused == refuse) return;
        _failed++;
        Console.WriteLine("NG  " + name + ": " + (refuse ? "断るはずが通した" : "通すはずが断った"));
    }
}
