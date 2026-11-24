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
    // ・Claude が書く（excel.ps1 -Write → パイプの excel-write）。書けるのは一覧で選んだシートだけ。保存は利用者がする。
    //   書く前の中身を控え、一覧の上の「戻す」で戻せる（外から書いた分は Excel の Ctrl+Z では戻らないため。書く途中で失敗したときも）。
    // COM を使うのは Office/ExcelBridge.cs だけ。
    public partial class MainWindow
    {
        /// <summary>一覧で選んだシート（ブックはフルパスか、保存していなければ名前）。選んでいなければ null。パイプのスレッドからも読むので、組で入れ替える。</summary>
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

        /// <summary>Claude が書いた分（新しいものが後ろ）。一覧の上の「戻す」で、新しいものから戻す。画面ごと。</summary>
        private readonly List<ExcelBridge.Written> _excelWrites = new List<ExcelBridge.Written>();
        private const int ExcelUndoLimit = 20;

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

            if (_excelUndoing) return "error\t利用者がランチャーの Excel の一覧の「戻す」で戻している途中なので、書かない。何も書いていない。終わってから、書いてよいかを利用者に確かめる";

            var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);   // 最後の改行・空行（知らないうちに下のセルを空にしない）
            var cells = lines.Select(line => line.Split('\t')).ToList();
            var width = cells.Count > 0 ? cells.Max(row => row.Length) : 0;
            var padded = cells.Sum(row => width - row.Length);   // 列の足りない行を空のセルで埋める数

            string problem;
            var written = ExcelBridge.Write(choice.Book, choice.Sheet, at, cells, choice.Instance, 10000,
                late => Dispatcher.BeginInvoke(new Action(() => RecordExcelWrite(late, true))), out problem);
            if (written == null)
                return "error\t" + (problem ?? "書けたかどうか分からない。excel.ps1 で読んで確かめる")
                       + (problem != null && problem.StartsWith("そのブックは Excel で開いていない", StringComparison.Ordinal)
                           ? "（選んだシートのブックが閉じられたかもしれない。利用者に一覧で選び直してもらう）" : string.Empty);

            var where = WhereOf(written);
            Dispatcher.BeginInvoke(new Action(() => RecordExcelWrite(written, false)));
            if (written.Failed)
                return "error\t" + problem + "（" + where + "。利用者はランチャーの Excel の一覧の「戻す」で、書く前に戻せる）";
            return "ok\t書いた: " + where + "（" + written.Rows + " 行 × " + written.Columns + " 列"
                   + (padded > 0 ? "。列の足りない行を、空のセルで埋めた（" + padded + " セル。そこにあった中身は消えた）" : string.Empty) + "）。"
                   + "保存はしていない（保存は利用者がする）。利用者はランチャーの Excel の一覧の「戻す」で、書く前に戻せる（Excel の Ctrl+Z では戻らない）";
        }

        private static string WhereOf(ExcelBridge.Written written)
        {
            return Path.GetFileName(written.Book) + " › " + written.Sheet + "!" + written.Address;
        }

        /// <summary>書いた分を「戻す」の記録に足す（画面のスレッドで）。late は、時間切れのあと Excel が遅れて応えて書けた分。</summary>
        private void RecordExcelWrite(ExcelBridge.Written written, bool late)
        {
            _excelWrites.Add(written);
            if (_excelWrites.Count > ExcelUndoLimit) _excelWrites.RemoveAt(0);
            PaintExcelUndo();
            var where = WhereOf(written);
            if (late)
                Status(written.Failed
                    ? "Excel が遅れて応えましたが、Claude が頼んでいた分を書く途中で失敗しました: " + where + "（途中まで書けているかもしれません。Claude には「応答しない」と返していました。Excel の一覧の「戻す」で戻せます）"
                    : "Excel が遅れて応え、Claude が頼んでいた分を書きました: " + where + "（Claude には「応答しない」と返していました。Excel の一覧の「戻す」で戻せます）");
            else
                Status(written.Failed
                    ? "Claude が Excel に書こうとして失敗しました: " + where + "（途中まで書けているかもしれません。Excel の一覧の「戻す」で書く前に戻せます）"
                    : "Claude が Excel に書きました: " + where + "（Excel の一覧の「戻す」で戻せます。保存はしていません）");
        }

        /// <summary>戻している途中（確かめのダイアログを出している間も）。その間は「戻す」を押せず、Claude の書き込みも断る（パイプのスレッドからも読む）。</summary>
        private volatile bool _excelUndoing;

        private void PaintExcelUndo()
        {
            if (_excelList == null) return;
            if (_excelUndoing)
            {
                _excelList.SetUndo(false, "戻している途中（Excel が応えるのを待っている）");
                return;
            }
            var last = _excelWrites.Count > 0 ? _excelWrites[_excelWrites.Count - 1] : null;
            if (last == null)
            {
                _excelList.SetUndo(false, "Claude が書いた分を戻す（まだ書いていない）");
                return;
            }
            _excelList.SetUndo(true, "Claude が最後に書いた分を、書く前に戻す: " + WhereOf(last)
                               + (_excelWrites.Count > 1 ? "（ほかに " + (_excelWrites.Count - 1) + " 回分。押すたびに新しいほうから戻す）" : string.Empty));
        }

        /// <summary>記録の一番上がまだ last か（確かめのダイアログを出している間に、記録が変わっていないか）。</summary>
        private bool StillLastExcelWrite(ExcelBridge.Written last)
        {
            return _excelWrites.Count > 0 && ReferenceEquals(_excelWrites[_excelWrites.Count - 1], last);
        }

        /// <summary>
        /// 一覧の上の「戻す」: Claude が最後に書いた分を、書く前の中身に戻す。
        /// 確かめのダイアログを出す前から終わるまで _excelUndoing を立て、その間は Claude の書き込みを断る（戻す順が崩れないように）。
        /// Excel が時間内に応えなければ、終わるまで（遅れて戻ったか、戻さなかったか）ボタンを押せないままにする。
        /// </summary>
        private void UndoExcelWrite()
        {
            if (_excelWrites.Count == 0 || _excelUndoing) return;
            var last = _excelWrites[_excelWrites.Count - 1];
            var where = WhereOf(last);
            _excelUndoing = true;
            PaintExcelUndo();
            try
            {
                if (MessageBox.Show(this, "Claude が書いた " + where + " を、書く前の中身に戻しますか？\n\n（値と式を戻します。表示形式（日付・% など）や、マクロ・テーブルの広がりで変わったほかのセルは戻りません）", "Excel を戻す",
                                    MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK
                    )
                {
                    EndExcelUndo(null);
                    return;
                }
                if (!StillLastExcelWrite(last))
                {
                    EndExcelUndo("その間に Claude が書いたので、戻すのをやめました。もう一度押してください");
                    return;
                }
                RunExcelUndo(last, where, false);
            }
            catch (Exception ex)
            {
                EndExcelUndo("Excel を戻せませんでした: " + ex.Message);
            }
        }

        /// <summary>裏で戻し、結果を画面のスレッドで受ける。</summary>
        private void RunExcelUndo(ExcelBridge.Written last, string where, bool force)
        {
            // 遅れて届いた結果（late）が、時間切れの知らせより先に画面のスレッドに来ることがある。そのときは時間切れの知らせを出さない（どちらも画面のスレッドで動く）
            var finished = new bool[1];
            Action<ExcelBridge.UndoResult> late = result => Dispatcher.BeginInvoke(new Action(() =>
            {
                finished[0] = true;
                FinishExcelUndo(last, where, result, true);
            }));
            Task.Run(() => ExcelBridge.Undo(last, force, 10000, late)).ContinueWith(task =>
            {
                var result = task.IsFaulted ? new ExcelBridge.UndoResult { Kind = ExcelBridge.UndoKind.Failed, Problem = "戻せなかった: " + task.Exception.GetBaseException().Message } : task.Result;
                if (result.Kind == ExcelBridge.UndoKind.NotAnswering && finished[0]) return;
                FinishExcelUndo(last, where, result, false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void EndExcelUndo(string status)
        {
            _excelUndoing = false;
            PaintExcelUndo();
            if (status != null) Status(status);
        }

        private void FinishExcelUndo(ExcelBridge.Written last, string where, ExcelBridge.UndoResult result, bool late)
        {
            try
            {
                switch (result.Kind)
                {
                    case ExcelBridge.UndoKind.Done:
                        _excelWrites.Remove(last);
                        EndExcelUndo((late ? "Excel が遅れて応え、書く前に戻しました: " : "Excel を書く前に戻しました: ") + where + "（保存はしていません）");
                        return;

                    case ExcelBridge.UndoKind.NotAnswering:
                        // まだ終わっていない。終わったら late で来るので、それまで押せないままにする
                        Status("Excel が応答しないので、戻せたかどうか分かりません（セルの編集・ダイアログを終えると、戻すか、戻さずにやめます。そのとき知らせます）: " + where);
                        PaintExcelUndo();
                        return;

                    case ExcelBridge.UndoKind.Busy:
                        EndExcelUndo("Excel を戻していません（" + result.Problem + "）。セルの編集・ダイアログを終えてから、もう一度押してください");
                        return;

                    case ExcelBridge.UndoKind.Changed:
                    {
                        if (!StillLastExcelWrite(last))
                        {
                            EndExcelUndo("その間に Claude が書いたので、戻すのをやめました。もう一度押してください");
                            return;
                        }
                        var detail = last.Failed ? "書く途中で失敗したので、" + where + " が書いた直後のままかを確かめられません。"
                            : result.Differ < 0 ? "書いた直後の中身を読めなかったので、" + where + " が書いた直後のままかを確かめられません。"
                            : "Claude が書いたあとに、" + where + " の " + result.Cells + " セルのうち " + result.Differ + " セルが変わっています（手で直したか、行・列を足したり消したりして場所がずれたかもしれません）。";
                        var answer = MessageBox.Show(this, detail + "\n\n"
                                                     + "はい: それでも書く前に戻す（あとで変えた分は消えます。行・列を足したり消したりしていたら、ずれた所に書くので押さない）\n"
                                                     + "いいえ: 戻さずに、この記録を捨てる（その前に Claude が書いた分を戻せるようになります）\n"
                                                     + "キャンセル: 何もしない", "Excel を戻す",
                                                     MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                        if (!StillLastExcelWrite(last))
                        {
                            EndExcelUndo("その間に Claude が書いたので、戻すのをやめました。もう一度押してください");
                            return;
                        }
                        if (answer == MessageBoxResult.Yes)
                        {
                            RunExcelUndo(last, where, true);
                            return;
                        }
                        if (answer == MessageBoxResult.No)
                        {
                            _excelWrites.Remove(last);
                            EndExcelUndo("戻さずに、記録を捨てました: " + where);
                            return;
                        }
                        EndExcelUndo(null);
                        return;
                    }

                    default:   // Gone・Failed: 残すと前の回も戻せなくなるので、捨てるかを尋ねる
                    {
                        Status("Excel を戻せませんでした: " + result.Problem);
                        if (StillLastExcelWrite(last)
                            && MessageBox.Show(this, where + " は戻せません（" + result.Problem + "）。\n\nこの記録を捨てて、その前に Claude が書いた分を戻せるようにしますか？", "Excel を戻す",
                                               MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
                            _excelWrites.Remove(last);
                        EndExcelUndo(null);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                EndExcelUndo("Excel を戻せませんでした: " + ex.Message);
            }
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
                _excelList.UndoRequested += UndoExcelWrite;
                ExcelListHost.Child = _excelList;
                PaintExcelUndo();
            }
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
                // AI に渡さない場所かは、ここ（裏）で決めておく（つながらないネットの場所で、画面を止めないように）
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
                var choice = _excelChoice;
                if (books != null && choice != null
                    && !books.Any(book => string.Equals(book.Path ?? book.Name, choice.Book, StringComparison.OrdinalIgnoreCase) && book.Sheets.Contains(choice.Sheet)
                                          && (book.Path != null || choice.Instance == 0 || book.Instance == choice.Instance)))   // 保存していないブックは、同じ Excel のものだけ
                {
                    Status("選んでいた Excel のシートが見つからなくなったので、外しました: " + Path.GetFileName(choice.Book) + " › " + choice.Sheet);
                    _excelChoice = null;
                }
                if (books != null)
                {
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
            var guarded = _excelGuarded;
            if (_excelList != null)
                _excelList.Show(books, path => path != null && guarded != null && guarded.Contains(path), _excelBook, _excelSheet, _excelListProblem);
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
                var listed = _lastExcelBooks != null ? _lastExcelBooks.FirstOrDefault(item => string.Equals(item.Path ?? item.Name, book, StringComparison.OrdinalIgnoreCase)) : null;
                _excelChoice = new ExcelChoice(book, sheet, listed != null ? listed.Instance : 0);
                Status("Excel のシートを選びました: " + Path.GetFileName(book) + " › " + sheet + "（話しかけると、ブックとシートの名前を Claude に渡します）");
            }
            ShowExcelList(_lastExcelBooks);
        }
    }
}
