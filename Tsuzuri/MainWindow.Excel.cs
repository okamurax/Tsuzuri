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
    // MainWindow の一部: 開いている Excel を読む（2026-11-24。利用者の考え。SPEC 19章・4章）。
    // ・ランチャーのファイラーの下の段に、開いているブックとシートの一覧。シートを押すと選び、もう一度押すと外す（初めは何も選んでいない）。
    // ・話しかけるたびの今の状態に「Excel」の段を書く: 選んだブックとシートの名前だけ（中身・Excel で前に出ているもの・保存したかは渡さない。利用者の判断。
    //   手で作業しているシートが AI に扱ってほしいシートとは限らず、読むところは利用者が話しかけて指定するため）。話しかけるたびに Excel は読まない。
    // ・Claude が範囲を指定して読む（excel.ps1 → パイプの excel-read。結果はファイルに書いてパスを返す）。ブック・シートを付けなければ、選んだシート。
    // ・Claude が書く（excel.ps1 -Write → パイプの excel-write）。書けるのは一覧で選んだシートだけ。保存は利用者がする。戻す仕組みは持たない（利用者の判断）。
    // COM を使うのは Office/ExcelBridge.cs だけ。
    public partial class MainWindow
    {
        /// <summary>一覧で選んだシート（ブックはフルパスか、保存していなければ名前）。選んでいなければ null。パイプのスレッドからも読むので、組で入れ替える。</summary>
        private sealed class ExcelChoice
        {
            public readonly string Book;
            public readonly string Sheet;

            public ExcelChoice(string book, string sheet)
            {
                Book = book;
                Sheet = sheet;
            }
        }

        private volatile ExcelChoice _excelChoice;

        private string _excelBook
        {
            get { var choice = _excelChoice; return choice != null ? choice.Book : null; }
        }

        private string _excelSheet
        {
            get { var choice = _excelChoice; return choice != null ? choice.Sheet : null; }
        }

        /// <summary>
        /// AI に渡さないブックか。OneDrive・SharePoint などの https の場所は、目印のフォルダ（AiGuard）を確かめられないので渡さない側にする。
        /// </summary>
        private static bool IsGuardedBook(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            return Path.IsPathRooted(path) && AiGuard.IsGuarded(path);
        }

        /// <summary>今の状態に「Excel」の段を書く（選んだブックとシートの名前だけ。選んでいなくても、そう書く）。Excel は読まない。</summary>
        private void AppendExcelContext(StringBuilder text)
        {
            if (_excelBook == null)
            {
                text.Append("Excel: ランチャーの Excel の一覧でシートを選んでいない（前の頼みで選んでいても、今は外されている）\n");
                return;
            }
            if (IsGuardedBook(_excelBook))
            {
                text.Append("Excel: ランチャーの Excel の一覧で選んだシートは、AI に渡さない場所のブックにあるので、名前も渡していない。尋ねたり探したりしない\n");
                return;
            }
            text.Append("Excel: 利用者がランチャーの Excel の一覧で選んだシート（名前だけ。中身は渡していない）: ブック「").Append(_excelBook)
                .Append(_excelBook.IndexOf('\\') < 0 ? "（まだ保存していない新しいブック）" : string.Empty)
                .Append("」のシート「").Append(_excelSheet).Append("」\n");
        }

        /// <summary>AI の欄の下端の行に足す、Excel の分（「  ・Excel 見積.xlsx › 明細」）。</summary>
        private string ExcelSummaryPart()
        {
            if (_excelBook == null) return string.Empty;
            if (IsGuardedBook(_excelBook)) return "  ・Excel（AI に渡さないブック）";
            return "  ・Excel " + Path.GetFileName(_excelBook) + " › " + _excelSheet;
        }

        /// <summary>
        /// Claude が範囲を指定して読む（パイプの「excel-read→ブック→シート→範囲」。どれも Base64）。
        /// ブックを付けなければ一覧で選んだシート、ブックだけで選んだブックならそのシート。範囲が空なら使っている範囲全体。
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

            // 付けなかったところは、一覧で選んだシートで埋める（Excel で前に出ているものは使わない）
            var choice = _excelChoice;   // 一度で読む（選び替えの途中の、ブックとシートの食い違った組にしない）
            var chosenBook = choice != null ? choice.Book : null;
            var chosenSheet = choice != null ? choice.Sheet : null;
            var sameBook = chosenBook != null && (string.Equals(book, chosenBook, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(book, Path.GetFileName(chosenBook), StringComparison.OrdinalIgnoreCase));
            if (book.Length == 0)
            {
                if (chosenBook == null) return "error\tランチャーの Excel の一覧でシートが選ばれていない。ブックとシートの名前は推し量らず、利用者に一覧で選んでもらうか、名前を教えてもらう";
                book = chosenBook;
                if (sheet.Length == 0) sheet = chosenSheet;
            }
            else if (sheet.Length == 0)
            {
                if (!sameBook) return "error\tシートも付けて読む（-Sheet）";
                sheet = chosenSheet;
            }

            string bookShown, sheetShown, problem;
            var grid = ExcelBridge.ReadRange(book, sheet, range, 10000, IsGuardedBook, out bookShown, out sheetShown, out problem);
            if (grid == null)
                return "error\t" + (problem ?? "Excel を読めなかった。")
                       + (choice != null && book == chosenBook && problem != null && problem.StartsWith("そのブックは Excel で開いていない", StringComparison.Ordinal)
                           ? "（選んだシートのブックが閉じられたかもしれない。利用者に一覧で選び直してもらう）" : string.Empty);

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

            var file = Path.Combine(AiFolder, "excel-read.tsv");
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
        /// Claude が書く（パイプの「excel-write→ブック→シート→場所→中身」。どれも Base64）。書けるのは一覧で選んだシートだけ（ブック・シートを付けなければそのシート）。
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

            var choice = _excelChoice;
            if (choice == null) return "error\t書けるのは、利用者がランチャーの Excel の一覧で選んだシートだけ。今は選ばれていないので書けない。利用者に一覧で選んでもらう";
            var sameBook = book.Length == 0 || string.Equals(book, choice.Book, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(book, Path.GetFileName(choice.Book), StringComparison.OrdinalIgnoreCase);
            if (!sameBook || (sheet.Length > 0 && !string.Equals(sheet, choice.Sheet, StringComparison.Ordinal)))
                return "error\t書けるのは、利用者がランチャーの Excel の一覧で選んだシート（" + Path.GetFileName(choice.Book) + " › " + choice.Sheet + "）だけ。ほかのシートに書くなら、利用者に一覧で選び直してもらう";
            if (IsGuardedBook(choice.Book)) return "error\tAI に渡さない場所のブックには書かない";
            if (at.Length == 0) return "error\t書く場所（左上のセル。B3 の形）を -Range で付ける";

            var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);   // 最後の改行
            var cells = lines.Select(line => line.Split('\t')).ToList();

            string problem;
            var written = ExcelBridge.Write(choice.Book, choice.Sheet, at, cells, 10000, out problem);
            if (written == null) return "error\t" + (problem ?? "書けなかった。");

            Dispatcher.BeginInvoke(new Action(delegate
            {
                Status("Claude が Excel に書きました: " + Path.GetFileName(written.Book) + " › " + written.Sheet + "!" + written.Address + "（保存はしていません）");
            }));
            return "ok\t書いた: " + Path.GetFileName(written.Book) + " › " + written.Sheet + "!" + written.Address + "（" + written.Rows + " 行 × " + written.Columns + " 列）。"
                   + "保存はしていない（保存は利用者がする）";
        }

        // ---------------------------------------------------------------
        // ランチャーの Excel の一覧
        // ---------------------------------------------------------------

        private ExcelListView _excelList;
        private bool _excelListing;
        private bool _excelListPending;

        /// <summary>一覧を読み直す（裏で。ランチャーを出していなければ何もしない）。選んだシートが無くなっていれば外す。</summary>
        private void RefreshExcelList()
        {
            if (_launcher == null) return;
            if (_excelList == null)
            {
                _excelList = new ExcelListView();
                _excelList.RefreshRequested += RefreshExcelList;
                _excelList.SheetClicked += ChooseExcelSheet;
                ExcelListHost.Child = _excelList;
            }
            if (_excelListing)
            {
                _excelListPending = true;
                return;
            }

            _excelListing = true;
            _excelListPending = false;
            string listProblem = null;
            Task.Run(() => ExcelBridge.ListBooks(3000, out listProblem)).ContinueWith(task =>
            {
                _excelListing = false;
                if (_excelListPending)
                {
                    RefreshExcelList();
                    return;
                }
                var books = task.IsFaulted ? null : task.Result;
                if (books != null && _excelBook != null
                    && !books.Any(book => string.Equals(book.Path ?? book.Name, _excelBook, StringComparison.OrdinalIgnoreCase) && book.Sheets.Contains(_excelSheet)))
                {
                    Status("選んでいた Excel のシートが見つからなくなったので、外しました: " + Path.GetFileName(_excelBook) + " › " + _excelSheet);
                    _excelChoice = null;
                }
                _lastExcelBooks = books ?? _lastExcelBooks;
                _excelListProblem = task.IsFaulted ? task.Exception.GetBaseException().Message : listProblem;
                ShowExcelList(books);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>最後に読めた一覧（押したときに出し直すため。Excel を読み直さずに済ませる）。</summary>
        private List<ExcelBridge.Book> _lastExcelBooks;

        /// <summary>最後に一覧を読めなかったわけ（一覧に出す）。</summary>
        private string _excelListProblem;

        private void ShowExcelList(List<ExcelBridge.Book> books)
        {
            if (_excelList != null) _excelList.Show(books, IsGuardedBook, _excelBook, _excelSheet, _excelListProblem);
        }

        /// <summary>シートを押した: 選ぶ（選んでいたシートなら外す）。Excel の側は何も切り替えない。</summary>
        private void ChooseExcelSheet(string book, string sheet)
        {
            if (string.Equals(book, _excelBook, StringComparison.OrdinalIgnoreCase) && string.Equals(sheet, _excelSheet, StringComparison.Ordinal))
            {
                _excelChoice = null;
                Status("Excel のシートを外しました（Claude には渡しません）");
            }
            else
            {
                _excelChoice = new ExcelChoice(book, sheet);
                Status("Excel のシートを選びました: " + Path.GetFileName(book) + " › " + sheet + "（話しかけると、ブックとシートの名前を Claude に渡します）");
            }
            ShowExcelList(_lastExcelBooks);
        }
    }
}
