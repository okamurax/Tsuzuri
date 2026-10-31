using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TableEditor
{
    /// <summary>
    /// 表エディタで編集している表の中身（SPEC 8.4.2）。セルの文字・列の寄せ・列の幅を持つ。
    ///
    /// セルの文字は、受け取ったままの文字列で持つ。数値として読み直さないので、1,000 や 001 も見た目が変わらない。
    /// 行と列は、字のある範囲に打ち足せる空きの1行・1列を足した分を持つ（TableGrid.Normalize が合わせる）。
    /// 書き戻すときは、字のある一番下の行・一番右の列までにする。
    /// </summary>
    internal sealed class TableData
    {
        /// <summary>列の幅の既定（ピクセル）。読み込んだあと、中身に合わせて広げる（TableGrid.FitColumns）。</summary>
        public const double DefaultWidth = 64;

        private readonly List<List<string>> _rows = new List<List<string>>();

        // 列ごとの寄せ。N: 指定なし、L: 左、C: 中央、R: 右（受け渡しの形と同じ記号）。
        private readonly List<char> _aligns = new List<char>();
        private readonly List<double> _widths = new List<double>();

        public int RowCount
        {
            get { return _rows.Count; }
        }

        public int ColumnCount
        {
            get { return _aligns.Count; }
        }

        public string Get(int row, int column)
        {
            if (row < 0 || row >= RowCount || column < 0 || column >= ColumnCount) return string.Empty;
            return _rows[row][column];
        }

        public void Set(int row, int column, string value)
        {
            EnsureSize(row + 1, column + 1);
            _rows[row][column] = value ?? string.Empty;
        }

        public char AlignOf(int column)
        {
            return column >= 0 && column < ColumnCount ? _aligns[column] : 'N';
        }

        public void SetAlign(int column, char align)
        {
            EnsureSize(RowCount, column + 1);
            _aligns[column] = align;
        }

        public double WidthOf(int column)
        {
            return column >= 0 && column < ColumnCount ? _widths[column] : DefaultWidth;
        }

        public void SetWidth(int column, double width)
        {
            EnsureSize(RowCount, column + 1);
            _widths[column] = width;
        }

        /// <summary>少なくとも rows 行・columns 列にする（減らさない）。</summary>
        public void EnsureSize(int rows, int columns)
        {
            while (_aligns.Count < columns)
            {
                _aligns.Add('N');
                _widths.Add(DefaultWidth);
                foreach (var row in _rows) row.Add(string.Empty);
            }

            while (_rows.Count < rows) _rows.Add(NewRow());
        }

        /// <summary>rows 行・columns 列より後ろを捨てる（字の無い空きを縮めるとき）。1行・1列は残す。</summary>
        public void TrimTo(int rows, int columns)
        {
            rows = Math.Max(1, rows);
            columns = Math.Max(1, columns);

            if (_rows.Count > rows) _rows.RemoveRange(rows, _rows.Count - rows);

            if (_aligns.Count > columns)
            {
                var count = _aligns.Count - columns;
                _aligns.RemoveRange(columns, count);
                _widths.RemoveRange(columns, count);
                foreach (var row in _rows) row.RemoveRange(columns, count);
            }
        }

        private List<string> NewRow()
        {
            var row = new List<string>(ColumnCount);
            for (var c = 0; c < ColumnCount; c++) row.Add(string.Empty);
            return row;
        }

        /// <summary>字のある一番下の行と一番右の列（0 始まり）。字が無ければ -1。</summary>
        public void UsedRange(out int lastRow, out int lastColumn)
        {
            lastRow = -1;
            lastColumn = -1;

            for (var r = 0; r < RowCount; r++)
            {
                for (var c = 0; c < ColumnCount; c++)
                {
                    if (IsBlank(_rows[r][c])) continue;
                    lastRow = Math.Max(lastRow, r);
                    lastColumn = Math.Max(lastColumn, c);
                }
            }
        }

        /// <summary>
        /// 書き戻すと空になるか（CellText が空か）。空白とタブだけなら空。改行は &lt;br&gt; になるので空ではない。
        /// 動くたびに呼ぶので、文字列を作らずに調べる。
        /// </summary>
        private static bool IsBlank(string value)
        {
            foreach (var ch in value)
            {
                if (ch == '\r' || ch == '\n' || !char.IsWhiteSpace(ch)) return false;
            }

            return true;
        }

        public TableData Clone()
        {
            var copy = new TableData();
            foreach (var row in _rows) copy._rows.Add(new List<string>(row));
            copy._aligns.AddRange(_aligns);
            copy._widths.AddRange(_widths);
            return copy;
        }

        /// <summary>元に戻すときに、中身をまるごと入れ替える。</summary>
        public void CopyFrom(TableData other)
        {
            _rows.Clear();
            foreach (var row in other._rows) _rows.Add(new List<string>(row));
            _aligns.Clear();
            _aligns.AddRange(other._aligns);
            _widths.Clear();
            _widths.AddRange(other._widths);
        }

        // ---------------------------------------------------------------
        // 本体との受け渡し（Tsuzuri の TableModel.ToExchange と同じ形）
        //
        //   #align	N	C	R      ← N: 指定なし、L: 左、C: 中央、R: 右
        //   名前	担当	金額      ← 1行目が見出し行。セルはタブ区切り
        // ---------------------------------------------------------------

        /// <summary>受け渡しの形から読む。打ち足せる空きは持たない（TableGrid.Normalize が足す）。</summary>
        public static TableData Parse(string exchange)
        {
            var aligns = new List<char>();
            var rows = new List<string[]>();

            foreach (var line in (exchange ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("#align", StringComparison.Ordinal))
                {
                    var parts = line.Split('\t');
                    for (var i = 1; i < parts.Length; i++) aligns.Add(AlignCode(parts[i]));
                    continue;
                }

                rows.Add(line.Split('\t'));
            }

            // 最後の空の行（受け渡しの末尾の改行）は数えない。
            // 先頭の空の行は捨てない（2026-10-27）。1列の表で見出し行が空だと、受け渡しの2行目が空になる。
            // 前は捨てていて、次の行が見出し行にずれた。本体の TableModel.ReplaceWith と同じ扱い。
            while (rows.Count > 0 && rows[rows.Count - 1].Length == 1 && rows[rows.Count - 1][0].Length == 0)
                rows.RemoveAt(rows.Count - 1);

            var columns = Math.Max(aligns.Count, 1);
            foreach (var row in rows) columns = Math.Max(columns, row.Length);

            var data = new TableData();
            data.EnsureSize(Math.Max(rows.Count, 1), columns);

            for (var r = 0; r < rows.Count; r++)
            {
                for (var c = 0; c < rows[r].Length; c++) data._rows[r][c] = rows[r][c];
            }

            for (var c = 0; c < aligns.Count; c++) data._aligns[c] = aligns[c];

            return data;
        }

        /// <summary>受け渡しの形にする。字のある一番下の行・一番右の列まで。何も無ければ空のセル1つ。</summary>
        public string ToExchange()
        {
            int lastRow, lastColumn;
            UsedRange(out lastRow, out lastColumn);
            lastRow = Math.Max(lastRow, 0);
            lastColumn = Math.Max(lastColumn, 0);

            var builder = new StringBuilder();
            builder.Append("#align");
            for (var c = 0; c <= lastColumn; c++) builder.Append('\t').Append(AlignOf(c));
            builder.Append('\n');

            for (var r = 0; r <= lastRow; r++)
            {
                for (var c = 0; c <= lastColumn; c++)
                {
                    if (c > 0) builder.Append('\t');
                    builder.Append(CellText(r, c));
                }
                builder.Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// 書き戻すセルの文字。タブは空白に、セルの中の改行は &lt;br&gt; にする
        /// （受け渡しの区切りと、Markdown の表の1行が1行の決まりを崩さないように）。
        /// </summary>
        private string CellText(int row, int column)
        {
            return Get(row, column).Replace("\t", " ").Replace("\r\n", "<br>").Replace("\r", "<br>").Replace("\n", "<br>").Trim();
        }

        private static char AlignCode(string code)
        {
            switch (code)
            {
                case "L": return 'L';
                case "C": return 'C';
                case "R": return 'R';
                default: return 'N';
            }
        }
    }

    /// <summary>
    /// クリップボードの表の形（タブ区切り。Excel と同じ）。セルにタブ・改行・" があれば " で囲み、中の " は "" にする。
    /// </summary>
    internal static class Tsv
    {
        public static string Write(List<List<string>> rows)
        {
            var builder = new StringBuilder();

            foreach (var row in rows)
            {
                for (var c = 0; c < row.Count; c++)
                {
                    if (c > 0) builder.Append('\t');
                    builder.Append(Quote(row[c]));
                }
                builder.Append("\r\n");
            }

            return builder.ToString();
        }

        private static string Quote(string value)
        {
            if (value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>タブ区切りの文字を、行とセルに分ける。末尾の改行のあとの空の行は数えない。</summary>
        public static List<List<string>> Read(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            text = text ?? string.Empty;

            var i = 0;
            var atCellStart = true;

            while (i < text.Length)
            {
                var ch = text[i];

                // " で始まるセルは、閉じる " まで（中の "" は " 1つ）。改行やタブも中身。
                if (atCellStart && ch == '"')
                {
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                cell.Append('"');
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        cell.Append(text[i]);
                        i++;
                    }

                    atCellStart = false;
                    continue;
                }

                atCellStart = false;

                if (ch == '\t')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    atCellStart = true;
                    i++;
                    continue;
                }

                if (ch == '\r' || ch == '\n')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    atCellStart = true;

                    i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                    continue;
                }

                cell.Append(ch);
                i++;
            }

            // 末尾に改行が無い最後の行。
            if (!atCellStart || cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row);
            }

            return rows;
        }
    }

    /// <summary>
    /// オートフィル（選んだ範囲の右下の ■ をドラッグして埋める）の続け方（SPEC 8.4.2）。Excel を簡単にしたもの。
    /// ・数値が2つ以上: 最初と最後の差から1つ分の増え方を出して続ける（1、2 → 3、4、5）
    /// ・末尾が数字の文字（項目1 など。頭の文字がすべて同じ）: 数字を続ける。1つだけなら 1 ずつ増やす（項目1 → 項目2）
    /// ・それ以外（数値が1つだけのときも）: 並びをくり返す
    /// </summary>
    internal static class FillSeries
    {
        private static readonly Regex TrailingNumber = new Regex(@"^(.*?)(\d+)$");

        /// <summary>
        /// source の続き count 個。backward なら source の前へさかのぼる（返すのは近いものから）。
        /// </summary>
        public static List<string> Extend(IList<string> source, int count, bool backward)
        {
            var result = new List<string>(count);
            var n = source.Count;
            if (n == 0) return result;

            decimal[] numbers;
            if (n >= 2 && AllNumbers(source, out numbers))
            {
                var step = (numbers[n - 1] - numbers[0]) / (n - 1);
                var scale = MaxScale(source);

                for (var k = 1; k <= count; k++)
                {
                    var value = backward ? numbers[0] - step * k : numbers[n - 1] + step * k;
                    result.Add(Format(value, scale));
                }

                return result;
            }

            string prefix;
            long[] tails;
            int digits;
            if (!AllNumbers(source, out numbers) && SamePrefix(source, out prefix, out tails, out digits))
            {
                long step = 1;
                var even = true;
                if (n >= 2)
                {
                    var span = tails[n - 1] - tails[0];
                    even = span % (n - 1) == 0;
                    step = span / (n - 1);
                }

                if (even)
                {
                    for (var k = 1; k <= count; k++)
                    {
                        var value = backward ? tails[0] - step * k : tails[n - 1] + step * k;
                        if (value < 0) value = 0;
                        result.Add(prefix + value.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'));
                    }

                    return result;
                }
            }

            // くり返す。さかのぼるときは、source の最後から前へ。
            for (var k = 1; k <= count; k++)
            {
                result.Add(backward ? source[n - 1 - (k - 1) % n] : source[(k - 1) % n]);
            }

            return result;
        }

        private static bool AllNumbers(IList<string> source, out decimal[] numbers)
        {
            numbers = new decimal[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                if (!decimal.TryParse(source[i], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out numbers[i])) return false;
            }

            return true;
        }

        private static bool SamePrefix(IList<string> source, out string prefix, out long[] tails, out int digits)
        {
            prefix = null;
            tails = new long[source.Count];
            digits = 1;

            for (var i = 0; i < source.Count; i++)
            {
                var match = TrailingNumber.Match(source[i]);
                if (!match.Success || match.Groups[2].Value.Length > 15) return false;

                var head = match.Groups[1].Value;
                if (prefix == null) prefix = head;
                else if (prefix != head) return false;

                tails[i] = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

                // 0 埋め（001 など）は桁を保つ。
                if (match.Groups[2].Value.StartsWith("0", StringComparison.Ordinal))
                    digits = Math.Max(digits, match.Groups[2].Value.Length);
            }

            return true;
        }

        /// <summary>source の数値の小数点以下の桁の最大。</summary>
        private static int MaxScale(IList<string> source)
        {
            var scale = 0;
            foreach (var text in source)
            {
                var dot = text.IndexOf('.');
                if (dot >= 0) scale = Math.Max(scale, text.Length - dot - 1);
            }

            return scale;
        }

        /// <summary>
        /// 小数点以下は source の桁にそろえる。割り切れない増え方（1、2、4 → 1.5 ずつ）のときは、要るだけ桁を足す（10 桁まで）。
        /// </summary>
        private static string Format(decimal value, int scale)
        {
            var rounded = Math.Round(value, 10);
            var text = rounded.ToString("F10", CultureInfo.InvariantCulture).TrimEnd('0');
            if (text.EndsWith(".", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);

            var dot = text.IndexOf('.');
            var have = dot < 0 ? 0 : text.Length - dot - 1;
            if (have < scale) text = (dot < 0 ? text + "." : text) + new string('0', scale - have);

            return text == "-0" ? "0" : text;
        }
    }
}
