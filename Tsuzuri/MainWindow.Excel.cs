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
    //   AI に渡さない場所・ネットの場所・隠れたブックは扱わない。書く前に、Claude が利用者の許可を取る（AI に渡す文で指示する）。保存は利用者がする。
    // ・ランチャーのファイラーの下の段に、開いているブックとシートの一覧。シートを押すと選び、もう一度押すと外す。いくつでも選べる（初めは何も選んでいない）。
    //   選んだシートが、AI への「このシート」になる。話しかけるたびの今の状態に、選んだブックとシートの名前だけを書く（中身・Excel で前に出ているもの・保存したかは渡さない）。
    // ・Claude が範囲を指定して読む（excel.ps1 → パイプの excel-read）・書く（excel.ps1 -Write → パイプの excel-write）。
    //   読むときは、ブック・シートを付けなければ、選んだシートが1つのときだけそのシート（2つ以上なら付けさせる）。書くときは、どちらも付けさせる
    //   （付けなければ断る。許可をもらったあとに利用者が選び直しても、許可をもらったのと別のシートに書かないように）。
    // ・書く前の中身を控え、一覧の上の「戻す」で、選んでいるシートごとに Claude が最後に書いた分を戻す。Claude が書いて戻していないシートには、一覧で名前の右に「※」。
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

        /// <summary>記録がそのシートのものか（保存していないブックは、同じ Excel のものだけ）。</summary>
        private static bool ExcelWriteOn(ExcelBridge.Written written, string book, string sheet, long instance)
        {
            return SameExcelBook(written.Book, book) && string.Equals(written.Sheet, sheet, StringComparison.OrdinalIgnoreCase)   // Excel もシートの名前の大文字・小文字を区別しない
                   && (!UnsavedExcelBook(book) || written.Instance == 0 || instance == 0 || written.Instance == instance);
        }

        /// <summary>今の状態に「Excel」の段を書く（選んだブックとシートの名前だけ。選んでいなくても、そう書く）。Excel は読まない。</summary>
        private void AppendExcelContext(StringBuilder text)
        {
            var choices = _excelChoices;
            if (choices.Length == 0)
            {
                text.Append("Excel: ランチャーの Excel の一覧でシートを選んでいない（前の頼みで選んでいても、今は外されている）\n");
                return;
            }
            var shown = choices.Where(choice => !IsGuardedBook(choice.Book)).ToList();
            var hidden = choices.Length - shown.Count;
            if (shown.Count == 0)
            {
                text.Append("Excel: ランチャーの Excel の一覧で選んだシートは、AI に渡さない場所のブックにあるので、名前も渡していない。尋ねたり探したりしない\n");
                return;
            }
            text.Append("Excel: 利用者がランチャーの Excel の一覧で選んだシート（").Append(shown.Count).Append(" 枚。名前だけ。中身は渡していない）: ");
            for (var i = 0; i < shown.Count; i++)
            {
                if (i > 0) text.Append("、");
                text.Append("ブック「").Append(shown[i].Book)
                    .Append(UnsavedExcelBook(shown[i].Book) ? "（まだ保存していない新しいブック）" : string.Empty)
                    .Append("」のシート「").Append(shown[i].Sheet).Append("」");
            }
            if (hidden > 0) text.Append("（ほかに選んだ ").Append(hidden).Append(" 枚は、AI に渡さない場所のブックなので、名前も渡していない。尋ねたり探したりしない）");
            text.Append('\n');
        }

        /// <summary>AI の欄の下端の行に足す、Excel の分（「  ・Excel 見積.xlsx › 明細」。2つ以上なら枚数）。</summary>
        private string ExcelSummaryPart()
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

        /// <summary>Claude が書いた分（新しいものが後ろ）。一覧の上の「戻す」で、選んでいるシートごとに新しいものから戻す。画面ごと。</summary>
        private readonly List<ExcelBridge.Written> _excelWrites = new List<ExcelBridge.Written>();

        /// <summary>シートごとに控える回数。</summary>
        private const int ExcelUndoLimit = 20;

        /// <summary>
        /// Claude が書く（パイプの「excel-write→ブック→シート→場所→中身」。どれも Base64）。開いているブックのどのシートにも書ける
        /// （書いてよいかは、Claude が書く前に利用者に尋ねる。AI に渡す文の指示）。付けなかったブック・シートは ResolveExcelTarget で埋める。
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

            // 書くときは、ブックもシートも付けさせる（付けなかった分を今選んでいるシートで埋めると、許可をもらったあとに選び直されたとき、別のシートに書くため）
            if (book.Length == 0 || sheet.Length == 0)
                return "error\t書くときは -Book と -Sheet を両方付ける（許可をもらうときに示したブックとシート）。何も書いていない";
            long instance;
            var unresolved = ResolveExcelTarget(ref book, ref sheet, out instance);
            if (unresolved != null) return "error\t" + unresolved + "。何も書いていない";
            if (IsGuardedBook(book)) return "error\tAI に渡さない場所のブックには書かない。何も書いていない";
            if (at.Length == 0) return "error\t書く場所（左上のセル。B3 の形）を -Range で付ける。何も書いていない";

            if (_excelUndoing) return "error\t利用者がランチャーの Excel の一覧の「戻す」で戻している途中なので、書かない。何も書いていない。終わってから、書いてよいかを利用者に確かめる";

            var lines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);   // 最後の改行・空行（知らないうちに下のセルを空にしない）
            var cells = lines.Select(line => line.Split('\t')).ToList();
            var width = cells.Count > 0 ? cells.Max(row => row.Length) : 0;
            var padded = cells.Sum(row => width - row.Length);   // 列の足りない行を空のセルで埋める数

            string problem;
            var written = ExcelBridge.Write(book, sheet, at, cells, instance, IsGuardedBook, () => _excelUndoing, 10000,
                late => Dispatcher.BeginInvoke(new Action(() => RecordExcelWrite(late, true))), out problem);
            if (written == null) return "error\t" + (problem ?? "書けたかどうか分からない。excel.ps1 で読んで確かめる");

            var where = WhereOf(written);
            Dispatcher.BeginInvoke(new Action(() => RecordExcelWrite(written, false)));
            if (written.Failed)
                return "error\t" + problem + "（" + where + "。利用者はランチャーの Excel の一覧でそのシートだけを選んで「戻す」を押すと、書く前に戻せる）";
            return "ok\t書いた: " + where + "（" + written.Rows + " 行 × " + written.Columns + " 列"
                   + (padded > 0 ? "。列の足りない行を、空のセルで埋めた（" + padded + " セル。そこにあった中身は消えた）" : string.Empty) + "）。"
                   + "保存はしていない（保存は利用者がする）。利用者はランチャーの Excel の一覧でそのシートだけを選んで「戻す」を押すと、この書き込みを書く前に戻せる"
                   + "（あとにまた書けば、新しいほうから1回ずつ。Excel の Ctrl+Z では戻らない。Tsuzuri を閉じると控えは消える）";
        }

        private static string WhereOf(ExcelBridge.Written written)
        {
            return Path.GetFileName(written.Book) + " › " + written.Sheet + "!" + written.Address;
        }

        /// <summary>書いた分を「戻す」の記録に足す（画面のスレッドで）。late は、時間切れのあと Excel が遅れて応えて書けた分。</summary>
        private void RecordExcelWrite(ExcelBridge.Written written, bool late)
        {
            _excelWrites.Add(written);
            var onSheet = _excelWrites.Where(item => ExcelWriteOn(item, written.Book, written.Sheet, written.Instance)).ToList();
            if (onSheet.Count > ExcelUndoLimit) _excelWrites.Remove(onSheet[0]);   // そのシートのいちばん古い分
            PaintExcelUndo();
            ShowExcelList(_lastExcelBooks);   // 名前の右の「※」
            var where = WhereOf(written);
            const string how = "Excel の一覧でそのシートだけを選んで「戻す」を押すと戻せます";
            if (late)
                Status(written.Failed
                    ? "Excel が遅れて応えましたが、Claude が頼んでいた分を書く途中で失敗しました: " + where + "（途中まで書けているかもしれません。Claude には「応答しない」と返していました。" + how + "）"
                    : "Excel が遅れて応え、Claude が頼んでいた分を書きました: " + where + "（Claude には「応答しない」と返していました。" + how + "）");
            else
                Status(written.Failed
                    ? "Claude が Excel に書こうとして失敗しました: " + where + "（途中まで書けているかもしれません。" + how + "）"
                    : "Claude が Excel に書きました: " + where + "（" + how + "。保存はしていません）");
        }

        /// <summary>戻している途中（確かめのダイアログを出している間も）。その間は「戻す」を押せず、Claude の書き込みも断る（パイプのスレッドからも読む）。</summary>
        private volatile bool _excelUndoing;

        /// <summary>「戻す」の回の番号（遅れて届いた結果が、前の回のものなら捨てる）。</summary>
        private int _excelUndoSession;

        /// <summary>この回で戻すもの（選んでいるシートごとに、最後に書いた分）と、その結果。</summary>
        private readonly Queue<ExcelBridge.Written> _excelUndoQueue = new Queue<ExcelBridge.Written>();
        private readonly List<string> _excelUndoReport = new List<string>();

        /// <summary>選んでいるシートごとに、Claude が最後に書いた分（戻すもの）。</summary>
        private List<ExcelBridge.Written> ExcelUndoTargets()
        {
            var targets = new List<ExcelBridge.Written>();
            foreach (var choice in _excelChoices)
            {
                var newest = _excelWrites.LastOrDefault(item => ExcelWriteOn(item, choice.Book, choice.Sheet, choice.Instance));
                if (newest != null && !targets.Contains(newest)) targets.Add(newest);
            }
            return targets;
        }

        private void PaintExcelUndo()
        {
            if (_excelList == null) return;
            if (_excelUndoing)
            {
                _excelList.SetUndo(false, "戻している途中（Excel が応えるのを待っている）");
                return;
            }
            if (_excelWrites.Count == 0)
            {
                _excelList.SetUndo(false, "Claude が書いた分を戻す（まだ書いていない）");
                return;
            }
            // 一覧のどのシートにも当たらない記録（書いたあとに名前を変えた・別の名前で保存したなど）は戻せない。あることだけ知らせる
            var books = _lastExcelBooks;
            var orphans = books == null ? 0 : _excelWrites.Count(item => !books.Any(book => book.Sheets.Any(name => ExcelWriteOn(item, book.Path ?? book.Name, name, book.Instance))));
            var orphanNote = orphans > 0 ? "\n（ほかに、一覧のどのシートにも当たらない記録が " + orphans + " 回分ある。書いたあとにブックやシートの名前を変えた・閉じたなど。戻せない）" : string.Empty;
            var targets = ExcelUndoTargets();
            if (targets.Count == 0)
            {
                _excelList.SetUndo(false, (_excelChoices.Length == 0
                    ? "戻すシートを、下の一覧で選ぶ（Claude が書いて戻していないシートには「※」）"
                    : "選んでいるシートには、Claude が書いて戻していない分が無い（「※」の付いたシートを選ぶ）") + orphanNote);
                return;
            }
            _excelList.SetUndo(true, "選んでいるシートで、Claude が最後に書いた分を、書く前に戻す（シートごとに1回分。押すたびに新しいほうから）:\n"
                                     + string.Join("\n", targets.Select(WhereOf)) + orphanNote);
        }

        /// <summary>そのシートで、記録のいちばん新しいものがまだ written か（確かめのダイアログを出している間に、記録が変わっていないか）。</summary>
        private bool StillNewestExcelWrite(ExcelBridge.Written written)
        {
            return _excelWrites.Contains(written)
                   && ReferenceEquals(_excelWrites.Last(item => ExcelWriteOn(item, written.Book, written.Sheet, written.Instance)), written);
        }

        /// <summary>
        /// 一覧の上の「戻す」: 選んでいるシートごとに、Claude が最後に書いた分を、書く前の中身に戻す（1つずつ順に）。
        /// 確かめのダイアログを出す前から終わるまで _excelUndoing を立て、その間は Claude の書き込みを断る（戻す順が崩れないように）。
        /// Excel が時間内に応えなければ、終わるまで（遅れて戻ったか、戻さなかったか）ボタンを押せないままにし、残りは戻さない。
        /// </summary>
        private void UndoExcelWrite()
        {
            if (_excelUndoing) return;
            var targets = ExcelUndoTargets();
            if (targets.Count == 0) return;
            _excelUndoing = true;
            PaintExcelUndo();
            try
            {
                if (MessageBox.Show(this, "Claude が書いた次の分を、書く前の中身に戻しますか？\n\n" + string.Join("\n", targets.Select(WhereOf))
                                    + "\n\n（値と式を戻します。表示形式（日付・% など）や、マクロ・テーブルの広がりで変わったほかのセルは戻りません）", "Excel を戻す",
                                    MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                {
                    EndExcelUndo();
                    return;
                }
                _excelUndoSession++;
                _excelUndoQueue.Clear();
                _excelUndoReport.Clear();
                foreach (var target in targets) _excelUndoQueue.Enqueue(target);
                NextExcelUndo();
            }
            catch (Exception ex)
            {
                _excelUndoReport.Add("戻せませんでした: " + ex.Message);
                EndExcelUndo();
            }
        }

        /// <summary>次の1つを戻す。無ければ終える。</summary>
        private void NextExcelUndo()
        {
            while (_excelUndoQueue.Count > 0)
            {
                var next = _excelUndoQueue.Dequeue();
                if (!StillNewestExcelWrite(next))
                {
                    _excelUndoReport.Add(WhereOf(next) + " は、その間に Claude が書いたので戻していません");
                    continue;
                }
                RunExcelUndo(next, false);
                return;
            }
            EndExcelUndo();
        }

        /// <summary>裏で戻し、結果を画面のスレッドで受ける。</summary>
        private void RunExcelUndo(ExcelBridge.Written written, bool force)
        {
            // 遅れて届いた結果（late）が、時間切れの知らせより先に画面のスレッドに来ることがある。そのときは時間切れの知らせを出さない（どちらも画面のスレッドで動く）
            var finished = new bool[1];
            var session = _excelUndoSession;
            Action<ExcelBridge.UndoResult> late = result => Dispatcher.BeginInvoke(new Action(() =>
            {
                finished[0] = true;
                if (session != _excelUndoSession || !_excelUndoing) return;   // 前の回のもの（その回はもう終えた）
                FinishExcelUndo(written, result, true);
            }));
            Task.Run(() => ExcelBridge.Undo(written, force, 10000, late)).ContinueWith(task =>
            {
                var result = task.IsFaulted ? new ExcelBridge.UndoResult { Kind = ExcelBridge.UndoKind.Failed, Problem = "戻せなかった: " + task.Exception.GetBaseException().Message } : task.Result;
                if (result.Kind == ExcelBridge.UndoKind.NotAnswering && finished[0]) return;
                if (session != _excelUndoSession || !_excelUndoing) return;
                FinishExcelUndo(written, result, false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>この回を終える: ボタンを戻し、結果をステータスバーに出す。</summary>
        private void EndExcelUndo()
        {
            _excelUndoQueue.Clear();
            _excelUndoing = false;
            PaintExcelUndo();
            ShowExcelList(_lastExcelBooks);   // 「※」を付け直す
            if (_excelUndoReport.Count > 0) Status("Excel: " + string.Join("／", _excelUndoReport));
            _excelUndoReport.Clear();
        }

        private void FinishExcelUndo(ExcelBridge.Written written, ExcelBridge.UndoResult result, bool late)
        {
            var where = WhereOf(written);
            try
            {
                switch (result.Kind)
                {
                    case ExcelBridge.UndoKind.Done:
                        _excelWrites.Remove(written);
                        _excelUndoReport.Add((late ? "Excel が遅れて応え、" : string.Empty) + where + " を書く前に戻しました（保存はしていません）");
                        NextExcelUndo();
                        return;

                    case ExcelBridge.UndoKind.NotAnswering:
                        // まだ終わっていない。終わったら late で来るので、それまで押せないままにする。残りは戻さない
                        foreach (var rest in _excelUndoQueue) _excelUndoReport.Add(WhereOf(rest) + " は戻していません（もう一度押してください）");
                        _excelUndoQueue.Clear();
                        Status("Excel が応答しないので、" + where + " を戻せたかどうか分かりません（セルの編集・ダイアログを終えると、戻すか、戻さずにやめます。そのとき知らせます）");
                        PaintExcelUndo();
                        return;

                    case ExcelBridge.UndoKind.Busy:
                        foreach (var rest in _excelUndoQueue) _excelUndoReport.Add(WhereOf(rest) + " は戻していません");
                        _excelUndoQueue.Clear();   // Excel が応えないので、残りも戻さない
                        _excelUndoReport.Add(where + " を戻していません（" + result.Problem + "）。セルの編集・ダイアログを終えてから、もう一度押してください");
                        EndExcelUndo();
                        return;

                    case ExcelBridge.UndoKind.Changed:
                    {
                        if (!StillNewestExcelWrite(written))
                        {
                            _excelUndoReport.Add(where + " は、その間に Claude が書いたので戻していません");
                            NextExcelUndo();
                            return;
                        }
                        var detail = written.Failed ? "書く途中で失敗したので、" + where + " が書いた直後のままかを確かめられません。"
                            : result.Differ < 0 ? "書いた直後の中身を読めなかったので、" + where + " が書いた直後のままかを確かめられません。"
                            : "Claude が書いたあとに、" + where + " の " + result.Cells + " セルのうち " + result.Differ + " セルが変わっています（手で直したか、行・列を足したり消したりして場所がずれたかもしれません）。";
                        var answer = MessageBox.Show(this, detail + "\n\n"
                                                     + "はい: それでも書く前に戻す（あとで変えた分は消えます。行・列を足したり消したりしていたら、ずれた所に書くので押さない）\n"
                                                     + "いいえ: 戻さずに、この記録を捨てる（そのシートで、その前に Claude が書いた分を戻せるようになります）\n"
                                                     + "キャンセル: このシートは戻さない", "Excel を戻す",
                                                     MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                        if (!StillNewestExcelWrite(written))
                        {
                            _excelUndoReport.Add(where + " は、その間に Claude が書いたので戻していません");
                            NextExcelUndo();
                            return;
                        }
                        if (answer == MessageBoxResult.Yes)
                        {
                            RunExcelUndo(written, true);
                            return;
                        }
                        if (answer == MessageBoxResult.No)
                        {
                            _excelWrites.Remove(written);
                            _excelUndoReport.Add(where + " は戻さずに、記録を捨てました");
                        }
                        NextExcelUndo();
                        return;
                    }

                    default:   // Gone・Failed: 残すと、そのシートの前の回も戻せなくなるので、捨てるかを尋ねる
                    {
                        _excelUndoReport.Add(where + " を戻せませんでした（" + result.Problem + "）");
                        if (StillNewestExcelWrite(written)
                            && MessageBox.Show(this, where + " は戻せません（" + result.Problem + "）。\n\nこの記録を捨てて、そのシートで、その前に Claude が書いた分を戻せるようにしますか？", "Excel を戻す",
                                               MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
                            _excelWrites.Remove(written);
                        NextExcelUndo();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _excelUndoReport.Add("戻せませんでした: " + ex.Message);
                EndExcelUndo();
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
                PaintExcelUndo();
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
                            (book, sheet) => choices.Any(choice => SameExcelBook(choice.Book, book.Path ?? book.Name) && string.Equals(choice.Sheet, sheet, StringComparison.OrdinalIgnoreCase)),
                            (book, sheet) => _excelWrites.Any(item => ExcelWriteOn(item, book.Path ?? book.Name, sheet, book.Instance)),
                            _excelListProblem);
        }

        /// <summary>シートを押した: 選ぶ（選んでいたシートなら外す）。いくつでも選べる。Excel の側は何も切り替えない。</summary>
        private void ChooseExcelSheet(string book, string sheet)
        {
            var choices = _excelChoices;
            var chosen = choices.FirstOrDefault(choice => SameExcelBook(choice.Book, book) && string.Equals(choice.Sheet, sheet, StringComparison.OrdinalIgnoreCase));
            if (chosen != null)
            {
                _excelChoices = choices.Where(choice => choice != chosen).ToArray();
                Status("Excel のシートを外しました: " + Path.GetFileName(book) + " › " + sheet);
            }
            else
            {
                var listed = _lastExcelBooks != null ? _lastExcelBooks.FirstOrDefault(item => SameExcelBook(item.Path ?? item.Name, book)) : null;
                _excelChoices = choices.Concat(new[] { new ExcelChoice(book, sheet, listed != null ? listed.Instance : 0) }).ToArray();
                Status("Excel のシートを選びました: " + Path.GetFileName(book) + " › " + sheet + "（選んでいるのは " + _excelChoices.Length + " 枚。話しかけると、名前を Claude に渡します）");
            }
            ShowExcelList(_lastExcelBooks);
            PaintExcelUndo();
        }
    }
}
