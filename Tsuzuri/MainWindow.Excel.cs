using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Tsuzuri.Markdown;
using Tsuzuri.Office;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: 開いている Excel を読み書きする（2026-11-24。利用者の考え。SPEC 19章・4章）。
    // ・Claude は、開いているブックのどのシートも読める・書ける（2026-11-25。利用者の判断。前は一覧で選んだ1つのシートだけ書けた）。
    //   AI に渡さない場所・https:// などの URL の場所・隠れたブックは扱わない（LAN の共有フォルダ・NAS は扱う）。書く前に、Claude が利用者の許可を取る（AI に渡す文で指示する）。保存は利用者がする。
    // ・ランチャーのファイラーの下の段に、開いているブックとシートの一覧。選び方はエクスプローラーと同じ（押すとそれだけ・Ctrl で足す外す・Shift で範囲。ブックの行はシート全部。初めは何も選んでいない）。
    //   選んだシートが、AI への「このシート」になる。話しかけるたびの今の状態に、選んだブックとシートの名前だけを書く（中身・Excel で前に出ているもの・保存したかは渡さない）。
    // ・Claude が範囲を指定して読む（excel.ps1 → パイプの excel-read）・書く（excel.ps1 -Write → パイプの excel-write）・COM の台本を動かす（excel.ps1 -Script → excel-script）。
    //   読むときは、ブック・シートを付けなければ、選んだシートが1つのときだけそのシート（2つ以上なら付けさせる）。書くときは、どちらも付けさせる
    //   （付けなければ断る。許可をもらったあとに利用者が選び直しても、許可をもらったのと別のシートに書かないように）。
    // ・戻す仕組みは持たない（2026-11-25。利用者の判断。Claude は保存しないので、利用者は保存せずに閉じれば元に戻せる）。
    //   保存していない変更があるブックには、一覧で名前の右に「※」（Claude の分も、手で直した分も）。
    // COM を使うのは Office/ExcelBridge.cs だけ。
    public partial class MainWindow
    {
        /// <summary>一覧で選んだシート1つ（ブックはフルパスか、保存していなければ名前）。</summary>
        private sealed class ExcelChoice
        {
            public readonly string Book;
            public readonly string Sheet;
            /// <summary>選んだときの Excel のプロセスの番号（保存していない新しいブックを、Excel を開き直したあとの同じ名前のブックと取り違えないため）。</summary>
            public readonly long Instance;

            public ExcelChoice(string book, string sheet, long instance)
            {
                Book = book;
                Sheet = sheet;
                Instance = instance;
            }
        }

        private static readonly ExcelChoice[] NoExcelChoices = new ExcelChoice[0];

        /// <summary>一覧で選んだシート（選んだ順）。パイプのスレッドからも読むので、配列ごと入れ替える（中は変えない）。</summary>
        private volatile ExcelChoice[] _excelChoices = NoExcelChoices;

        /// <summary>
        /// AI に渡さないブックか。OneDrive・SharePoint などの https の場所は、目印のフォルダ（AiGuard）を確かめられないので渡さない側にする。
        /// </summary>
        private static bool IsGuardedBook(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            return Path.IsPathRooted(path) && AiGuard.IsGuarded(path);
        }

        private static bool SameExcelBook(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>保存していない新しいブック（フルパスでなく名前）か。</summary>
        private static bool UnsavedExcelBook(string book)
        {
            return book.IndexOf('\\') < 0 && book.IndexOf('/') < 0;
        }

        /// <summary>選んだシートがこれより多いブックは、全部を選んでいなくても、名前を並べずにファイルに書く。</summary>
        private const int ExcelSheetsInline = 30;

        /// <summary>
        /// シートの名前の一覧を書くファイルの頭（2026-11-25）。画面ごと。中身ごとに別の名前（sheets-画面-中身の印.txt）にし、上書きしない
        /// （前の頼みの今の状態が指すファイルを、あとの頼みで書き換えないため）。閉じるときにまとめて消す。
        /// </summary>
        private string ExcelSheetsPrefix
        {
            get { return "sheets-" + WindowKey + "-"; }
        }

        /// <summary>名前の一覧をファイルに書き、そのパスを返す。中身が同じなら同じファイル。書けなければ null。</summary>
        private string WriteExcelSheets(string content)
        {
            try
            {
                string mark;
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    mark = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(content)).Take(6).Select(b => b.ToString("x2")));
                }
                var path = Path.Combine(AiFolder, ExcelSheetsPrefix + mark + ".txt");
                if (File.Exists(path)) return path;
                Directory.CreateDirectory(AiFolder);
                var temp = path + ".saving";
                try
                {
                    File.WriteAllText(temp, content, new UTF8Encoding(false));
                    File.Move(temp, path);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>この画面が書いた名前の一覧のファイルを消す（閉じるとき）。</summary>
        private void DeleteExcelSheets()
        {
            try
            {
                if (!Directory.Exists(AiFolder)) return;
                foreach (var path in Directory.GetFiles(AiFolder, ExcelSheetsPrefix + "*.txt")) File.Delete(path);
            }
            catch (Exception)
            {
                // 消せなくても閉じるのは止めない。次に起動したときに片付ける
            }
        }

        /// <summary>
        /// 今の状態に「Excel」の段を書く（選んだブックとシートの名前だけ。選んでいなくても、そう書く）。Excel は読まない。
        /// 2枚以上のシートがあるブックで、一覧を最後に読んだときのシートを全部選んでいれば「全シート（N 枚）」、
        /// 選んだシートが ExcelSheetsInline 枚より多ければ「シート N 枚」と書き、名前は並べずにファイルに書いてそのパスを添える
        /// （2026-11-25。利用者の考え。シートがとても多いブックで、話しかけるたびに名前を全部並べないため）。ファイルを書けなければ名前を並べる。
        /// </summary>
        private void AppendExcelContext(StringBuilder text)
        {
            var choices = _excelChoices;
            if (choices.Length == 0)
            {
                text.Append("Excel: ランチャーの Excel の一覧でシートを選んでいない（前の頼みで選んでいても、今は外されている）\n");
                return;
            }
            // 画面のスレッドで動くので、ファイルを確かめに行かず、一覧を読んだときに裏で決めたものを使う（つながらない共有フォルダで画面を止めないように。2026-11-26 のレビュー）
            var known = _excelGuarded;
            var shown = choices.Where(choice => !(known != null ? known.Contains(choice.Book) || choice.Book.IndexOf("://", StringComparison.Ordinal) >= 0 : IsGuardedBook(choice.Book))).ToList();
            var hidden = choices.Length - shown.Count;
            if (shown.Count == 0)
            {
                text.Append("Excel: ランチャーの Excel の一覧で選んだシートは、AI に渡さない場所のブックにあるので、名前も渡していない。尋ねたり探したりしない\n");
                return;
            }

            // ブックごとにまとめる（ブックは最初に選んだ順）。名前を並べないブックの分は、先にファイルの中身を作る
            var books = _lastExcelBooks;
            var groups = shown.GroupBy(choice => choice.Book, StringComparer.OrdinalIgnoreCase).ToList();
            var sheetList = new StringBuilder();
            var listedOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wholeBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var chosen = group.ToList();
                var listed = books != null ? books.FirstOrDefault(item => SameExcelBook(item.Path ?? item.Name, group.Key)) : null;
                var whole = listed != null && listed.Sheets.Count >= 2 && chosen.Count == listed.Sheets.Count
                            && listed.Sheets.All(sheet => chosen.Any(choice => string.Equals(choice.Sheet, sheet, StringComparison.OrdinalIgnoreCase)));
                if (!whole && chosen.Count <= ExcelSheetsInline) continue;
                listedOut.Add(group.Key);
                if (whole) wholeBooks.Add(group.Key);
                sheetList.Append("# ブック: ").Append(group.Key).Append(whole ? "（全シート " + chosen.Count + " 枚）" : "（選んだシート " + chosen.Count + " 枚）").Append('\n');
                foreach (var sheet in whole ? listed.Sheets : chosen.Select(choice => choice.Sheet).ToList()) sheetList.Append(sheet).Append('\n');
            }
            var file = sheetList.Length > 0 ? WriteExcelSheets(sheetList.ToString()) : null;
            if (file == null) listedOut.Clear();   // 書けなければ、名前を並べる

            text.Append("Excel: 利用者がランチャーの Excel の一覧で選んだシート（").Append(shown.Count).Append(" 枚。渡すのは名前だけで、中身は渡していない）: ");
            var first = true;
            foreach (var group in groups)
            {
                var chosen = group.ToList();
                var unsaved = UnsavedExcelBook(group.Key) ? "（まだ保存していない新しいブック）" : string.Empty;
                if (listedOut.Contains(group.Key))
                {
                    if (!first) text.Append("、");
                    first = false;
                    text.Append("ブック「").Append(group.Key).Append("」").Append(unsaved)
                        .Append(wholeBooks.Contains(group.Key) ? "の全シート（" : "のシート（").Append(chosen.Count)
                        .Append(" 枚。シートの名前はここに並べず、ファイル「").Append(file).Append("」に書いた）");
                    continue;
                }
                foreach (var choice in chosen)
                {
                    if (!first) text.Append("、");
                    first = false;
                    text.Append("ブック「").Append(group.Key).Append("」").Append(unsaved).Append("のシート「").Append(choice.Sheet).Append("」");
                }
            }
            if (hidden > 0) text.Append("（ほかに選んだ ").Append(hidden).Append(" 枚は、AI に渡さない場所のブックなので、名前も渡していない。尋ねたり探したりしない）");
            text.Append('\n');
        }

        /// <summary>
        /// 会話の名前（#1）から、Excel の作業ファイルの名前に付ける形（tab1）を作る（2026-11-25）。会話の名前はアプリの中で重ならないので、
        /// 会話ごと（窓をまたいでも）に別のファイルになる。会話が2つ以上あると、同じ名前のファイルを取り合い、別の会話の中身を書いてしまったため。
        /// </summary>
        private static string AiExcelFileSuffix(string name)
        {
            // 数字だけを使う（app.json から戻した名前に \ や .. が入っていても、AI の置き場所の外を指さないように）
            return "tab" + new string((name ?? string.Empty).Where(char.IsDigit).ToArray());
        }

        /// <summary>
        /// 今の状態に「Excel の作業ファイル」の段を書く（この会話が書く中身・台本を作るファイル。excel.ps1 は環境変数 TSUZURI_AI_NAME で同じ名前を選ぶ）。
        /// どの会話か分からなければ、書く・台本を動かすのはやめるよう書く。
        /// </summary>
        private void AppendExcelFiles(StringBuilder text, AiTab tab)
        {
            if (tab == null || string.IsNullOrEmpty(tab.Name))
            {
                text.Append("Excel の作業ファイル: どの会話からの頼みか分からなかったので渡せていない（Excel に書く・台本を動かすのはやめ、利用者に伝える。読むのはよい）\n");
                return;
            }
            var suffix = AiExcelFileSuffix(tab.Name);
            text.Append("Excel の作業ファイル（この会話専用）: 書く中身は「").Append(Path.Combine(AiFolder, "excel-write-" + suffix + ".tsv"))
                .Append("」、台本は「").Append(Path.Combine(AiFolder, "excel-script-" + suffix + ".ps1")).Append("」\n");
        }

        /// <summary>AI の欄の下端の行に足す、Excel の分（「  ・Excel 見積.xlsx › 明細」。2つ以上なら枚数）。</summary>
        private string ExcelSummaryPart()
        {
            return ExcelOnlySummaryPart() + (_outlook != null ? _outlook.SummaryPart() : string.Empty);   // Outlook の分も（2026-11-26）
        }

        private string ExcelOnlySummaryPart()
        {
            var choices = _excelChoices;
            if (choices.Length == 0) return string.Empty;
            if (choices.Length > 1) return "  ・Excel " + choices.Length + " シート";
            var guarded = _excelGuarded;   // 一覧を読んだときに裏で決めたもの（下端の行は何度も出し直すので、ここでファイルを確かめに行かない）
            if (guarded != null && guarded.Contains(choices[0].Book)) return "  ・Excel（AI に渡さないブック）";
            return "  ・Excel " + Path.GetFileName(choices[0].Book) + " › " + choices[0].Sheet;
        }

        /// <summary>
        /// 付けなかったブック・シートを、一覧で選んだシートで埋める（Excel で前に出ているものは使わない）。決まらなければ、わけを返す。
        /// どちらも付けていなければ、選んだシートが1つのときだけそのシート。シートだけなら、選んだシートのうちその名前のもの（無ければ、選んだブックが1つならそのブック）。
        /// ブックだけなら、選んだシートのうちそのブックのものが1つのときだけそれ。instance は、選んだシートに当たれば選んだときの Excel のプロセスの番号。
        /// </summary>
        private string ResolveExcelTarget(ref string book, ref string sheet, out long instance)
        {
            instance = 0;
            var all = _excelChoices;   // 一度で読む（選び替えの途中の、食い違った組にしない）
            // AI に渡さない場所のブックのシートは数えない（今の状態の段にも書いていないので、枚数を段と合わせる）
            var choices = all.Where(choice => !IsGuardedBook(choice.Book)).ToArray();
            if (book.Length == 0 && sheet.Length == 0 && choices.Length == 0 && all.Length > 0)
                return "ランチャーの Excel の一覧で選んだシートは、AI に渡さない場所のブックにあるので扱わない。利用者にそう伝える（名前を尋ねたり探したりしない）";
            if (book.Length == 0 && sheet.Length == 0)
            {
                if (choices.Length == 0) return "ランチャーの Excel の一覧でシートが選ばれていない。どのブックのどのシートかを推し量らず、利用者に一覧で選んでもらうか、名前を教えてもらう";
                if (choices.Length > 1) return "ランチャーの Excel の一覧でシートを " + choices.Length + " 枚選んでいる。どのシートかを -Book と -Sheet で付ける（分からなければ利用者に尋ねる）";
                book = choices[0].Book;
                sheet = choices[0].Sheet;
            }
            else if (book.Length == 0)
            {
                var name = sheet;
                var named = choices.Where(choice => string.Equals(choice.Sheet, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var books = choices.Select(choice => choice.Book).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (named.Count == 1) book = named[0].Book;
                else if (named.Count == 0 && books.Count == 1) book = books[0];
                else return "どのブックのシートかを -Book で付ける";
            }
            else if (sheet.Length == 0)
            {
                var given = book;
                var inBook = choices.Where(choice => SameExcelBook(choice.Book, given) || SameExcelBook(Path.GetFileName(choice.Book), given)).ToList();
                if (inBook.Count != 1) return "シートも -Sheet で付ける";
                book = inBook[0].Book;
                sheet = inBook[0].Sheet;
            }

            var bookName = book;
            var sheetName = sheet;
            var match = choices.FirstOrDefault(choice => (SameExcelBook(choice.Book, bookName) || SameExcelBook(Path.GetFileName(choice.Book), bookName))
                                                         && string.Equals(choice.Sheet, sheetName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                instance = match.Instance;
                book = match.Book;   // 選んだシートに当たれば、そのブックのフルパス（名前だけで渡されても、同じ名前の別のブックと取り違えない）
            }
            return null;
        }

        /// <summary>
        /// Claude が範囲を指定して読む（パイプの「excel-read→ブック→シート→範囲」。どれも Base64）。開いているブックのどのシートも読める。
        /// 付けなかったブック・シートは ResolveExcelTarget で埋める。範囲が空なら使っている範囲全体。
        /// 結果は AI の置き場所のファイルに書き、「ok→パス（説明）」を返す。読めなければ「error→わけ」。
        /// </summary>
        private string ServeExcelRead(string request)
        {
            var parts = request.Split('\t');
            string book, sheet, range;
            try
            {
                book = parts.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Trim() : string.Empty;
                sheet = parts.Length > 2 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).Trim() : string.Empty;
                range = parts.Length > 3 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])).Trim() : string.Empty;
            }
            catch (FormatException)
            {
                return "error\t頼みを読めなかった。";
            }

            long instance;
            var unresolved = ResolveExcelTarget(ref book, ref sheet, out instance);
            if (unresolved != null) return "error\t" + unresolved;

            string bookShown, sheetShown, problem;
            var grid = ExcelBridge.ReadRange(book, sheet, range, 10000, IsGuardedBook, out bookShown, out sheetShown, out problem);
            if (grid == null) return "error\t" + (problem ?? "Excel を読めなかった。");

            var output = new StringBuilder();
            output.Append("# ブック: ").Append(bookShown).Append('\n');
            output.Append("# シート: ").Append(sheetShown).Append('\n');
            output.Append("# 範囲: ").Append(grid.Address).Append("（").Append(grid.Rows).Append(" 行 × ").Append(grid.Columns).Append(" 列。中身のあるところだけ）\n");
            output.Append("# タブ区切り。1行目は列の名前、各行の頭は行の番号。式のセルは値のあとに「（式 =…）」。セルの中の改行は ↵、タブは →\n");
            if (grid.Rows > 0) output.Append(grid.ToTsv(string.Empty));
            if (grid.Truncated || grid.ColumnsTruncated)
                output.Append("# （多いので").Append(grid.Truncated ? "先頭の " + grid.Cells.Count + " 行" : string.Empty)
                    .Append(grid.Truncated && grid.ColumnsTruncated ? "・" : string.Empty)
                    .Append(grid.ColumnsTruncated ? "左の " + grid.ColumnsRead + " 列" : string.Empty)
                    .Append("だけ。続きは -Range で範囲を絞って読む）\n");

            var file = Path.Combine(AiFolder, "excel-read-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tsv");   // 頼みごとに別の名前（会話が2つ以上あっても、別の会話の結果で上書きしない。2026-11-25）
            try
            {
                File.WriteAllText(file, output.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "error\t書き出せなかった: " + ex.Message;
            }
            return "ok\t" + file + "（ブック " + bookShown + "・シート " + sheetShown + "・範囲 " + grid.Address + "。" + grid.Rows + " 行 × " + grid.Columns + " 列"
                   + (grid.Truncated || grid.ColumnsTruncated ? "。多いので一部だけ（ファイルの最後の行に書いた）" : string.Empty) + "）";
        }

        /// <summary>
        /// シートの概要（パイプの「excel-overview→ブック→シート」。どれも Base64）: 値と式に加え、書式・結合・図形・グラフなどを1つのファイルに書き、
        /// 「ok→パス（説明）」を返す。付けなかったブック・シートは ResolveExcelTarget で埋める（読むときと同じ）。
        /// </summary>
        private string ServeExcelOverview(string request)
        {
            var parts = request.Split('\t');
            string book, sheet;
            try
            {
                book = parts.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Trim() : string.Empty;
                sheet = parts.Length > 2 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).Trim() : string.Empty;
            }
            catch (FormatException)
            {
                return "error\t頼みを読めなかった。";
            }

            long instance;
            var unresolved = ResolveExcelTarget(ref book, ref sheet, out instance);
            if (unresolved != null) return "error\t" + unresolved;

            string bookShown, sheetShown, problem;
            var mark = Guid.NewGuid().ToString("N").Substring(0, 12);   // 頼みごとに別の名前（会話が2つ以上あっても、別の会話の結果で上書きしない。2026-11-25）
            var text = ExcelBridge.Overview(book, sheet, IsGuardedBook, Path.Combine(AiFolder, "excel-overview-" + mark + ".png"), 45000, out bookShown, out sheetShown, out problem);
            if (text == null) return "error\t" + (problem ?? "Excel を読めなかった。");
            var file = Path.Combine(AiFolder, "excel-overview-" + mark + ".txt");
            try
            {
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "error\t書き出せなかった: " + ex.Message;
            }
            return "ok\t" + file + "（ブック " + bookShown + "・シート " + sheetShown + " の概要。シートの画像のパス・書式・結合・図形・グラフなどと、セルの値と式。長いことがあるので、Read で全部読み、「## 画像」に書いた画像も Read で見る）";
        }

        /// <summary>
        /// Claude が書く（パイプの「excel-write→ブック→シート→場所→中身」。どれも Base64）。開いているブックのどのシートにも書ける
        /// （書いてよいかは、Claude が書く前に利用者に尋ねる。AI に渡す文の指示）。ブックとシートは両方付けさせる（付けなければ断る。
        /// 許可をもらったあとに利用者が選び直しても、許可をもらったのと別のシートに書かないように）。
        /// 中身はタブ区切りで、1行が Excel の1行。書けたら「ok→説明」、書けなければ「error→わけ」。
        /// </summary>
        private string ServeExcelWrite(string request)
        {
            var parts = request.Split('\t');
            string book, sheet, at, content;
            try
            {
                book = parts.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])).Trim() : string.Empty;
                sheet = parts.Length > 2 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).Trim() : string.Empty;
                at = parts.Length > 3 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])).Trim() : string.Empty;
                content = parts.Length > 4 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[4])) : string.Empty;
            }
            catch (FormatException)
            {
                return "error\t頼みを読めなかった。";
            }
            // 終わりの印（excel.ps1 が最後に付ける）が無ければ、途中で切れた頼み（送る途中で excel.ps1 が止められた）。途中までの中身を書かない
            if (parts.Length != 6 || parts[5] != ExcelRequestEnd)
                return "error\t頼みが途中で切れていた（終わりの印が無い）。何も書いていない";

            if (book.Length == 0 || sheet.Length == 0)
                return "error\t書くときは -Book と -Sheet を両方付ける（許可をもらうときに示したブックとシート）。何も書いていない";
            long instance;
            var unresolved = ResolveExcelTarget(ref book, ref sheet, out instance);
            if (unresolved != null) return "error\t" + unresolved + "。何も書いていない";
            if (IsGuardedBook(book)) return "error\tAI に渡さない場所のブックには書かない。何も書いていない";
            if (at.Length == 0) return "error\t書く場所（左上のセル。B3 の形）を -Range で付ける。何も書いていない";

            var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);   // 最後の改行・空行（知らないうちに下のセルを空にしない）
            var cells = lines.Select(line => line.Split('\t')).ToList();
            var width = cells.Count > 0 ? cells.Max(row => row.Length) : 0;
            var padded = cells.Sum(row => width - row.Length);   // 列の足りない行を空のセルで埋める数

            string problem;
            var written = ExcelBridge.Write(book, sheet, at, cells, instance, IsGuardedBook, 10000,
                late => Dispatcher.BeginInvoke(new Action(() => NoteExcelWrite(late, true))), out problem);
            if (written == null) return "error\t" + (problem ?? "書けたかどうか分からない。excel.ps1 で読んで確かめる");

            var where = WhereOf(written);
            Dispatcher.BeginInvoke(new Action(() => NoteExcelWrite(written, false)));
            if (written.Failed) return "error\t" + problem + "（" + where + "）";
            return "ok\t書いた: " + where + "（" + written.Rows + " 行 × " + written.Columns + " 列"
                   + (padded > 0 ? "。列の足りない行を、空のセルで埋めた（" + padded + " セル。そこにあった中身は消えた）" : string.Empty) + "）。"
                   + "保存はしていない（保存は利用者がする）。Excel の Ctrl+Z では戻らない";
        }

        private static string WhereOf(ExcelBridge.Written written)
        {
            return Path.GetFileName(written.Book) + " › " + written.Sheet + "!" + written.Address;
        }

        /// <summary>書いたことを知らせ、一覧の「※」（保存していない変更）を付け直す（画面のスレッドで）。late は、時間切れのあと Excel が遅れて応えて書けた分。</summary>
        private void NoteExcelWrite(ExcelBridge.Written written, bool late)
        {
            var where = WhereOf(written);
            if (late)
                Status(written.Failed
                    ? "Excel が遅れて応えましたが、Claude が頼んでいた分を書く途中で失敗しました: " + where + "（途中まで書けているかもしれません。Claude には「応答しない」と返していました）"
                    : "Excel が遅れて応え、Claude が頼んでいた分を書きました: " + where + "（Claude には「応答しない」と返していました。保存はしていません）");
            else
                Status(written.Failed
                    ? "Claude が Excel に書こうとして失敗しました: " + where + "（途中まで書けているかもしれません）"
                    : "Claude が Excel に書きました: " + where + "（保存はしていません）");
            RefreshExcelList();
        }

        // ---------------------------------------------------------------
        // Claude の COM の台本（excel.ps1 -Script。2026-11-25。利用者の判断）
        //
        // Claude が Excel の COM を使う PowerShell の台本を書き、Tsuzuri が中身を確かめてから、別の PowerShell のプロセスで動かす。
        // 保存・閉じる・Excel を終える呼び出しを含む台本は動かさない（保存は利用者がする。保存しなければ、閉じるときに保存せずに閉じて元に戻せるため。
        // 閉じると、利用者の保存していない作業まで消えるため）。中身の確かめは語で見るだけなので、書き方を変えれば抜けられる（誤って保存するのを防ぐためのもの）。
        // 別のプロセスで動かすのは、COM の参照を持ち続けないため（プロセスが終われば全部手放される）と、止まっても Tsuzuri を巻き込まないため。
        // 動かしている間は、Excel を呼ぶほかの処理（一覧・読む・書く）を待たせる（ExcelBridge.RunExclusive）。
        // ---------------------------------------------------------------

        /// <summary>
        /// 台本に含んではいけない語（大文字・小文字を区別しない。語の一部でも）。保存（save を含む語。Saved・AutoSave も）・閉じる・Excel を終える・
        /// ブックを読み込み直す（保存していない変更が消える）・プロセスを止める・キーを送る（Ctrl+S を送れば保存になる）。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ExcelForbidden = new System.Text.RegularExpressions.Regex(
            // DisplayAlerts・.Open…・.Run・ExecuteExcel4Macro（2026-11-25。利用者の判断）: 確かめを消して保存していない変更を捨てる・開いているブックを開き直す・
            // ブックやファイルを開く・マクロを動かすのを、台本でさせない（AI に渡さない場所のブックを開いて読む道にもなるため）
            @"save|\bclose\b|\bquit\b|changefileaccess|stop-process|taskkill|sendkeys|\bkill\b|displayalerts|\.open|\.run\b|\.runautomacros|executeexcel4macro"
            // 開く・動かすほかの道（パスを渡した Workbooks.Add・FollowHyperlink・Invoke-Item・Start-Process）と、メソッドの名前を文字や変数にした呼び出し（.'Open'(…)・.$m(…)）
            + @"|workbooks\s*\.\s*add\s*\(\s*[^)\s]|followhyperlink|invoke-item|start-process|\.\s*['""]\s*\w+\s*['""]\s*\(|\.\s*\$\w+\s*\(",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>台本の出力として返す字数の上限。</summary>
        private const int ExcelScriptOutput = 20000;

        /// <summary>台本を待つ秒の上限（Claude の Bash ツールの上限 600 秒より、Gate を待つ分・止める分だけ短く）。</summary>
        private const int ExcelScriptMaxSeconds = 540;

        /// <summary>包みが「Excel が動いていない」ときに使う終わりの番号（台本自身の exit と取り違えにくい番号）。</summary>
        private const int ExcelNotRunningExit = 87;

        /// <summary>包みが「台本が決まりの外の書き方をしている（excel-guard.ps1 が断った）」ときに使う終わりの番号。</summary>
        private const int ExcelRefusedExit = 86;

        /// <summary>excel.ps1 が書く・台本の頼みの最後に付ける終わりの印（送る途中で切れた頼みを、そろった頼みと取り違えないため。サーバーは改行が来なくても切れ目までを1行として読む）。</summary>
        private const string ExcelRequestEnd = "end";

        /// <summary>
        /// 台本の書き方の確かめ（2026-11-26。利用者の判断）。台本は利用者の権限で囲いの外で動くので、語で見るだけでは足りない（'Sa'+'ve'・[IO.File]::Delete・&amp; cmd など）。
        /// PowerShell の構文解析で、許した形（変数・制御文・$excel からたどる COM・決まった名前のメソッド・一部のコマンドと型）だけを通す。中身は Ai/excel-guard.ps1（Tsuzuri と AiLink で同じファイル）。
        /// </summary>
        private static readonly string ExcelGuardFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ai", "excel-guard.ps1");

        /// <summary>
        /// パイプの「excel-script→台本→待つ秒」（どれも Base64）。答えは「ok→出力」「error→わけ」で、どちらも Base64（台本の出力は何行にもなるため）。
        /// </summary>
        private string ServeExcelScript(string request)
        {
            var parts = request.Split('\t');
            string script;
            var seconds = 60;
            try
            {
                script = parts.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])) : string.Empty;
                int asked;
                if (parts.Length > 2 && int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])), out asked) && asked > 0)
                    seconds = Math.Min(asked, ExcelScriptMaxSeconds);
            }
            catch (FormatException)
            {
                return ExcelScriptAnswer(false, "頼みを読めなかった。何もしていない");
            }
            // 終わりの印が無ければ、途中で切れた頼み。途中までの台本を動かさない
            if (parts.Length != 4 || parts[3] != ExcelRequestEnd) return ExcelScriptAnswer(false, "頼みが途中で切れていた（終わりの印が無い）。何もしていない");
            if (script.Trim().Length == 0) return ExcelScriptAnswer(false, "台本が空。何もしていない");

            var forbidden = ExcelForbidden.Match(script);
            if (forbidden.Success)
                return ExcelScriptAnswer(false, "保存・閉じる・Excel を終える・読み込み直す・プロセスを止める・キーを送る・確かめを消す・開く・マクロを動かす呼び出しを含む台本は動かさない（「" + forbidden.Value + "」を含んでいる。"
                                                + "save を含む語（Saved・AutoSave・Unsaved なども）・Close・Quit・ChangeFileAccess・Stop-Process・taskkill・SendKeys・Kill・DisplayAlerts・"
                                                + ".Open で始まる呼び出し（Workbooks.Open・OpenText など）・.Run・RunAutoMacros・ExecuteExcel4Macro・パスを渡した Workbooks.Add・FollowHyperlink・Invoke-Item・Start-Process の語と、"
                                                + "メソッドの名前を文字や変数にした呼び出し（.'Open'(…)・.$m(…)）は、"
                                                + "コメントや文字・名前の一部でも使えない）。保存とブックを閉じるのは利用者がする。セルに書く文字にこれらの語が入るなら、excel.ps1 -Write で書く。何もしていない");
            // AI に渡さない場所に触れる台本は動かさない（台本は Claude のツールのフックを通らないので、Bash のコマンドと同じ確かめをここでする）
            var guard = AiGuard.Check("Bash", AiFolder, new[] { new KeyValuePair<string, string>("command", script) });
            if (guard != null) return ExcelScriptAnswer(false, guard + "何もしていない");

            bool started;
            string answer = null;
            // 台本のファイルは、Excel への頼みを1つずつ流す中で、頼みごとに別の名前で置く（同時に来た頼みで中身が入れ替わらないように）
            var waited = ExcelBridge.RunExclusive(() => answer = RunExcelScript(script, seconds), 10000, out started);
            if (!started) return ExcelScriptAnswer(false, waited);
            var failed = answer.StartsWith("error\t", StringComparison.Ordinal);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Status(failed ? "Claude の Excel の台本が、エラーか時間切れで止まりました（途中まで操作されているかもしれません。保存はしていません）"
                              : "Claude が Excel の台本を動かしました（保存はしていません）");
                RefreshExcelList();   // 「※」（保存していない変更）を付け直す
            }));
            return answer;
        }

        private static string ExcelScriptAnswer(bool ok, string message)
        {
            return (ok ? "ok\t" : "error\t") + Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        }

        /// <summary>台本を別の PowerShell のプロセスで動かし、出力を返す。時間を過ぎたら止める。置いたファイルは終わったら消す。</summary>
        private static string RunExcelScript(string script, int seconds)
        {
            var id = Guid.NewGuid().ToString("N");
            var wrapper = Path.Combine(AiFolder, "excel-script-run-" + id + ".ps1");
            try
            {
                // 書き方の確かめは包みの中に写す（置いたファイルを読み込まない）。台本の文字も包みに埋め、確かめたその文字をそのまま動かす（確かめたあとに差し替えられないように）
                var guard = File.ReadAllText(ExcelGuardFile, Encoding.UTF8);
                // PowerShell 5.1 は BOM の無い台本を ANSI の文字コードで読むので、BOM を付けて書く（日本語のシート名・文字を化けさせない）
                File.WriteAllText(wrapper,
                    "$ErrorActionPreference = 'Stop'\r\n"
                    + "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n"
                    + guard + "\r\n"
                    // 台本の文字は包みの中に埋める（ファイルから読み直すと、C# で確かめたあとに差し替えられる）
                    + "$__text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + "'))\r\n"
                    + "$__problem = Test-ExcelScript $__text\r\n"
                    + "if ($__problem) { [Console]::Error.WriteLine($__problem); exit " + ExcelRefusedExit + " }\r\n"
                    + "try { $excel = [Runtime.InteropServices.Marshal]::GetActiveObject('Excel.Application') } catch { [Console]::Error.WriteLine('Excel is not running'); exit " + ExcelNotRunningExit + " }\r\n"
                    + "Remove-Item -Path Function:\\Test-ExcelScript\r\n"
                    + ". ([ScriptBlock]::Create($__text))\r\n",
                    new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                TryDelete(wrapper);
                return ExcelScriptAnswer(false, "台本を置けなかった: " + ex.Message + "。何もしていない");
            }

            var output = new StringBuilder();
            var gate = new object();
            var outputDone = new System.Threading.ManualResetEvent(false);
            var errorDone = new System.Threading.ManualResetEvent(false);
            System.Diagnostics.Process process = null;
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + wrapper + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                process = new System.Diagnostics.Process { StartInfo = info };
                process.OutputDataReceived += (sender, e) => Collect(e.Data, output, gate, outputDone);
                process.ErrorDataReceived += (sender, e) => Collect(e.Data, output, gate, errorDone);
                process.Start();
                // Tsuzuri が終わると一緒に終わるようにする（閉じたり落ちたりしたあとも、Excel をつかんだまま動き続けないように。2026-11-25）
                Tsuzuri.Views.PseudoConsole.AssignToAppJob(process.Handle);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(seconds * 1000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    process.WaitForExit(5000);
                    return ExcelScriptAnswer(false, "台本が " + seconds + " 秒で終わらなかったので止めた（Excel が応えない・台本が重いなど）。途中まで操作されているかもしれないので、読んで確かめる。"
                                                    + "長くかかる台本なら -Timeout で秒を延ばす（" + ExcelScriptMaxSeconds + " まで）。出力:\n" + Clipped(output, gate));
                }
                // 出力を読み終えるまで待つ。台本がほかのプログラムを起こして出力をつないだままだと終わりが来ないので、数秒で打ち切る
                System.Threading.WaitHandle.WaitAll(new System.Threading.WaitHandle[] { outputDone, errorDone }, 5000);
                var text = Clipped(output, gate);
                if (process.ExitCode == ExcelNotRunningExit) return ExcelScriptAnswer(false, "Excel が動いていない。何もしていない");
                if (process.ExitCode == ExcelRefusedExit)
                    return ExcelScriptAnswer(false, "台本を動かさなかった（台本で使えない書き方: " + text + "）。何もしていない。"
                                                    + "台本で使える形は、指示の「COM の台本」の段のとおり（決まった Excel のメンバーと決まったコマンド・型だけ。コレクションは変数に入れない。"
                                                    + "台本の中で作る表はハッシュ表にして $h['名前'] で読む）。返ったわけを見て、使える形で書き直す。使える形で書けない操作なら、利用者に伝える");
                if (process.ExitCode != 0)
                    return ExcelScriptAnswer(false, "台本がエラーで止まった（終わりの番号 " + process.ExitCode + "）。止まるまでの操作は Excel に残っているので、読んで確かめる。出力:\n" + text);
                return ExcelScriptAnswer(true, text.Length == 0 ? "台本が終わった（出力なし）。保存はしていない" : text);
            }
            catch (Exception ex)
            {
                return ExcelScriptAnswer(false, "台本を動かせなかった: " + ex.Message);
            }
            finally
            {
                if (process != null)
                {
                    // 起動したあとの途中で例外が出ても、台本を動かしたままにしない（Gate を返したあとも Excel を操作し続けないように。2026-11-26 のレビュー）
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    process.Dispose();
                }
                outputDone.Dispose();
                errorDone.Dispose();
                TryDelete(wrapper);
            }
        }

        private static void Collect(string line, StringBuilder output, object gate, System.Threading.ManualResetEvent done)
        {
            if (line == null)
            {
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                }
                return;
            }
            lock (gate)
            {
                if (output.Length <= ExcelScriptOutput) output.Append(line).Append('\n');
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception)
            {
            }
        }

        private static string Clipped(StringBuilder output, object gate)
        {
            lock (gate)
            {
                var text = output.ToString().TrimEnd('\n');
                return text.Length <= ExcelScriptOutput ? text : text.Substring(0, ExcelScriptOutput) + "\n…（長いので切った）";
            }
        }

        // ---------------------------------------------------------------
        // ランチャーの Excel の一覧
        // ---------------------------------------------------------------

        private ExcelListView _excelList;

        /// <summary>Excel と Outlook の一覧を切り替えるタブ（2026-11-26）。</summary>
        private OfficeTabs _officeTabs;
        private bool _excelListing;
        private bool _excelListPending;

        private System.Windows.Threading.DispatcherTimer _excelListSoon;

        /// <summary>少し待ってから一覧を読み直す（AI がツールを使うたびに読み直すと、AI の Excel の読み書きと取り合うため。続けて来た分は 1 回にまとめる）。</summary>
        private void RefreshExcelListSoon()
        {
            if (_excelListSoon == null)
            {
                _excelListSoon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _excelListSoon.Tick += delegate
                {
                    _excelListSoon.Stop();
                    RefreshExcelList();
                };
            }
            _excelListSoon.Stop();
            _excelListSoon.Start();
        }

        /// <summary>一覧を読み直す（裏で。ランチャーを出していなければ何もしない）。選んだシートが無くなっていれば外す。</summary>
        private void RefreshExcelList()
        {
            if (_launcher == null) return;
            if (_excelList == null)
            {
                _excelList = new ExcelListView();
                _excelList.RefreshRequested += RefreshExcelList;
                _excelList.NotesRequested += OpenExcelNotes;
                _excelList.RowClicked += ChooseExcelRow;
                _excelList.EmptyClicked += ClearExcelChoices;
                // Excel と Outlook はタブで切り替える（2026-11-26。利用者の判断。MainWindow.Outlook.cs）。Outlook のタブを初めて出したときに読む
                _officeTabs = new OfficeTabs(_excelList, Outlook.View);
                _officeTabs.Switched += index => { if (index == 1) Outlook.Refresh(false); };
                ExcelListHost.Child = _officeTabs;
            }
            if (_officeTabs.Current == 1) Outlook.Refresh(false);   // Outlook のタブを出していれば（まだ読めていなければ読む）
            if (_excelListing)
            {
                _excelListPending = true;
                return;
            }

            _excelListing = true;
            _excelListPending = false;
            string listProblem = null;
            HashSet<string> guarded = null;
            Task.Run(() =>
            {
                var found = ExcelBridge.ListBooks(5000, out listProblem);
                // AI に渡さない場所かは、ここ（裏）で決めておく（つながらない共有フォルダで、画面を止めないように）
                if (found != null)
                    guarded = new HashSet<string>(found.Where(book => book.Path != null && IsGuardedBook(book.Path)).Select(book => book.Path), StringComparer.OrdinalIgnoreCase);
                return found;
            }).ContinueWith(task =>
            {
                _excelListing = false;
                if (_excelListPending)
                {
                    RefreshExcelList();
                    return;
                }
                var books = task.IsFaulted ? null : task.Result;
                if (books != null)
                {
                    // 無くなったシート（閉じた・名前を変えた・保存していないブックの Excel を開き直した）は外す
                    var choices = _excelChoices;
                    var kept = choices.Where(choice => books.Any(book => SameExcelBook(book.Path ?? book.Name, choice.Book) && book.Sheets.Contains(choice.Sheet)
                                                                         && (book.Path != null || choice.Instance == 0 || book.Instance == choice.Instance))).ToArray();
                    if (kept.Length < choices.Length)
                    {
                        Status("選んでいた Excel のシートが見つからなくなったので、外しました: "
                               + string.Join("、", choices.Except(kept).Select(choice => Path.GetFileName(choice.Book) + " › " + choice.Sheet)));
                        _excelChoices = kept;
                    }
                    _lastExcelBooks = books;
                    _excelGuarded = guarded;
                }
                _excelListProblem = task.IsFaulted ? task.Exception.GetBaseException().Message : listProblem;
                ShowExcelList(books);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>最後に読めた一覧（押したときに出し直すため。Excel を読み直さずに済ませる）。</summary>
        private List<ExcelBridge.Book> _lastExcelBooks;

        /// <summary>最後に一覧を読めなかったわけ（一覧に出す）。</summary>
        private string _excelListProblem;

        /// <summary>最後に読めた一覧のうち、AI に渡さない場所のブック（フルパス）。</summary>
        private HashSet<string> _excelGuarded;

        private void ShowExcelList(List<ExcelBridge.Book> books)
        {
            if (_excelList == null) return;
            var guarded = _excelGuarded;
            var choices = _excelChoices;
            _excelList.Show(books, path => path != null && guarded != null && guarded.Contains(path),
                            (book, sheet) => choices.Any(choice => SameExcelBook(choice.Book, book.Path ?? book.Name) && string.Equals(choice.Sheet, sheet, StringComparison.OrdinalIgnoreCase)
                                                                 && (book.Path != null || SameExcelInstance(choice.Book, choice.Instance, book.Instance))),
                            _excelListProblem);
        }

        /// <summary>Shift で範囲を選ぶときの起点: 最後に Shift なしで押した行のブック。null なら起点なし。</summary>
        private string _excelAnchorBook;

        /// <summary>起点のシート。null ならブックの行（そのブックのシート全部）。</summary>
        private string _excelAnchorSheet;

        /// <summary>起点のブックの Excel のプロセスの番号。</summary>
        private long _excelAnchorInstance;

        /// <summary>一覧の並びの、選べるシート（AI に渡さない場所のブックを除く）。</summary>
        private List<ExcelChoice> SelectableExcelSheets()
        {
            var all = new List<ExcelChoice>();
            var books = _lastExcelBooks;
            var guarded = _excelGuarded;
            if (books == null) return all;
            foreach (var book in books)
            {
                if (book.Path != null && guarded != null && guarded.Contains(book.Path)) continue;
                foreach (var sheet in book.Sheets) all.Add(new ExcelChoice(book.Path ?? book.Name, sheet, book.Instance));
            }
            return all;
        }

        /// <summary>行が並びのどこからどこまでか（シートなら1枚、ブックならそのシート全部）。無ければ false。</summary>
        private static bool ExcelRowSpan(List<ExcelChoice> all, string book, string sheet, long instance, out int first, out int last)
        {
            first = -1;
            last = -1;
            if (book == null) return false;
            for (var i = 0; i < all.Count; i++)
            {
                // 番号も比べる（別々の Excel に同じ名前の未保存のブックがあると、名前だけでは2冊にまたがり、間のブックまで範囲に入る）
                if (!SameExcelBook(all[i].Book, book) || all[i].Instance != instance) continue;
                if (sheet != null && !string.Equals(all[i].Sheet, sheet, StringComparison.OrdinalIgnoreCase)) continue;
                if (first < 0) first = i;
                last = i;
            }
            return first >= 0;
        }

        private static bool SameExcelChoice(ExcelChoice left, ExcelChoice right)
        {
            return SameExcelBook(left.Book, right.Book) && string.Equals(left.Sheet, right.Sheet, StringComparison.OrdinalIgnoreCase)
                   && SameExcelInstance(left.Book, left.Instance, right.Instance);
        }

        /// <summary>
        /// 同じ Excel のブックか（保存していない新しいブックだけ番号を比べる。別々の Excel に同じ名前の Book1 があっても取り違えないように。
        /// 保存したブックはフルパスで決まり、Excel を開き直して番号が変わっても同じブック）。番号が分からない（0）ときは同じとみなす。
        /// </summary>
        private static bool SameExcelInstance(string book, long left, long right)
        {
            if (left == 0 || right == 0 || left == right) return true;
            return book != null && (book.IndexOf('\\') >= 0 || book.IndexOf('/') >= 0);
        }

        /// <summary>
        /// シートかブックの行を押した（2026-11-26。利用者の判断。選び方はエクスプローラーと同じ）。sheet が null ならブックの行で、そのブックのシート全部として扱う
        /// （2026-11-25。シートがとても多いブックを、1枚ずつ押さずに渡すため）。
        /// そのまま押す: それだけを選ぶ（ほかは外れる）。Ctrl: 足す（全部選んでいれば外す）。Shift: 起点からの範囲だけを選ぶ。Ctrl＋Shift: 起点からの範囲を足す。
        /// 起点は Shift なしで最後に押した行（起点が一覧に無ければ、押した行を起点にする。Shift ならその行だけ、Ctrl＋Shift ならその行を足す）。AI に渡さない場所のブックは選ばない（範囲にも入れない）。Excel の側は何も切り替えない。
        /// </summary>
        private void ChooseExcelRow(string book, string sheet, long instance, bool control, bool shift)
        {
            var all = SelectableExcelSheets();
            int first, last;
            if (!ExcelRowSpan(all, book, sheet, instance, out first, out last)) return;

            var choices = _excelChoices;
            int anchorFirst, anchorLast;
            List<ExcelChoice> picked;
            if (shift)
            {
                // 起点が一覧に無ければ、押した行を起点にする（その行だけの範囲）
                if (!ExcelRowSpan(all, _excelAnchorBook, _excelAnchorSheet, _excelAnchorInstance, out anchorFirst, out anchorLast))
                {
                    anchorFirst = first;
                    anchorLast = last;
                    _excelAnchorBook = book;
                    _excelAnchorSheet = sheet;
                    _excelAnchorInstance = instance;
                }
                var from = Math.Min(first, anchorFirst);
                var to = Math.Max(last, anchorLast);
                var range = all.GetRange(from, to - from + 1);
                picked = control ? choices.ToList() : new List<ExcelChoice>();
                picked.AddRange(range.Where(item => !picked.Any(choice => SameExcelChoice(choice, item))));
            }
            else
            {
                var target = all.GetRange(first, last - first + 1);
                if (control)
                {
                    var allChosen = target.All(item => choices.Any(choice => SameExcelChoice(choice, item)));
                    picked = allChosen
                        ? choices.Where(choice => !target.Any(item => SameExcelChoice(choice, item))).ToList()
                        : choices.Concat(target.Where(item => !choices.Any(choice => SameExcelChoice(choice, item)))).ToList();
                }
                else
                {
                    picked = target;
                }
                _excelAnchorBook = book;
                _excelAnchorSheet = sheet;
                _excelAnchorInstance = instance;
            }

            _excelChoices = picked.ToArray();
            ShowExcelChoiceStatus();
            ShowExcelList(_lastExcelBooks);
        }

        /// <summary>一覧の何もない所を押した: 選んでいるシートを全部外す。</summary>
        private void ClearExcelChoices()
        {
            _excelAnchorBook = null;
            _excelAnchorSheet = null;
            if (_excelChoices.Length == 0) return;
            _excelChoices = NoExcelChoices;
            Status("Excel のシートを全部外しました。");
            ShowExcelList(_lastExcelBooks);
        }

        private void ShowExcelChoiceStatus()
        {
            var choices = _excelChoices;
            if (choices.Length == 0)
            {
                Status("Excel のシートを全部外しました。");
                return;
            }
            var books = choices.Select(choice => choice.Book).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Status("Excel のシートを選んでいます: "
                   + (choices.Length == 1 ? Path.GetFileName(choices[0].Book) + " › " + choices[0].Sheet : books + " 冊の " + choices.Length + " 枚")
                   + "（話しかけると、名前を Claude に渡します）");
        }

        // ---------------------------------------------------------------
        // Excel で AI にいつも伝えること（2026-11-26。利用者の考え。中身は Office/OfficeNotes.cs。Outlook と共通）
        // ---------------------------------------------------------------

        private static readonly OfficeNotes ExcelNotes = new OfficeNotes("Tsuzuri", "Excel", "Claude");

        /// <summary>一覧の一番下の「Excel で AI にいつも伝えること」: ファイルを（無ければ空で作って）メモ帳で開く。</summary>
        private void OpenExcelNotes()
        {
            Status(ExcelNotes.Open());
        }

        /// <summary>AI に渡す指示の最後に足す段（ファイルが無い・空なら空の文字）。</summary>
        private static string ExcelNotesSection()
        {
            return ExcelNotes.Section();
        }
    }
}
