using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tsuzuri.Office
{
    /// <summary>
    /// 開いている Excel（Windows のデスクトップ版）を読み書きする（2026-11-24。利用者の考え。SPEC 19章）。
    /// 書くのは、利用者がランチャーの Excel の一覧で選んだシートだけ（呼ぶ側の MainWindow.Excel.cs が決める）。保存はしない。
    /// 書く前の中身を控えて返し、戻せるようにする（外から書いた分は Excel の Ctrl+Z では戻らないため。書く途中で失敗したときも控えを返す）。
    ///
    /// Office のアプリとのやり取りは、この Office フォルダの中だけで行う（COM を使うのはここだけ）。
    /// いつか COM が使えなくなっても、ここを差し替えれば、AI に渡す今の状態の形はそのまま残せるように。
    /// 型の参照（Interop）は使わず、名前で呼ぶ（Excel の版に縛られない）。
    /// 呼ぶたびに自分の STA のスレッドで読み、時間を切る（Excel が応答しない・セルを編集中でも、こちらを止めない）。
    /// 受け取った COM の参照はその場で全部手放す（持ち続けると、利用者が Excel を閉じても EXCEL.EXE が残る）。
    /// </summary>
    public static class ExcelBridge
    {
        /// <summary>excel.ps1 で読むときの上限（セルの数）。</summary>
        public const int DumpCells = 20000;

        /// <summary>excel.ps1 で読む列の上限。</summary>
        public const int DumpColumns = 200;

        /// <summary>1つのセルの字の上限。</summary>
        private const int CellChars = 300;

        /// <summary>セルの中身の表。行ごとに、列ごとの文字（式のセルは値のあとに「（式 =…）」）。</summary>
        public sealed class Grid
        {
            public string Address;
            public int FirstRow;
            public int FirstColumn;
            public int Rows;
            public int Columns;
            /// <summary>読んだ行（Rows より少なければ、多いので先頭だけ）。</summary>
            public List<string[]> Cells = new List<string[]>();

            /// <summary>読んだ列の数（Columns より少なければ、多いので左だけ）。</summary>
            public int ColumnsRead;

            public bool Truncated
            {
                get { return Cells.Count < Rows; }
            }

            public bool ColumnsTruncated
            {
                get { return ColumnsRead < Columns; }
            }

            /// <summary>タブ区切りの表。1行目は列の名前（A・B…）、各行の頭は行の番号。</summary>
            public string ToTsv(string indent)
            {
                var text = new StringBuilder();
                text.Append(indent).Append("行＼列");
                for (var c = 0; c < ColumnsRead; c++) text.Append('\t').Append(ColumnName(FirstColumn + c));
                text.Append('\n');
                for (var r = 0; r < Cells.Count; r++)
                {
                    text.Append(indent).Append(FirstRow + r);
                    foreach (var cell in Cells[r]) text.Append('\t').Append(cell);
                    text.Append('\n');
                }
                return text.ToString();
            }
        }

        /// <summary>
        /// 指定した範囲を読む（excel.ps1 から）。book はブックの名前かフルパス、sheet はシートの名前（どちらも要る。どこが前に出ているかは使わない）、
        /// range は A1 の形の範囲（空なら使っている範囲全体）。読めなければ problem にわけを入れて null。
        /// </summary>
        public static Grid ReadRange(string book, string sheet, string range, int timeout, Func<string, bool> guarded, out string bookShown, out string sheetShown, out string problem)
        {
            Grid grid = null;
            string shownBook = null, shownSheet = null, why = null;
            var done = RunSta(() => grid = ReadRangeCore(book, sheet, range, guarded, out shownBook, out shownSheet, out why), timeout);
            bookShown = shownBook;
            sheetShown = shownSheet;
            problem = done ? why : "Excel が応答しない（セルを編集中か、ダイアログを出しているか、重い計算の途中）";
            return done ? grid : null;
        }

        /// <summary>書いた範囲と、戻すための控え。</summary>
        public sealed class Written
        {
            public string Book;
            public string Sheet;
            public string Address;
            public int Rows;
            public int Columns;
            /// <summary>書く前の中身（Formula2 の形。1つのセルなら object、ほかは object[,]）。</summary>
            public object Before;
            /// <summary>書いた直後の中身（戻す前に、利用者がそのあと手で直していないかを見るため）。読めなければ null。</summary>
            public object After;
            /// <summary>書く途中で失敗した（途中まで書けているかもしれない）。</summary>
            public bool Failed;
        }

        /// <summary>
        /// 選んだシートに書く。at は書き始めの左上のセル（B3）か、中身と同じ大きさの範囲（B3:D5）。cells は行ごと・列ごとの文字で、
        /// セルに手で打ったときと同じに入る（「=」で始まれば式。式は英語の関数名と「,」の区切り）。書けなければ problem にわけを入れて null。
        /// 書く途中で失敗したときは、problem にわけを入れ、控え（Failed）を返す（途中まで書けているかもしれないので、戻せるように）。
        /// 時間切れのときは null（あとで書き終えても控えは残らない）。
        /// </summary>
        public static Written Write(string book, string sheet, string at, List<string[]> cells, int timeout, out string problem)
        {
            Written written = null;
            string why = null;
            var done = RunSta(() => written = WriteCore(book, sheet, at, cells, out why), timeout);
            problem = done ? why : "Excel が応答しない（セルを編集中か、ダイアログを出しているか、重い計算の途中）。書けたかどうか分からないので、Excel で確かめる";
            return done ? written : null;
        }

        /// <summary>書いた分を、書く前の中身に戻す。戻せなければわけを返す。force でなければ、書いたあとに手で直したセルがあると断る（"changed" で始まるわけ）。</summary>
        public static string Undo(Written written, bool force, int timeout)
        {
            string problem = null;
            var done = RunSta(() => problem = UndoCore(written, force), timeout);
            return done ? problem : "Excel が応答しない（セルを編集中か、ダイアログを出しているか、重い計算の途中）";
        }

        private static string UndoCore(Written written, bool force)
        {
            using (var scope = new Scope())
            {
                var app = ActiveExcel(scope);
                if (app == null) return "Excel が動いていない";
                try
                {
                    string problem;
                    var sheet = FindSheet(scope, app, written.Book, written.Sheet, out problem);
                    if (sheet == null) return problem;
                    var target = scope.Get(sheet, "Range", written.Address);
                    if (!force && (written.After == null || !Same(Snapshot(scope, target), written.After)))
                        return "changed\t書いたあとに、その範囲のセルが変わっている（" + written.Address + "）";
                    SetFormula(target, written.Before);
                    return null;
                }
                catch (Exception ex)
                {
                    return IsBusy(ex) ? Busy(ex) : "戻せなかった: " + (ex.InnerException ?? ex).Message;
                }
            }
        }

        /// <summary>
        /// セルに手で打ったときと同じに入れる。Formula2 は、今の Excel で FILTER・UNIQUE などの式がそのまま広がる（Formula だと「@」が付いて1つの値になる）。
        /// Formula2 の無い古い Excel だけ Formula に入れる。
        /// </summary>
        private static void SetFormula(object target, object value)
        {
            try
            {
                target.GetType().InvokeMember("Formula2", BindingFlags.SetProperty, null, target, new[] { value }, Automation);
            }
            catch (Exception ex) when (UnknownName(ex))
            {
                target.GetType().InvokeMember("Formula", BindingFlags.SetProperty, null, target, new[] { value }, Automation);
            }
        }

        /// <summary>手で打ったときの形で読む（Formula2。無ければ Formula）。</summary>
        private static object GetFormula(Scope scope, object target)
        {
            try
            {
                return scope.Get(target, "Formula2");
            }
            catch (Exception ex) when (UnknownName(ex))
            {
                return scope.Get(target, "Formula");
            }
        }

        /// <summary>
        /// 戻すための控え: 手で打ったときの形（Formula2）で、文字のセルには頭に ' を付ける。
        /// ' を付けて入れた文字（'001 など）は Formula2 で読むと ' が落ち、そのまま入れ直すと数・日付・式に化けるため。
        /// 1つのセルなら string、ほかは object[,]（COM から来たままの 1 始まり）。
        /// </summary>
        private static object Snapshot(Scope scope, object target)
        {
            var formulas = GetFormula(scope, target);
            var values = scope.Get(target, "Value2");
            var array = formulas as object[,];
            if (array == null) return Texted(formulas, values);

            var copy = (object[,])array.Clone();
            var valueArray = values as object[,];
            for (var r = copy.GetLowerBound(0); r <= copy.GetUpperBound(0); r++)
                for (var c = copy.GetLowerBound(1); c <= copy.GetUpperBound(1); c++)
                    copy[r, c] = Texted(copy[r, c], valueArray != null ? valueArray[r, c] : null);
            return copy;
        }

        private static object Texted(object formula, object value)
        {
            var text = formula as string;
            if (string.IsNullOrEmpty(text) || !(value is string)) return formula;
            // 「=」で始まっても、値がその文字のままなら式ではなく文字（' を付けて入れた =abc）
            if (text.StartsWith("=", StringComparison.Ordinal) && !string.Equals(text, (string)value, StringComparison.Ordinal)) return formula;
            return "'" + text;
        }

        private static bool Same(object a, object b)
        {
            var x = a as object[,];
            var y = b as object[,];
            if (x == null || y == null)
                return x == null && y == null && string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (x.GetLength(0) != y.GetLength(0) || x.GetLength(1) != y.GetLength(1)) return false;
            for (var r = 0; r < x.GetLength(0); r++)
                for (var c = 0; c < x.GetLength(1); c++)
                    if (!string.Equals(Convert.ToString(x[x.GetLowerBound(0) + r, x.GetLowerBound(1) + c], CultureInfo.InvariantCulture),
                                       Convert.ToString(y[y.GetLowerBound(0) + r, y.GetLowerBound(1) + c], CultureInfo.InvariantCulture), StringComparison.Ordinal))
                        return false;
            return true;
        }

        private static object FindSheet(Scope scope, object app, string bookName, string sheetName, out string problem)
        {
            problem = null;
            var books = scope.Get(app, "Workbooks");
            var count = Convert.ToInt32(scope.Get(books, "Count"), CultureInfo.InvariantCulture);
            object book = null;
            for (var i = 1; i <= count && book == null; i++)
            {
                var candidate = scope.Get(books, "Item", i);
                if (!Visible(scope, candidate)) continue;   // 隠れたブックには書かない（読むときと同じ）
                if (string.Equals(Text(scope.Get(candidate, "FullName")), bookName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Text(scope.Get(candidate, "Name")), bookName, StringComparison.OrdinalIgnoreCase))
                    book = candidate;
            }
            if (book == null)
            {
                problem = "そのブックは Excel で開いていない: " + bookName;
                return null;
            }
            try
            {
                return scope.Get(scope.Get(book, "Sheets"), "Item", sheetName);
            }
            catch (Exception)
            {
                problem = "そのシートは無い: " + sheetName;
                return null;
            }
        }

        private static Written WriteCore(string bookName, string sheetName, string at, List<string[]> cells, out string problem)
        {
            problem = null;
            var rows = cells.Count;
            var columns = 0;
            foreach (var row in cells) columns = Math.Max(columns, row.Length);
            if (rows == 0 || columns == 0)
            {
                problem = "書く中身が空";
                return null;
            }
            if ((long)rows * columns > DumpCells)
            {
                problem = "一度に書けるのは " + DumpCells + " セルまで。分けて書く";
                return null;
            }

            using (var scope = new Scope())
            {
                var app = ActiveExcel(scope);
                if (app == null)
                {
                    problem = "Excel が動いていない";
                    return null;
                }
                try
                {
                    var sheet = FindSheet(scope, app, bookName, sheetName, out problem);
                    if (sheet == null) return null;

                    object given;
                    try
                    {
                        given = scope.Get(sheet, "Range", at);
                    }
                    catch (Exception ex)
                    {
                        problem = IsBusy(ex) ? Busy(ex) : "書く場所を読めない（B3 か B3:D5 の形で）: " + at;
                        return null;
                    }
                    if (Convert.ToInt32(scope.Get(scope.Get(given, "Areas"), "Count"), CultureInfo.InvariantCulture) > 1)
                    {
                        problem = "書く場所は1つ（B3 か B3:D5 の形で）: " + at;
                        return null;
                    }
                    var givenRows = Count(scope, given, "Rows");
                    var givenColumns = Count(scope, given, "Columns");
                    if ((givenRows != 1 || givenColumns != 1) && (givenRows != rows || givenColumns != columns))
                    {
                        problem = "書く場所（" + givenRows + " 行 × " + givenColumns + " 列）と中身（" + rows + " 行 × " + columns + " 列）の大きさが違う。左上のセルだけを指定するか、大きさを合わせる";
                        return null;
                    }

                    var first = scope.Get(given, "Cells", 1, 1);
                    var target = scope.Get(first, "Resize", rows, columns);
                    if (!string.Equals(Text(scope.Get(scope.Get(target, "Worksheet"), "Name")), sheetName, StringComparison.Ordinal))
                    {
                        problem = "書く場所がほかのシートを指している（B3 の形で、シートの名前を付けない）: " + at;
                        return null;
                    }
                    var address = Address(scope, target);
                    var before = Snapshot(scope, target);   // 戻すための控え

                    var data = new object[rows, columns];
                    for (var r = 0; r < rows; r++)
                        for (var c = 0; c < columns; c++)
                            data[r, c] = c < cells[r].Length ? cells[r][c] : string.Empty;
                    object value = rows == 1 && columns == 1 ? data[0, 0] : (object)data;
                    var written = new Written { Book = bookName, Sheet = sheetName, Address = address, Rows = rows, Columns = columns, Before = before };
                    try
                    {
                        SetFormula(target, value);
                    }
                    catch (Exception ex)
                    {
                        if (IsBusy(ex))
                        {
                            problem = Busy(ex);   // 呼び出しを断られた（何も書いていない）
                            return null;
                        }
                        // 途中まで書けているかもしれないので、控えは返す（戻せるように）
                        written.Failed = true;
                        problem = "書けなかった（式の書き間違い（英語の関数名・「,」の区切り）・シートの保護・結合したセル・配列の式など）: " + (ex.InnerException ?? ex).Message
                                  + "。途中まで書けているかもしれない";
                        return written;
                    }

                    // 書いた直後の中身（戻すときに、手で直していないかを見る）。読めなくても、書けたことは変わらない
                    try
                    {
                        written.After = Snapshot(scope, target);
                    }
                    catch (Exception)
                    {
                    }
                    return written;
                }
                catch (Exception ex)
                {
                    problem = IsBusy(ex) ? Busy(ex) : "書けなかった（式の書き間違い（英語の関数名・「,」の区切り）・シートの保護・結合したセル・配列の式など）: " + (ex.InnerException ?? ex).Message;
                    return null;
                }
            }
        }

        /// <summary>開いているブック（ランチャーの Excel の一覧）。</summary>
        public sealed class Book
        {
            /// <summary>フルパス。まだ保存していなければ null。</summary>
            public string Path;
            public string Name;
            public List<string> Sheets = new List<string>();
        }

        /// <summary>開いているブックとシートの一覧。Excel が動いていなければ空。読めなければ null で、problem にわけ。</summary>
        public static List<Book> ListBooks(int timeout, out string problem)
        {
            List<Book> books = null;
            string why = null;
            var done = RunSta(() => books = ListBooksCore(out why), timeout);
            problem = done ? why : "Excel が応答しない（セルを編集中か、ダイアログを出しているか、重い計算の途中）";
            return done ? books : null;
        }

        private static List<Book> ListBooksCore(out string problem)
        {
            problem = null;
            var list = new List<Book>();
            using (var scope = new Scope())
            {
                var app = ActiveExcel(scope);
                if (app == null) return list;
                try
                {
                    ListBooksInto(scope, app, list);
                }
                catch (Exception ex)
                {
                    problem = Busy(ex);   // 「ブックが無い」と取り違えないように、わけを返す
                    return null;
                }
            }
            return list;
        }

        private static void ListBooksInto(Scope scope, object app, List<Book> list)
        {
            {
                var books = scope.Get(app, "Workbooks");
                var count = Convert.ToInt32(scope.Get(books, "Count"), CultureInfo.InvariantCulture);
                for (var i = 1; i <= count; i++)
                {
                    var book = scope.Get(books, "Item", i);
                    if (!Visible(scope, book)) continue;   // 隠れたブック（PERSONAL.XLSB など）は出さない
                    var full = Text(scope.Get(book, "FullName"));
                    var entry = new Book
                    {
                        Name = Text(scope.Get(book, "Name")),
                        Path = Text(scope.Get(book, "Path")).Length > 0 ? full : null,
                    };
                    var sheets = scope.Get(book, "Sheets");
                    var sheetCount = Convert.ToInt32(scope.Get(sheets, "Count"), CultureInfo.InvariantCulture);
                    for (var j = 1; j <= sheetCount; j++) entry.Sheets.Add(Text(scope.Get(scope.Get(sheets, "Item", j), "Name")));
                    list.Add(entry);
                }
            }
        }

        /// <summary>ブックの窓が見えているか（隠れたブックは扱わない）。分からなければ見えているとする。</summary>
        private static bool Visible(Scope scope, object book)
        {
            try
            {
                var windows = scope.Get(book, "Windows");
                if (Convert.ToInt32(scope.Get(windows, "Count"), CultureInfo.InvariantCulture) == 0) return false;
                return scope.Get(scope.Get(windows, "Item", 1), "Visible") as bool? ?? true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // ---------------------------------------------------------------

        private static bool RunSta(Action work, int timeout)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception)
                {
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(timeout);
        }

        /// <summary>
        /// Excel を呼ぶときの言語（LCID）。英語（1033）にする（2026-11-24）。Excel の自動操作は英語ならどの版でも受け付け、範囲の書き方（A1:B2）も英語のまま通る。
        /// 初めは InvariantCulture（LCID 127）を渡していて、日本語の Excel で一覧が読めなかった。
        /// </summary>
        private static readonly CultureInfo Automation = new CultureInfo("en-US");

        /// <summary>受け取った COM の参照を覚えておき、最後に全部手放す。</summary>
        private sealed class Scope : IDisposable
        {
            private readonly List<object> _objects = new List<object>();

            public object Keep(object value)
            {
                if (value != null && Marshal.IsComObject(value)) _objects.Add(value);
                return value;
            }

            public object Get(object target, string name, params object[] args)
            {
                if (target == null) return null;
                return Keep(target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args, Automation));
            }

            public object Call(object target, string name, params object[] args)
            {
                if (target == null) return null;
                return Keep(target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, Automation));
            }

            public void Dispose()
            {
                for (var i = _objects.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(_objects[i]);
                    }
                    catch (Exception)
                    {
                    }
                }
                _objects.Clear();
            }
        }

        private static object ActiveExcel(Scope scope)
        {
            try
            {
                return scope.Keep(Marshal.GetActiveObject("Excel.Application"));
            }
            catch (Exception)
            {
                return null;   // 動いていない
            }
        }

        private static Grid ReadRangeCore(string bookName, string sheetName, string rangeText, Func<string, bool> guarded,
                                          out string bookShown, out string sheetShown, out string problem)
        {
            bookShown = null;
            sheetShown = null;
            problem = null;
            using (var scope = new Scope())
            {
                var app = ActiveExcel(scope);
                if (app == null)
                {
                    problem = "Excel が動いていない";
                    return null;
                }

                try
                {
                    if (string.IsNullOrEmpty(bookName) || string.IsNullOrEmpty(sheetName))
                    {
                        problem = "ブックとシートを指定する";
                        return null;
                    }

                    object book = null;
                    {
                        var books = scope.Get(app, "Workbooks");
                        var count = Convert.ToInt32(scope.Get(books, "Count"), CultureInfo.InvariantCulture);
                        for (var i = 1; i <= count && book == null; i++)
                        {
                            var candidate = scope.Get(books, "Item", i);
                            if (!Visible(scope, candidate)) continue;
                            if (string.Equals(Text(scope.Get(candidate, "Name")), bookName, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(Text(scope.Get(candidate, "FullName")), bookName, StringComparison.OrdinalIgnoreCase))
                                book = candidate;
                        }
                    }
                    if (book == null)
                    {
                        problem = "そのブックは Excel で開いていない: " + bookName;
                        return null;
                    }

                    var path = Text(scope.Get(book, "Path")).Length > 0 ? Text(scope.Get(book, "FullName")) : null;
                    bookShown = path ?? Text(scope.Get(book, "Name"));
                    if (path != null && guarded != null && guarded(path))
                    {
                        problem = "このブックは、利用者が AI に渡さないと決めた場所にある。読まない";
                        bookShown = null;
                        return null;
                    }

                    object sheet;
                    {
                        try
                        {
                            sheet = scope.Get(scope.Get(book, "Sheets"), "Item", sheetName);
                        }
                        catch (Exception)
                        {
                            problem = "そのシートは無い: " + sheetName;
                            return null;
                        }
                    }
                    sheetShown = Text(scope.Get(sheet, "Name"));

                    object used;
                    try
                    {
                        used = scope.Get(sheet, "UsedRange");
                    }
                    catch (Exception)
                    {
                        problem = "セルのシートではない（グラフのシートなど）: " + sheetShown;
                        return null;
                    }

                    object target;
                    if (string.IsNullOrEmpty(rangeText))
                    {
                        target = used;
                    }
                    else
                    {
                        try
                        {
                            var asked = scope.Get(sheet, "Range", rangeText);
                            if (Convert.ToInt32(scope.Get(scope.Get(asked, "Areas"), "Count"), CultureInfo.InvariantCulture) > 1)
                            {
                                problem = "離れた範囲は1つずつ読む（A1:B5 の形で1つ）: " + rangeText;
                                return null;
                            }
                            target = scope.Call(app, "Intersect", asked, used);   // 中身のあるところだけ
                        }
                        catch (Exception ex)
                        {
                            problem = IsBusy(ex) ? Busy(ex) : "範囲を読めない（A1:H20 の形で）: " + rangeText;
                            return null;
                        }
                        if (target == null) return new Grid { Address = rangeText, Rows = 0, Columns = 0 };   // 空
                    }
                    return ReadGrid(scope, target, DumpCells, DumpColumns);
                }
                catch (Exception ex)
                {
                    problem = Busy(ex);
                    return null;
                }
            }
        }

        private static bool UnknownName(Exception ex)
        {
            if (ex is MissingMemberException || ex.InnerException is MissingMemberException) return true;
            var com = ex as COMException ?? ex.InnerException as COMException;
            return com != null && com.ErrorCode == unchecked((int)0x80020006);
        }

        private static bool IsBusy(Exception ex)
        {
            var com = ex as COMException ?? ex.InnerException as COMException;
            return com != null && (com.ErrorCode == unchecked((int)0x80010001) || com.ErrorCode == unchecked((int)0x800AC472));
        }

        private static string Busy(Exception ex)
        {
            if (IsBusy(ex)) return "Excel が今は応えない（セルを編集中か、ダイアログを出している）";
            return "Excel を読めなかった（" + (ex.InnerException ?? ex).Message + "）";
        }

        private static int Count(Scope scope, object range, string part)
        {
            return Convert.ToInt32(scope.Get(scope.Get(range, part), "Count"), CultureInfo.InvariantCulture);
        }

        private static string Address(Scope scope, object range)
        {
            return Text(scope.Get(range, "Address", false, false));
        }

        /// <summary>範囲の中身を読む。セルが多ければ先頭の行だけ（limit のセルの数まで）。</summary>
        private static Grid ReadGrid(Scope scope, object range, int limit, int maxColumns)
        {
            var grid = new Grid
            {
                Address = Address(scope, range),
                FirstRow = Convert.ToInt32(scope.Get(range, "Row"), CultureInfo.InvariantCulture),
                FirstColumn = Convert.ToInt32(scope.Get(range, "Column"), CultureInfo.InvariantCulture),
                Rows = Count(scope, range, "Rows"),
                Columns = Count(scope, range, "Columns"),
            };
            if (grid.Rows == 0 || grid.Columns == 0) return grid;

            grid.ColumnsRead = Math.Min(grid.Columns, maxColumns);
            var take = Math.Max(1, Math.Min(grid.Rows, limit / grid.ColumnsRead));
            var part = take < grid.Rows || grid.ColumnsRead < grid.Columns ? scope.Get(range, "Resize", take, grid.ColumnsRead) : range;
            var values = scope.Get(part, "Value");
            var formulas = scope.Get(part, "Formula");

            for (var r = 0; r < take; r++)
            {
                var row = new string[grid.ColumnsRead];
                for (var c = 0; c < grid.ColumnsRead; c++)
                {
                    var value = At(values, r, c);
                    var formula = At(formulas, r, c) as string;
                    var cell = Shown(value);
                    if (formula != null && formula.StartsWith("=", StringComparison.Ordinal)) cell += "（式 " + formula + "）";
                    row[c] = Clip(cell);
                }
                grid.Cells.Add(row);
            }
            return grid;
        }

        private static object At(object values, int row, int column)
        {
            var array = values as object[,];
            if (array == null) return row == 0 && column == 0 ? values : null;
            return array[array.GetLowerBound(0) + row, array.GetLowerBound(1) + column];
        }

        /// <summary>セルの値を字にする（日付は yyyy-MM-dd、エラーは #DIV/0! など）。</summary>
        private static string Shown(object value)
        {
            if (value == null) return string.Empty;
            if (value is DateTime)
            {
                var date = (DateTime)value;
                if (date.Date == new DateTime(1899, 12, 30)) return date.ToString("HH:mm:ss", CultureInfo.InvariantCulture);   // 時刻だけのセル
                return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            if (value is double) return ((double)value).ToString("G15", CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            if (value is bool) return (bool)value ? "TRUE" : "FALSE";
            if (value is int) return ErrorName((int)value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>エラーの値（CVErr。0x800A0000 + Excel のエラーの番号）を #DIV/0! などにする。</summary>
        private static string ErrorName(int code)
        {
            switch (code - unchecked((int)0x800A0000))
            {
                case 2000: return "#NULL!";
                case 2007: return "#DIV/0!";
                case 2015: return "#VALUE!";
                case 2023: return "#REF!";
                case 2029: return "#NAME?";
                case 2036: return "#NUM!";
                case 2042: return "#N/A";
                case 2043: return "#GETTING_DATA";
                case 2045: return "#SPILL!";
                case 2046: return "#CONNECT!";
                case 2047: return "#BLOCKED!";
                case 2048: return "#UNKNOWN!";
                case 2049: return "#FIELD!";
                case 2050: return "#CALC!";
                default: return "#エラー（" + code.ToString(CultureInfo.InvariantCulture) + "）";
            }
        }

        /// <summary>セルの字: 改行とタブは見える印に変え、長ければ切る。</summary>
        private static string Clip(string text)
        {
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "↵").Replace("\t", "→");
            return text.Length <= CellChars ? text : text.Substring(0, CellChars) + "…（長いので切った）";
        }

        private static string Text(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>列の番号（1 から）を A・B…・AA の名前にする。</summary>
        public static string ColumnName(int column)
        {
            var name = string.Empty;
            while (column > 0)
            {
                var rest = (column - 1) % 26;
                name = (char)('A' + rest) + name;
                column = (column - 1) / 26;
            }
            return name;
        }
    }
}
