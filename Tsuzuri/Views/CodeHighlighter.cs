using System;
using System.Collections.Generic;
using System.Windows.Media;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// コードブロックの中を軽く色分けする（2026-11-04。SPEC 8.4.0）。
    ///
    /// 言語は見分けない。どの言語でもだいたい同じ意味になるもの（文字列・数・コメント・括弧・記号・
    /// よく使う語・「名前:」「名前=」の名前）だけを、1行ずつ頭から1回なめて色を付ける。
    /// 前後の行は見ないので、複数行にまたがる文字列やコメントは、その行の中だけで閉じたものとして扱う。
    /// </summary>
    public static class CodeHighlighter
    {
        public static readonly Brush Plain    = Freeze("#FFD4D4D4");
        public static readonly Brush String   = Freeze("#FFCE9178");
        public static readonly Brush Number   = Freeze("#FFB5CEA8");
        public static readonly Brush Comment  = Freeze("#FF6A9955");
        public static readonly Brush Keyword  = Freeze("#FF569CD6");
        public static readonly Brush Key      = Freeze("#FF9CDCFE");
        public static readonly Brush Bracket  = Freeze("#FFD7BA7D");
        public static readonly Brush Operator = Freeze("#FF9AA4B5");

        /// <summary>多くの言語に共通する語。大文字だけの SQL の語も入れる（大文字小文字は区別する）。</summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "true", "false", "null", "nil", "None", "True", "False", "undefined", "this", "self",
            "if", "else", "elif", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue",
            "return", "yield", "try", "catch", "finally", "throw", "raise", "except", "with", "as", "in", "is", "not", "and", "or",
            "function", "def", "class", "struct", "interface", "enum", "new", "var", "let", "const", "static", "public", "private",
            "protected", "internal", "void", "int", "long", "bool", "string", "double", "float", "using", "namespace", "import",
            "from", "export", "package", "async", "await", "echo", "fi", "then", "done", "end", "sub", "dim",
            "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "TABLE", "ALTER", "DROP",
            "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "ON", "AND", "OR", "NOT", "NULL", "AS", "ORDER", "GROUP", "BY", "HAVING",
            "LIMIT", "DISTINCT", "UNION", "IN", "IS", "LIKE", "BETWEEN", "CASE", "WHEN", "THEN", "ELSE", "END", "PRIMARY", "KEY",
        };

        /// <summary>1行を色分けする。paint(始まり, 長さ, 色) を、色の変わる区切りごとに呼ぶ（Plain の所は呼ばない）。</summary>
        public static void Scan(string text, Action<int, int, Brush> paint)
        {
            var n = text.Length;
            var i = 0;
            var lineStart = true; // 行頭（空白と「- 」のあと）にいるか

            while (i < n)
            {
                var c = text[i];

                if (c == ' ' || c == '\t' || c == '　')
                {
                    i++;
                    continue;
                }

                // コメント。行の残り全部か、同じ行で閉じる /* */・<!-- -->。
                if (IsLineComment(text, i, lineStart))
                {
                    paint(i, n - i, Comment);
                    return;
                }

                if (Starts(text, i, "/*") || Starts(text, i, "<!--"))
                {
                    var close = text.IndexOf(c == '/' ? "*/" : "-->", i + 2, StringComparison.Ordinal);
                    var stop = close < 0 ? n : close + (c == '/' ? 2 : 3);
                    paint(i, stop - i, Comment);
                    i = stop;
                    lineStart = false;
                    continue;
                }

                // 文字列。' は直前が英数字なら文字列にしない（don't などの英文）。
                if (c == '"' || c == '`' || (c == '\'' && !(i > 0 && char.IsLetterOrDigit(text[i - 1]))))
                {
                    var stop = StringEnd(text, i);
                    paint(i, stop - i, IsKeyColon(text, stop) ? Key : String);
                    i = stop;
                    lineStart = false;
                    continue;
                }

                if (char.IsDigit(c) && !(i > 0 && IsWord(text[i - 1])))
                {
                    var stop = i + 1;
                    while (stop < n && (IsWord(text[stop]) || (text[stop] == '.' && stop + 1 < n && char.IsDigit(text[stop + 1])))) stop++;
                    paint(i, stop - i, Number);
                    i = stop;
                    lineStart = false;
                    continue;
                }

                if (IsWordStart(c))
                {
                    var stop = i + 1;
                    while (stop < n && (IsWord(text[stop]) || text[stop] == '-' && stop + 1 < n && IsWord(text[stop + 1]))) stop++;

                    if (lineStart && (IsKeyColon(text, stop) || IsKeyEquals(text, stop)))
                        paint(i, stop - i, Key);
                    else if (i > 0 && (text[i - 1] == '<' || (text[i - 1] == '/' && i > 1 && text[i - 2] == '<')))
                        paint(i, stop - i, Keyword); // HTML・XML のタグ名
                    else if (!InPath(text, i, stop) && Keywords.Contains(text.Substring(i, stop - i)))
                        paint(i, stop - i, Keyword);

                    i = stop;
                    lineStart = false;
                    continue;
                }

                if (c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}')
                {
                    paint(i, 1, Bracket);
                    i++;
                    lineStart = false;
                    continue;
                }

                if (IsOperator(c))
                {
                    var stop = i + 1;
                    while (stop < n && IsOperator(text[stop]) && !IsLineComment(text, stop, false) && !Starts(text, stop, "/*")) stop++;
                    paint(i, stop - i, Operator);

                    // YAML の「- 名前:」は、- のあとも行頭として扱う。
                    lineStart = lineStart && stop - i == 1 && c == '-' && stop < n && text[stop] == ' ';
                    i = stop;
                    continue;
                }

                // 日本語などの字はそのまま。
                i++;
                lineStart = false;
            }
        }

        /// <summary>行末までのコメントか。# は行頭か空白のあと（URL の #x や色の #fff を避ける）、// は : のあとでない所（https:// を避ける）。</summary>
        private static bool IsLineComment(string text, int i, bool lineStart)
        {
            var c = text[i];
            var afterSpace = i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t';

            if (c == '#') return afterSpace && !(i + 1 < text.Length && text[i + 1] == '{');
            if (c == '/') return Starts(text, i, "//") && !(i > 0 && text[i - 1] == ':');
            if (c == '-') return lineStart && Starts(text, i, "-- ");
            return false;
        }

        /// <summary>パスやメンバーの一部か（/var/lib の var、x.end の end を語にしない）。</summary>
        private static bool InPath(string text, int start, int stop)
        {
            if (start > 0 && (text[start - 1] == '/' || text[start - 1] == '.' || text[start - 1] == '\\')) return true;
            return stop < text.Length && (text[stop] == '/' || text[stop] == '\\');
        }

        private static int StringEnd(string text, int i)
        {
            var quote = text[i];
            var stop = i + 1;

            while (stop < text.Length)
            {
                if (text[stop] == '\\' && quote != '`') stop += 2;
                else if (text[stop++] == quote) return stop;
            }

            return text.Length;
        }

        /// <summary>直後が「:」で、その次が空白か行末（YAML・JSON の名前）。a::b や http: の URL は除く。</summary>
        private static bool IsKeyColon(string text, int stop)
        {
            if (stop >= text.Length || text[stop] != ':') return false;
            return stop + 1 == text.Length || text[stop + 1] == ' ' || text[stop + 1] == '\t';
        }

        /// <summary>直後が「=」（空白を挟んでもよい）で、== ではない（.env・ini の名前）。</summary>
        private static bool IsKeyEquals(string text, int stop)
        {
            while (stop < text.Length && text[stop] == ' ') stop++;
            return stop < text.Length && text[stop] == '=' && !(stop + 1 < text.Length && text[stop + 1] == '=');
        }

        private static bool Starts(string text, int i, string value)
        {
            return string.CompareOrdinal(text, i, value, 0, value.Length) == 0;
        }

        private static bool IsWordStart(char c)
        {
            return c < 0x80 && (char.IsLetter(c) || c == '_' || c == '$' || c == '@');
        }

        private static bool IsWord(char c)
        {
            return c < 0x80 && (char.IsLetterOrDigit(c) || c == '_' || c == '$');
        }

        private static bool IsOperator(char c)
        {
            return "=+-*/%<>!&|^~?:;,.\\".IndexOf(c) >= 0;
        }

        private static Brush Freeze(string argb)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
            brush.Freeze();
            return brush;
        }
    }
}
