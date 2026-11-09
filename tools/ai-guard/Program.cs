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
        File.WriteAllText(Path.Combine(vault, AiGuard.MarkerName), string.Empty);
        File.WriteAllText(Path.Combine(vault, "secret.md"), "x");
        File.WriteAllText(Path.Combine(plain, "memo.md"), "x");

        try
        {
            // まだ目印のフォルダを知らない間（Bash の文字は照合できない。場所の項目は上へたどる）
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
        }
        finally
        {
            Directory.Delete(root, true);
        }

        Console.WriteLine(_failed == 0 ? "OK" : "NG（" + _failed + " 件）");
        return _failed == 0 ? 0 : 1;
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
