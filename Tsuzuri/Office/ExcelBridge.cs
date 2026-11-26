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
    /// 開いているブックのどのシートにも書ける（AI に渡さない場所・URL の場所・隠れたブックは除く。書く前に AI が利用者の許可を取る）。保存はしない。
    /// AI の COM の台本（excel.ps1 -Script）は、別のプロセスで動かす。ここでは、Excel を呼ぶほかの処理と重ならないように順番を守る（RunExclusive）。
    ///
    /// Office のアプリとのやり取りは、この Office フォルダの中だけで行う（アプリの中で COM を使うのはここだけ。AI の台本は別のプロセス）（Tsuzuri と AiLink で同じファイルを使う。AiLink はリンクしてコンパイルする。2026-11-26）。
    /// いつか COM が使えなくなっても、ここを差し替えれば、AI に渡す今の状態の形はそのまま残せるように。
    /// 型の参照（Interop）は使わず、名前で呼ぶ（Excel の版に縛られない）。
    /// 呼ぶたびに自分の STA のスレッドで読み、時間を切る（Excel が応答しない・セルを編集中でも、こちらを止めない）。
    /// 受け取った COM の参照はその場で全部手放す（持ち続けると、利用者が Excel を閉じても EXCEL.EXE が残る）。
    /// </summary>
    public static partial class ExcelBridge
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
            book = UncPath(book);
            Grid grid = null;
            string shownBook = null, shownSheet = null, why = null;
            var outcome = RunSta(job => grid = ReadRangeCore(book, sheet, range, guarded, out shownBook, out shownSheet, out why), timeout, null);
            var done = outcome == Outcome.Done;
            bookShown = done ? shownBook : null;
            sheetShown = done ? shownSheet : null;
            problem = done ? why : outcome == Outcome.Previous ? StillBusy : NotAnswering;
            return done ? grid : null;
        }

        /// <summary>書いた範囲（Excel から読んだ本当のブック（保存していればフルパス、まだなら名前）とシートの名前）。</summary>
        public sealed class Written
        {
            public string Book;
            public string Sheet;
            public string Address;
            public int Rows;
            public int Columns;
            /// <summary>書く途中で失敗した（途中まで書けているかもしれない）。</summary>
            public bool Failed;
        }

        /// <summary>
        /// 指定したブックのシートに書く。at は書き始めの左上のセル（B3）か、中身と同じ大きさの範囲（B3:D5）。cells は行ごと・列ごとの文字で、
        /// セルに手で打ったときと同じに入る（「=」で始まれば式。式は英語の関数名と「,」の区切り）。書けなければ problem にわけを入れて null。
        /// 書く途中で失敗したときは、problem にわけを入れ、Failed を立てて返す（途中まで書けているかもしれない）。
        /// instance は選んだときの Excel のプロセスの番号（0 なら見ない）。保存していない新しいブックは、これが違えば書かない。
        /// 時間切れのときは null。そのあと、書き始める前なら書かない。書き始めていて遅れて書けたら、それを late に渡す（裏のスレッドから）。
        /// </summary>
        public static Written Write(string book, string sheet, string at, List<string[]> cells, long instance, Func<string, bool> guarded, int timeout, Action<Written> late, out string problem)
        {
            book = UncPath(book);
            Written written = null;
            string why = null;
            var outcome = RunSta(job => written = WriteCore(book, sheet, at, cells, instance, guarded, job, out why), timeout,
                                 () => { if (written != null && late != null) late(written); });
            if (outcome == Outcome.Previous) problem = StillBusy;
            else if (outcome == Outcome.TimedOut)
                problem = NotAnswering + "。まだ書き始めていなければ、書かない。書き始めていたら、Excel が応えたときに書き終わるか、途中で失敗する（どちらも利用者に知らせが出る）";
            else problem = why;
            return outcome == Outcome.Done ? written : null;
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

        private static object FindSheet(Scope scope, object app, string bookName, string sheetName, long instance, Func<string, bool> guarded, out string problem)
        {
            problem = null;
            if (instance != 0 && Unsaved(bookName) && InstanceOf(scope, app) != instance)
            {
                problem = "そのブックは Excel で開いていない（選んだときの Excel が閉じられた。保存していない新しいブックは、同じ名前でも別のブック）: " + bookName;
                return null;
            }
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
            // AI に渡さない場所かは、シートを探す前に確かめる（そのブックにどんなシートがあるかを、返すわけから漏らさない）
            if (guarded != null && Text(scope.Get(book, "Path")).Length > 0 && guarded(Text(scope.Get(book, "FullName"))))
            {
                problem = "このブックは、利用者が決めた AI に渡さない場所か、https:// などの URL の場所にある。何もしていない";
                return null;
            }
            try
            {
                return scope.Get(scope.Get(book, "Sheets"), "Item", sheetName);
            }
            catch (Exception ex) when (!IsBusy(ex))   // 応えないときは外で「今は応えない」として返す（「無い」と取り違えない）
            {
                problem = "そのシートは無い: " + sheetName;
                return null;
            }
        }

        private static Written WriteCore(string bookName, string sheetName, string at, List<string[]> cells, long instance, Func<string, bool> guarded, Job job, out string problem)
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
                    var sheet = FindSheet(scope, app, bookName, sheetName, instance, guarded, out problem);
                    if (sheet == null) return null;

                    // 記録には、Excel の本当のブック（保存していればフルパス、まだなら名前）とシートの名前を残す（一覧の選択・ステータスバーの知らせと突き合わせるため）
                    var foundBook = scope.Get(sheet, "Parent");
                    var foundFull = Text(scope.Get(foundBook, "FullName"));
                    var bookKey = Text(scope.Get(foundBook, "Path")).Length > 0 ? foundFull : Text(scope.Get(foundBook, "Name"));
                    var sheetKey = Text(scope.Get(sheet, "Name"));

                    object given;
                    try
                    {
                        given = scope.Get(sheet, "Range", at);
                    }
                    catch (Exception ex)
                    {
                        problem = IsBusy(ex) ? Busy(ex) : "書く場所を読めない（B3 か B3:D5 の形で。グラフのシートには書けない）。何も書いていない: " + at;
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
                    object target;
                    try
                    {
                        target = scope.Get(first, "Resize", rows, columns);
                    }
                    catch (Exception ex)
                    {
                        problem = IsBusy(ex) ? Busy(ex) : "書く範囲がシートの端を超える（" + rows + " 行 × " + columns + " 列）。何も書いていない: " + at;
                        return null;
                    }
                    // 名前の定義などで、ほかのシート・ほかのブックを指していないか（シートの名前は大文字・小文字を区別しない）
                    var targetSheet = scope.Get(target, "Worksheet");
                    var sheetBook = scope.Get(targetSheet, "Parent");
                    if (!string.Equals(Text(scope.Get(targetSheet, "Name")), sheetKey, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(Text(scope.Get(sheetBook, "FullName")), foundFull, StringComparison.OrdinalIgnoreCase))
                    {
                        problem = "書く場所がほかのシートを指している（B3 の形で、シートの名前を付けない）。何も書いていない: " + at;
                        return null;
                    }
                    var address = Address(scope, target);

                    var data = new object[rows, columns];
                    for (var r = 0; r < rows; r++)
                        for (var c = 0; c < columns; c++)
                            data[r, c] = c < cells[r].Length ? cells[r][c] : string.Empty;
                    object value = rows == 1 && columns == 1 ? data[0, 0] : (object)data;
                    var written = new Written { Book = bookKey, Sheet = sheetKey, Address = address, Rows = rows, Columns = columns };
                    if (job.Abandoned)
                    {
                        problem = "Excel が応えるのが遅れたので、書かなかった";   // 呼んだ側はもう待っていない。あとから勝手に書かない
                        return null;
                    }
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
                        written.Failed = true;   // 途中まで書けているかもしれない
                        problem = "書けなかった（式の書き間違い（英語の関数名・「,」の区切り）・シートの保護・結合したセル・配列の式など）: " + (ex.InnerException ?? ex).Message
                                  + "。途中まで書けているかもしれない";
                        return written;
                    }

                    return written;
                }
                catch (Exception ex)
                {
                    problem = IsBusy(ex) ? Busy(ex) : "書く前の確かめで失敗したので、何も書いていない: " + (ex.InnerException ?? ex).Message;
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
            /// <summary>Excel のプロセスの番号（Excel を開き直すと変わる）。読めなければ 0。</summary>
            public long Instance;
            /// <summary>保存していない変更がある（Workbook.Saved が偽。AI の分も、手で直した分も）。</summary>
            public bool Unsaved;
        }

        /// <summary>開いているブックとシートの一覧。Excel が動いていなければ空。読めなければ null で、problem にわけ。</summary>
        public static List<Book> ListBooks(int timeout, out string problem)
        {
            List<Book> books = null;
            string why = null;
            var outcome = RunSta(job => books = ListBooksCore(out why), timeout, null);
            problem = outcome == Outcome.Done ? why : outcome == Outcome.Previous ? StillBusy : NotAnswering;
            return outcome == Outcome.Done ? books : null;
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
                    var instance = InstanceOf(scope, app);
                    foreach (var book in list) book.Instance = instance;
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
                        Unsaved = scope.Get(book, "Saved") as bool? == false,
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

        /// <summary>Excel を呼ぶ1回分。State: 0 動いている、1 終えた、2 時間切れで呼んだ側が待つのをやめた。</summary>
        private sealed class Job
        {
            public int State;

            /// <summary>呼んだ側が待つのをやめた（書く直前に見て、やめる。あとから勝手に書かないように）。</summary>
            public bool Abandoned
            {
                get { return Volatile.Read(ref State) == 2; }
            }
        }

        private enum Outcome { Done, TimedOut, Previous }

        /// <summary>
        /// Excel を呼ぶのは一度に1つだけ（2026-11-25。レビューの指摘）。時間切れになっても、Excel の中に入った呼び出しを抱えた裏のスレッドは止められない。
        /// そのスレッドが終わるまで次を始めないことで、Excel が止まっている間にスレッドと COM の参照が積み上がらず、
        /// 書く手順（確かめる→書く）や AI の台本が、ほかの呼び出しと混ざらない。
        /// </summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private const string NotAnswering = "Excel が応答しない（セルを編集中か、ダイアログを出しているか、重い計算・マクロの途中）";
        private const string StillBusy = "Excel がまだ前の頼みに応えていない（セルを編集中か、ダイアログを出しているか、重い計算・マクロの途中か、AI の台本を動かしている途中）。今回は何もしていない";

        /// <summary>
        /// 自分の STA のスレッドで work を呼び、timeout まで待つ。前の呼び出しのスレッドが timeout のうちに終わらなければ、始めずに Previous。
        /// 時間切れ（TimedOut）のときは、スレッドが終わったときにそのスレッドで late を呼ぶ（遅れて書けた分を知らせるため）。
        /// </summary>
        private static Outcome RunSta(Action<Job> work, int timeout, Action late)
        {
            if (!Gate.Wait(timeout)) return Outcome.Previous;
            var job = new Job();
            Thread thread;
            try
            {
                thread = new Thread(() =>
                {
                    try
                    {
                        work(job);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        // late（遅れて書けた分を知らせる）を、次の呼び出しより先に出す（知らせの順を、Excel に書いた順に揃える）。late は知らせを出すだけで、すぐ返る
                        if (Interlocked.CompareExchange(ref job.State, 1, 0) == 2 && late != null)
                        {
                            try
                            {
                                late();
                            }
                            catch (Exception)
                            {
                            }
                        }
                        Gate.Release();
                    }
                });
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch (Exception)
            {
                Gate.Release();   // スレッドを始められなかった
                throw;
            }
            if (thread.Join(timeout)) return Outcome.Done;
            // 待つのをやめる。その直前に終えていれば、終えたものとして結果を使う（late は呼ばれない）
            return Interlocked.CompareExchange(ref job.State, 2, 0) == 0 ? Outcome.TimedOut : Outcome.Done;
        }

        /// <summary>
        /// Excel を呼ぶほかの処理と重ならないように、work を呼ぶ（AI の台本を別のプロセスで動かす間。2026-11-25）。
        /// 前の呼び出しが wait のうちに終わらなければ、呼ばずに StillBusy を返す（started は偽）。work はこのスレッドで動く。
        /// </summary>
        public static string RunExclusive(Func<string> work, int wait, out bool started)
        {
            started = false;
            if (!Gate.Wait(wait)) return StillBusy;
            started = true;
            try
            {
                return work();
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Excel のプロセスの番号（Excel を開き直すと変わる。保存していない新しいブックを、同じ名前の別のブックと取り違えないため）。読めなければ 0。
        /// Application.Hwnd そのものは使わない（Excel 2013 からはブックごとに窓が分かれ、前に出ているブックで変わりうる）。その窓のプロセスの番号にする。
        /// </summary>
        private static long InstanceOf(Scope scope, object app)
        {
            try
            {
                var hwnd = new IntPtr(Convert.ToInt64(scope.Get(app, "Hwnd"), CultureInfo.InvariantCulture));
                uint process;
                GetWindowThreadProcessId(hwnd, out process);
                return process;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>保存していない新しいブック（フルパスでなく名前だけ）か。</summary>
        /// <summary>
        /// ネットワークの場所のブック（\\server\…）のパスの頭が \ 1つになっていれば、\\ に戻す（2026-11-26）。Tsuzuri の Claude は Git Bash でコマンドを動かし、
        /// 二重引用符の中の \\ が \ になって届く（AiLink の Codex は PowerShell なので起きない）。ドライブの付いたパス・名前だけのブックはそのまま。
        /// </summary>
        internal static string UncPath(string book)
        {
            if (book == null) return book;
            return book.StartsWith("\\", StringComparison.Ordinal) && !book.StartsWith("\\\\", StringComparison.Ordinal) ? "\\" + book : book;
        }

        private static bool Unsaved(string book)
        {
            return book.IndexOf('\\') < 0 && book.IndexOf('/') < 0;
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

            /// <summary>メソッドでもプロパティでも呼ぶ（Item・Text など。VB の遅い結び付けと同じく、どちらの形でも受け付けさせる）。</summary>
            public object Pick(object target, string name, params object[] args)
            {
                if (target == null) return null;
                return Keep(target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, args, Automation));
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
                        problem = "このブックは、利用者が決めた AI に渡さない場所か、https:// などの URL の場所にある。読まない";
                        bookShown = null;
                        return null;
                    }

                    object sheet;
                    {
                        try
                        {
                            sheet = scope.Get(scope.Get(book, "Sheets"), "Item", sheetName);
                        }
                        catch (Exception ex) when (!IsBusy(ex))   // 応えないときは外で「今は応えない」として返す（「無い」と取り違えない）
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
                    catch (Exception ex) when (!IsBusy(ex))
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
            // RPC_E_CALL_REJECTED・RPC_E_SERVERCALL_RETRYLATER（どちらも呼び出しを受け付けなかった）・Excel の VBA_E_IGNORE
            return com != null && (com.ErrorCode == unchecked((int)0x80010001) || com.ErrorCode == unchecked((int)0x8001010A) || com.ErrorCode == unchecked((int)0x800AC472));
        }

        private static string Busy(Exception ex)
        {
            if (IsBusy(ex)) return "Excel が今は応えない（セルを編集中か、ダイアログを出している）。今回は何もしていない";
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
            var formulas = GetFormula(scope, part);   // 書くときと同じ形（Formula2）。Formula だと広がる式に「@」が付いて見え、写して書くと式が変わる

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
