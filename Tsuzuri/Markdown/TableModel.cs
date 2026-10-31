using System;
using System.Collections.Generic;
using System.Text;

namespace Tsuzuri.Markdown
{
    /// <summary>列の寄せ。None は区切り行が --- のもの（寄せの指定なし。表示は左）。</summary>
    public enum TableAlign
    {
        None,
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// Markdown の表の中身（SPEC 8.4.2。2026-10-16）。表エディタ（TableEditor）との受け渡しのために、セルの並びとして持つ。
    ///
    /// 行 0 が見出し行、1 からが中身の行（区切り行は持たず、寄せだけ Aligns に持つ）。
    /// セルの文字は、`\|` を `|` に戻したもの。書き戻すときにまた `\|` にする。
    /// 書き戻すときは `|` の位置を揃える（全角の文字は2文字分の幅として数える）。
    /// </summary>
    public sealed class TableModel
    {
        public readonly List<List<string>> Rows = new List<List<string>>();
        public readonly List<TableAlign> Aligns = new List<TableAlign>();

        /// <summary>表の行の頭の空白。書き戻すときにも付ける。</summary>
        private string _indent = string.Empty;

        public int RowCount
        {
            get { return Rows.Count; }
        }

        public int ColumnCount
        {
            get { return Aligns.Count; }
        }

        /// <summary>表の行（見出し行・区切り行・中身の行）から読む。表として読めなければ null。</summary>
        public static TableModel Parse(IList<string> lines)
        {
            if (lines == null || lines.Count < 2) return null;

            var header = SplitRow(lines[0]);
            var delimiter = SplitRow(lines[1]);
            if (header.Count == 0 || delimiter.Count == 0) return null;

            var model = new TableModel();
            model._indent = LeadingSpaces(lines[0]);

            var columns = header.Count;
            for (var c = 0; c < columns; c++)
            {
                var align = TableAlign.None;
                if (c < delimiter.Count && !TryParseAlign(delimiter[c], out align)) return null;
                model.Aligns.Add(align);
            }

            model.Rows.Add(header);
            for (var i = 2; i < lines.Count; i++) model.Rows.Add(SplitRow(lines[i]));

            // 見出し行より多いセルは、表示でも読まれない（GFM の決まり）が、文字としては本文にある。
            // 揃えると捨てることになるので、揃える前に数えておき、表エディタを開く前に知らせる（2026-10-27。前は黙って捨てていた）。
            foreach (var row in model.Rows)
            {
                if (row.Count > columns) model.ExtraCellRows++;
            }

            model.Normalize();
            return model;
        }

        /// <summary>
        /// 読んだときに、見出し行より多いセルがあった行の数（2026-10-27）。表エディタに渡すとその分は消えるので、
        /// 0 でなければ開く前に知らせる（MarkdownView.EditTable）。
        /// </summary>
        public int ExtraCellRows { get; private set; }

        /// <summary>行ごとのセルの数を、列の数に揃える（足りなければ空のセル、多ければ捨てる）。</summary>
        private void Normalize()
        {
            foreach (var row in Rows)
            {
                while (row.Count < ColumnCount) row.Add(string.Empty);
                if (row.Count > ColumnCount) row.RemoveRange(ColumnCount, row.Count - ColumnCount);
            }
        }

        public string Get(int row, int column)
        {
            return row >= 0 && row < RowCount && column >= 0 && column < ColumnCount ? Rows[row][column] : string.Empty;
        }

        // ---------------------------------------------------------------
        // 表エディタとの受け渡し（TableEditor の TableData と同じ形）
        //
        //   #align	N	C	R      ← N: 指定なし、L: 左、C: 中央、R: 右
        //   名前	担当	金額      ← 1行目が見出し行。セルはタブ区切り
        // ---------------------------------------------------------------

        public string ToExchange()
        {
            var builder = new StringBuilder();
            builder.Append("#align");
            foreach (var align in Aligns) builder.Append('\t').Append(CodeOf(align));
            builder.Append('\n');

            foreach (var row in Rows)
            {
                for (var c = 0; c < row.Count; c++)
                {
                    if (c > 0) builder.Append('\t');
                    builder.Append(row[c].Replace('\t', ' '));
                }
                builder.Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>表エディタから戻ってきた中身に入れ替える。行の頭の空白（字下げ）は元のまま。読めなければ false。</summary>
        public bool ReplaceWith(string exchange)
        {
            var aligns = new List<TableAlign>();
            var rows = new List<List<string>>();

            foreach (var line in (exchange ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("#align", StringComparison.Ordinal))
                {
                    var parts = line.Split('\t');
                    for (var i = 1; i < parts.Length; i++) aligns.Add(AlignOf(parts[i]));
                    continue;
                }

                rows.Add(new List<string>(line.Split('\t')));
            }

            // 空の行は、末尾（受け渡しの最後の改行）のものだけを捨てる。途中の空の行は、1列の表の空のセルの行なので残す
            // （前は空の行をすべて捨てていて、1列の表の空の行が消え、見出し行が空だと次の行が見出しにずれた）。
            while (rows.Count > 0 && rows[rows.Count - 1].Count == 1 && rows[rows.Count - 1][0].Length == 0)
                rows.RemoveAt(rows.Count - 1);

            if (rows.Count == 0) return false;

            var columns = aligns.Count;
            foreach (var row in rows) columns = Math.Max(columns, row.Count);
            if (columns == 0) return false;
            while (aligns.Count < columns) aligns.Add(TableAlign.None);

            Rows.Clear();
            Aligns.Clear();
            Aligns.AddRange(aligns);
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (var cell in row) cells.Add(Clean(cell));
                Rows.Add(cells);
            }

            Normalize();
            return true;
        }

        private static string CodeOf(TableAlign align)
        {
            switch (align)
            {
                case TableAlign.Left: return "L";
                case TableAlign.Center: return "C";
                case TableAlign.Right: return "R";
                default: return "N";
            }
        }

        private static TableAlign AlignOf(string code)
        {
            switch (code)
            {
                case "L": return TableAlign.Left;
                case "C": return TableAlign.Center;
                case "R": return TableAlign.Right;
                default: return TableAlign.None;
            }
        }

        private static string Clean(string text)
        {
            return (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ---------------------------------------------------------------
        // 書き戻す
        // ---------------------------------------------------------------

        /// <summary>Markdown の表の文字にする。`|` の位置を揃える。最後の行の後ろに改行は付けない。</summary>
        public string Serialize(string newLine)
        {
            var widths = new int[ColumnCount];
            for (var c = 0; c < ColumnCount; c++)
            {
                widths[c] = 3;   // 区切り行の --- の分
                foreach (var row in Rows) widths[c] = Math.Max(widths[c], DisplayWidth(Escape(row[c])));
            }

            var builder = new StringBuilder();
            for (var r = 0; r < RowCount; r++)
            {
                if (r > 0) builder.Append(newLine);
                AppendRow(builder, Rows[r], widths);

                if (r == 0)
                {
                    builder.Append(newLine);
                    AppendDelimiter(builder, widths);
                }
            }

            return builder.ToString();
        }

        private void AppendRow(StringBuilder builder, List<string> row, int[] widths)
        {
            builder.Append(_indent).Append('|');
            for (var c = 0; c < ColumnCount; c++)
            {
                var text = Escape(row[c]);
                var pad = widths[c] - DisplayWidth(text);

                var left = Aligns[c] == TableAlign.Right ? pad : Aligns[c] == TableAlign.Center ? pad / 2 : 0;
                builder.Append(' ').Append(' ', left).Append(text).Append(' ', pad - left).Append(" |");
            }
        }

        private void AppendDelimiter(StringBuilder builder, int[] widths)
        {
            builder.Append(_indent).Append('|');
            for (var c = 0; c < ColumnCount; c++)
            {
                var width = widths[c];
                string cell;
                switch (Aligns[c])
                {
                    case TableAlign.Left: cell = ":" + new string('-', width - 1); break;
                    case TableAlign.Center: cell = ":" + new string('-', width - 2) + ":"; break;
                    case TableAlign.Right: cell = new string('-', width - 1) + ":"; break;
                    default: cell = new string('-', width); break;
                }
                builder.Append(' ').Append(cell).Append(" |");
            }
        }

        // ---------------------------------------------------------------
        // 小物
        // ---------------------------------------------------------------

        /// <summary>
        /// 1行をセルに分ける。両端の `|` は外し、`\|` は区切りにしない（セルの中の `|` に戻す）。
        /// </summary>
        private static List<string> SplitRow(string line)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.StartsWith("|", StringComparison.Ordinal)) text = text.Substring(1);
            if (text.EndsWith("|", StringComparison.Ordinal) && !IsEscaped(text, text.Length - 1))
                text = text.Substring(0, text.Length - 1);

            var cells = new List<string>();
            var cell = new StringBuilder();

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (ch == '\\' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    cell.Append('|');
                    i++;
                    continue;
                }

                if (ch == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    continue;
                }

                cell.Append(ch);
            }

            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private static bool IsEscaped(string text, int index)
        {
            var slashes = 0;
            for (var i = index - 1; i >= 0 && text[i] == '\\'; i--) slashes++;
            return slashes % 2 == 1;
        }

        private static bool TryParseAlign(string cell, out TableAlign align)
        {
            align = TableAlign.None;

            var text = cell.Trim();
            if (text.Length == 0) return false;

            var left = text.StartsWith(":", StringComparison.Ordinal);
            var right = text.EndsWith(":", StringComparison.Ordinal);
            var dashes = text.Trim(':');
            if (dashes.Length == 0 || dashes.Trim('-').Length != 0) return false;

            align = left && right ? TableAlign.Center : right ? TableAlign.Right : left ? TableAlign.Left : TableAlign.None;
            return true;
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty).Replace("|", "\\|");
        }

        private static string LeadingSpaces(string line)
        {
            var count = 0;
            while (count < line.Length && count < 3 && line[count] == ' ') count++;
            return new string(' ', count);
        }

        /// <summary>見た目の幅。全角の文字は2、半角は1（`|` を揃えるため）。</summary>
        private static int DisplayWidth(string text)
        {
            var width = 0;
            foreach (var ch in text)
            {
                if (char.IsLowSurrogate(ch)) continue;   // サロゲートペアは上位で2と数えた
                width += IsWide(ch) ? 2 : 1;
            }
            return width;
        }

        private static bool IsWide(char ch)
        {
            if (char.IsHighSurrogate(ch)) return true;
            if (ch >= 0x1100 && ch <= 0x115F) return true;   // ハングルの字母
            if (ch >= 0x2E80 && ch <= 0xA4CF) return true;   // CJK・かな・記号
            if (ch >= 0xAC00 && ch <= 0xD7A3) return true;   // ハングル
            if (ch >= 0xF900 && ch <= 0xFAFF) return true;   // CJK 互換漢字
            if (ch >= 0xFE30 && ch <= 0xFE4F) return true;
            if (ch >= 0xFF00 && ch <= 0xFF60) return true;   // 全角英数・記号
            if (ch >= 0xFFE0 && ch <= 0xFFE6) return true;
            return false;
        }
    }
}
