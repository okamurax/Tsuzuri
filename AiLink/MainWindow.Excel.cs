using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using AiLink.Agents;
using AiLink.Office;
using AiLink.Views;

namespace AiLink
{
    // MainWindow の一部: 開いている Excel を読み書きする（Tsuzuri の MainWindow.Excel.cs から写したもの。2026-11-25）。
    // ・AI は、開いているブックのどのシートも読める・書ける（2026-11-25。利用者の判断。前は一覧で選んだ1つのシートだけ書けた）。
    //   AI に渡さない場所・ネットの場所・隠れたブックは扱わない。書く前に、AI が利用者の許可を取る（AI に渡す文で指示する）。保存は利用者がする。
    // ・ランチャーのファイラーの下の段に、開いているブックとシートの一覧。シートを押すと選び、もう一度押すと外す。いくつでも選べる（初めは何も選んでいない）。
    //   選んだシートが、AI への「このシート」になる。話しかけるたびの今の状態に、選んだブックとシートの名前だけを書く（中身・Excel で前に出ているもの・保存したかは渡さない）。
    // ・AI が範囲を指定して読む（excel.ps1 → パイプの excel-read）・書く（excel.ps1 -Write → パイプの excel-write）・COM の台本を動かす（excel.ps1 -Script → excel-script）。
    //   読むときは、ブック・シートを付けなければ、選んだシートが1つのときだけそのシート（2つ以上なら付けさせる）。書くときは、どちらも付けさせる
    //   （付けなければ断る。許可をもらったあとに利用者が選び直しても、許可をもらったのと別のシートに書かないように）。
    // ・戻す仕組みは持たない（2026-11-25。利用者の判断。AI は保存しないので、利用者は保存せずに閉じれば元に戻せる）。
    //   保存していない変更があるブックには、一覧で名前の右に「※」（AI の分も、手で直した分も）。
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

        /// <summary>今の状態に「Excel」の段を書く（選んだブックとシートの名前だけ。選んでいなくても、そう書く）。Excel は読まない。</summary>
        private void AppendExcelContext(StringBuilder text)
        {
            var choices = _excelChoices;
            if (choices.Length == 0)
            {
                text.Append("Excel: Excel の一覧でシートを選んでいない（前の頼みで選んでいても、今は外されている）\n");
                return;
            }
            var shown = choices.Where(choice => !IsGuardedBook(choice.Book)).ToList();
            var hidden = choices.Length - shown.Count;
            if (shown.Count == 0)
            {
                text.Append("Excel: Excel の一覧で選んだシートは、AI に渡さない場所のブックにあるので、名前も渡していない。尋ねたり探したりしない\n");
                return;
            }
            text.Append("Excel: 利用者がExcel の一覧で選んだシート（").Append(shown.Count).Append(" 枚。名前だけ。中身は渡していない）: ");
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
                return "Excel の一覧で選んだシートは、AI に渡さない場所のブックにあるので扱わない。利用者にそう伝える（名前を尋ねたり探したりしない）";
            if (book.Length == 0 && sheet.Length == 0)
            {
                if (choices.Length == 0) return "Excel の一覧でシートが選ばれていない。どのブックのどのシートかを推し量らず、利用者に一覧で選んでもらうか、名前を教えてもらう";
                if (choices.Length > 1) return "Excel の一覧でシートを " + choices.Length + " 枚選んでいる。どのシートかを -Book と -Sheet で付ける（分からなければ利用者に尋ねる）";
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
        /// AI が範囲を指定して読む（パイプの「excel-read→ブック→シート→範囲」。どれも Base64）。開いているブックのどのシートも読める。
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
            var text = ExcelBridge.Overview(book, sheet, IsGuardedBook, Path.Combine(AiFolder, "excel-overview.png"), 45000, out bookShown, out sheetShown, out problem);
            if (text == null) return "error\t" + (problem ?? "Excel を読めなかった。");
            var file = Path.Combine(AiFolder, "excel-overview.txt");
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
        /// AI が書く（パイプの「excel-write→ブック→シート→場所→中身」。どれも Base64）。開いているブックのどのシートにも書ける
        /// （書いてよいかは、AI が書く前に利用者に尋ねる。AI に渡す文の指示）。ブックとシートは両方付けさせる（付けなければ断る。
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
                    ? "Excel が遅れて応えましたが、AI が頼んでいた分を書く途中で失敗しました: " + where + "（途中まで書けているかもしれません。AI には「応答しない」と返していました）"
                    : "Excel が遅れて応え、AI が頼んでいた分を書きました: " + where + "（AI には「応答しない」と返していました。保存はしていません）");
            else
                Status(written.Failed
                    ? "AI が Excel に書こうとして失敗しました: " + where + "（途中まで書けているかもしれません）"
                    : "AI が Excel に書きました: " + where + "（保存はしていません）");
            RefreshExcelList();
        }

        // ---------------------------------------------------------------
        // AI の COM の台本（excel.ps1 -Script。2026-11-25。利用者の判断）
        //
        // AI が Excel の COM を使う PowerShell の台本を書き、AiLink が中身を確かめてから、別の PowerShell のプロセスで動かす。
        // 保存・閉じる・Excel を終える呼び出しを含む台本は動かさない（保存は利用者がする。保存しなければ、閉じるときに保存せずに閉じて元に戻せるため。
        // 閉じると、利用者の保存していない作業まで消えるため）。中身の確かめは語で見るだけなので、書き方を変えれば抜けられる（誤って保存するのを防ぐためのもの）。
        // 別のプロセスで動かすのは、COM の参照を持ち続けないため（プロセスが終われば全部手放される）と、止まっても AiLink を巻き込まないため。
        // 動かしている間は、Excel を呼ぶほかの処理（一覧・読む・書く）を待たせる（ExcelBridge.RunExclusive）。
        // ---------------------------------------------------------------

        /// <summary>
        /// 台本に含んではいけない語（大文字・小文字を区別しない。語の一部でも）。保存（save を含む語。Saved・AutoSave も）・閉じる・Excel を終える・
        /// ブックを読み込み直す（保存していない変更が消える）・プロセスを止める・キーを送る（Ctrl+S を送れば保存になる）。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ExcelForbidden = new System.Text.RegularExpressions.Regex(
            @"save|\bclose\b|\bquit\b|changefileaccess|stop-process|taskkill|sendkeys|\bkill\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>台本の出力として返す字数の上限。</summary>
        private const int ExcelScriptOutput = 20000;

        /// <summary>台本を待つ秒の上限（AI の Bash ツールの上限 600 秒より、Gate を待つ分・止める分だけ短く）。</summary>
        private const int ExcelScriptMaxSeconds = 540;

        /// <summary>包みが「Excel が動いていない」ときに使う終わりの番号（台本自身の exit と取り違えにくい番号）。</summary>
        private const int ExcelNotRunningExit = 87;

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
            if (script.Trim().Length == 0) return ExcelScriptAnswer(false, "台本が空。何もしていない");

            var forbidden = ExcelForbidden.Match(script);
            if (forbidden.Success)
                return ExcelScriptAnswer(false, "保存・閉じる・Excel を終える・読み込み直す・プロセスを止める・キーを送る呼び出しを含む台本は動かさない（「" + forbidden.Value + "」を含んでいる。"
                                                + "save を含む語（Saved・AutoSave・Unsaved なども）・Close・Quit・ChangeFileAccess・Stop-Process・taskkill・SendKeys・Kill の語は、"
                                                + "コメントや文字・名前の一部でも使えない）。保存とブックを閉じるのは利用者がする。セルに書く文字にこれらの語が入るなら、excel.ps1 -Write で書く。何もしていない");
            // AI に渡さない場所に触れる台本は動かさない（台本は AI のツールのフックを通らないので、Bash のコマンドと同じ確かめをここでする）
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
                Status(failed ? "AI の Excel の台本が、エラーか時間切れで止まりました（途中まで操作されているかもしれません。保存はしていません）"
                              : "AI が Excel の台本を動かしました（保存はしていません）");
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
            var body = Path.Combine(AiFolder, "excel-script-" + id + ".ps1");
            var wrapper = Path.Combine(AiFolder, "excel-script-run-" + id + ".ps1");
            try
            {
                // PowerShell 5.1 は BOM の無い台本を ANSI の文字コードで読むので、BOM を付けて書く（日本語のシート名・文字を化けさせない）
                File.WriteAllText(body, script, new UTF8Encoding(true));
                File.WriteAllText(wrapper,
                    "$ErrorActionPreference = 'Stop'\r\n"
                    + "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n"
                    + "try { $excel = [Runtime.InteropServices.Marshal]::GetActiveObject('Excel.Application') } catch { [Console]::Error.WriteLine('Excel is not running'); exit " + ExcelNotRunningExit + " }\r\n"
                    + ". '" + body.Replace("'", "''") + "'\r\n",
                    new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                TryDelete(body);
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
                var info = new System.Diagnostics.ProcessStartInfo("powershell.exe",
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
                if (process != null) process.Dispose();
                outputDone.Dispose();
                errorDone.Dispose();
                TryDelete(body);
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
        // Excel の一覧
        // ---------------------------------------------------------------

        private ExcelListView _excelList;
        private bool _excelListing;
        private bool _excelListPending;

        /// <summary>一覧を読み直す（裏で）。選んだシートが無くなっていれば外す。</summary>
        private void RefreshExcelList()
        {
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
                Status("Excel のシートを選びました: " + Path.GetFileName(book) + " › " + sheet + "（選んでいるのは " + _excelChoices.Length + " 枚。話しかけると、名前を AI に渡します）");
            }
            ShowExcelList(_lastExcelBooks);
        }
    }
}
