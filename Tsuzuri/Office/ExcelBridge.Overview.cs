using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tsuzuri.Office
{
    /// <summary>
    /// シートの概要（2026-11-25。利用者の考え。SPEC 19章）: Claude がシートを扱い始めるときに一度、全体を読んで把握するため。
    /// セルの値と式に加え、書式（フォント・大きさ・太字・斜体・文字の色・塗りの色・表示形式・横の配置）・結合・列の幅・行の高さ・隠した行と列・
    /// 図形・グラフ・テーブル・ピボットテーブル・名前の定義・条件付き書式・入力規則・コメント・ハイパーリンク・シートの見出しの色・保護を、文字にして返す。
    /// 書式はセルを1つずつ聞くと遅い（1回ごとにプロセスをまたぐ）ので、範囲でまとめて聞き、そろっていなければ半分に分けて聞き直す。
    /// 書式は、素のシートか書式を整えたシートかの大まかな判断と、使っているフォント・色・表示形式を並べるだけにする（2026-11-25。利用者の判断）。
    /// どれが「ふだん」かを決めて違いを並べると、見出しの太字・数字のフォント・印刷に合わせた調整を、例外や崩れと取り違えるおそれがあるため。
    /// 細かいところ（どのセルがどの書式か）は、Claude が要るときに台本で調べる。
    /// あわせて、使っている範囲を画面の見た目のまま画像にする（2026-11-25。利用者の考え。どういうファイルか（書類の様式・一覧・集計など）を、
    /// 文字の並びから組み立てるより、見た目でつかむほうが早く確かなため）。Range.CopyPicture でクリップボードを通すので、利用者がコピーしていたものは消える
    /// （戻さない。利用者の判断）。
    /// </summary>
    public static partial class ExcelBridge
    {
        /// <summary>書式を聞く範囲の数の上限（超えたら、そこまでと書く）。</summary>
        private const int OverviewBlocks = 1500;

        /// <summary>書式・行の高さを調べるのに使う時間の上限（過ぎたら途中までにして、ほかの段は続ける）。</summary>
        private const int OverviewWalkMillis = 15000;

        /// <summary>A4 の紙で印刷できる広さ（ポイント。余白を引いたおよその値）。シートの大きさを何枚分かで見積もるのに使う。</summary>
        private const double PageShort = 520;
        private const double PageLong = 760;

        /// <summary>これより行が多ければ、大きなデータの一覧と見込み、画像は上の DataPictureRows 行だけにする（見出しとデータの並び方が分かれば足りる）。</summary>
        private const int DataRows = 1000;
        private const int DataPictureRows = 60;

        /// <summary>画像にする範囲の上限（行・列）と、画像の長い辺の上限（ピクセル。超えたら縮める）。</summary>
        private const int PictureRows = 300;
        private const int PictureColumns = 60;
        private const int PictureSide = 2400;

        /// <summary>画像にする範囲の幅・高さの上限（ポイント）。大きすぎると Excel が「図が大きすぎる」のダイアログを出して止まるうえ、縮めると字は読めないため。</summary>
        private const double PicturePoints = 3000;

        /// <summary>画像を撮るのに使う時間の上限（ほかのアプリがクリップボードを開いたままでも、概要全体を時間切れにしない）。</summary>
        private const int PictureMillis = 10000;

        /// <summary>図形・コメントなど、種類ごとに並べる数の上限。</summary>
        private const int OverviewItems = 50;

        /// <summary>1つの書式に並べる範囲の数の上限。</summary>
        private const int OverviewRanges = 40;

        /// <summary>シートの概要を文字で返し、シートの画像を picture に書く。読めなければ problem にわけを入れて null。</summary>
        public static string Overview(string book, string sheet, Func<string, bool> guarded, string picture, int timeout, out string bookShown, out string sheetShown, out string problem)
        {
            string text = null, why = null, shownBook = null, shownSheet = null;
            var outcome = RunSta(job => text = OverviewCore(book, sheet, guarded, picture, job, out shownBook, out shownSheet, out why), timeout, null);
            var done = outcome == Outcome.Done;
            bookShown = done ? shownBook : null;
            sheetShown = done ? shownSheet : null;
            problem = done ? why : outcome == Outcome.Previous ? StillBusy
                : "シートが大きいか、Excel が応えないので、時間内に読みきれなかった（Excel がセルの編集中・ダイアログでなければ、-Range で範囲を絞って読む）";
            return done ? text : null;
        }

        /// <summary>書式を聞いた範囲1つ（行・列は 1 始まりのシートの番号）。</summary>
        private sealed class StyledBlock
        {
            public int Top, Left, Bottom, Right;
            public string[] Parts;   // フォント・文字の色・塗り・表示形式・横の配置。null なら、文字の一部だけ書式が違うセル
            public bool Merged;
        }

        private static string OverviewCore(string bookName, string sheetName, Func<string, bool> guarded, string picture, Job job,
                                           out string bookShown, out string sheetShown, out string problem)
        {
            bookShown = null;
            sheetShown = null;
            problem = null;
            if (string.IsNullOrEmpty(bookName) || string.IsNullOrEmpty(sheetName))
            {
                problem = "ブックとシートを指定する";
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
                    var ws = FindSheet(scope, app, bookName, sheetName, 0, guarded, out problem);
                    if (ws == null) return null;
                    var wb = scope.Get(ws, "Parent");
                    bookShown = Text(scope.Get(wb, "Path")).Length > 0 ? Text(scope.Get(wb, "FullName")) : Text(scope.Get(wb, "Name"));
                    sheetShown = Text(scope.Get(ws, "Name"));

                    object used;
                    try
                    {
                        used = scope.Get(ws, "UsedRange");
                    }
                    catch (Exception ex) when (!IsBusy(ex))
                    {
                        problem = "セルのシートではない（グラフのシートなど）: " + sheetShown;
                        return null;
                    }

                    var top = Convert.ToInt32(scope.Get(used, "Row"), CultureInfo.InvariantCulture);
                    var left = Convert.ToInt32(scope.Get(used, "Column"), CultureInfo.InvariantCulture);
                    var rows = Count(scope, used, "Rows");
                    var columns = Count(scope, used, "Columns");
                    // 書式・行の高さを聞くのは、値を読むのと同じ範囲まで（多ければ先頭の行・左の列だけ）
                    var walkColumns = Math.Min(columns, DumpColumns);
                    var walkRows = Math.Min(rows, Math.Max(1, DumpCells / Math.Max(1, walkColumns)));

                    // まず使っている範囲の大きさ（行・列と、幅・高さのポイント）をつかみ、何の見込みか・画像をどこまで撮るかを決める
                    string sizeNote;
                    int pictureRows, pictureColumns;
                    Measure(scope, used, rows, columns, out sizeNote, out pictureRows, out pictureColumns);
                    // 画像は先に作る（書式などを調べて時間切れになっても、見た目はつかめるように）
                    var pictureNote = Picture(scope, ws, top, left, pictureRows, pictureColumns, rows, columns, picture, job);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    Func<bool> stop = () => job.Abandoned || clock.ElapsedMilliseconds > OverviewWalkMillis;
                    var bottom = top + walkRows - 1;
                    var right = left + walkColumns - 1;

                    var o = new StringBuilder();
                    o.Append("# シートの概要\n");
                    o.Append("# ブック: ").Append(bookShown).Append('\n');
                    o.Append("# シート: ").Append(sheetShown).Append('\n');
                    o.Append("使っている範囲: ").Append(Address(scope, used)).Append("（").Append(rows).Append(" 行 × ").Append(columns).Append(" 列）");
                    if (walkRows < rows || walkColumns < columns)
                        o.Append("。多いので、書式・結合・列の幅・行の高さ・セルは ").Append(RangeName(top, left, bottom, right)).Append(" だけ");
                    o.Append('\n');
                    o.Append("\n## 大きさ\n").Append(sizeNote);
                    o.Append("\n## 画像\n").Append(pictureNote).Append('\n');
                    var lookAt = o.Length;   // ここに「見た目」の段を入れる（書式などを調べてから決める）

                    Section(o, "シート", () => SheetFacts(scope, ws), job);
                    var blocks = new List<StyledBlock>();
                    var budget = OverviewBlocks;
                    var reasons = new List<string>();
                    var styleRead = false;
                    var styleCut = false;
                    Section(o, "書式のあらまし", () =>
                    {
                        // 列ごとに聞き、そろっていない列だけを行で半分に分ける（ふつうの表は列ごとに表示形式が違うので、行から割ると上限をすぐ使い切る）
                        for (var c = left; c <= right && !stop(); c++)
                            WalkStyles(scope, ws, top, c, bottom, c, blocks, ref budget, stop);
                        styleCut = budget <= 0 || stop();
                        var text = StyleText(blocks, styleCut, reasons);
                        styleRead = true;
                        return text;
                    }, job);
                    Section(o, "結合", () =>
                    {
                        var text = MergeText(scope, ws, blocks, styleCut);
                        if (!string.IsNullOrEmpty(text)) reasons.Add("結合");
                        return text;
                    }, job);
                    Section(o, "列の幅・隠した列", () =>
                    {
                        bool widths;
                        var text = ColumnText(scope, ws, left, right, out widths);
                        if (widths) reasons.Add("列の幅");
                        return text;
                    }, job);
                    Section(o, "行の高さ・隠した行", () =>
                    {
                        bool heights;
                        var text = RowText(scope, ws, top, bottom, stop, out heights);
                        if (heights) reasons.Add("行の高さ");
                        return text;
                    }, job);
                    // 大まかな判断（素のシートか、見た目・印刷に合わせて書式を整えたシートか）。表示形式（日付・数の形）は判断に入れない
                    o.Insert(lookAt, "\n## 見た目\n" + (!styleRead
                        ? "分からない（書式を読めなかった）"
                        : reasons.Count > 0
                            ? "書式を整えたシート（当てはまったもの: " + string.Join("・", reasons.Distinct()) + "。見出しや印刷に合わせた調整かもしれないので、今の書式を崩さない）"
                            : "素のシート（フォント・色・塗り・配置・結合・列の幅・行の高さは、ほとんど既定のまま。表示形式は判断に入れていない）")
                        + (styleCut ? "（範囲が広いので、書式は途中まで調べて判断した）" : string.Empty) + "\n");
                    Section(o, "図形", () => ShapeText(scope, ws), job);
                    Section(o, "グラフ", () => ChartText(scope, ws), job);
                    Section(o, "テーブル・ピボットテーブル", () => TableText(scope, ws), job);
                    Section(o, "名前の定義（ブック全体）", () => NameText(scope, wb), job);
                    Section(o, "条件付き書式", () => ConditionText(scope, ws), job);
                    Section(o, "入力規則", () => ValidationText(scope, ws), job);
                    Section(o, "コメント・メモ", () => CommentText(scope, ws), job);
                    Section(o, "ハイパーリンク", () => LinkText(scope, ws), job);

                    if (job.Abandoned) return null;   // 呼んだ側はもう待っていない
                    o.Append("\n## セル（タブ区切り。1行目は列の名前、各行の頭は行の番号。式のセルは値のあとに「（式 =…）」。セルの中の改行は ↵、タブは →）\n");
                    var grid = ReadGrid(scope, used, DumpCells, DumpColumns);
                    if (grid.Rows > 0) o.Append(grid.ToTsv(string.Empty));
                    if (grid.Truncated || grid.ColumnsTruncated)
                        o.Append("（多いので、ここまで。続きは excel.ps1 -Range で範囲を絞って読む）\n");
                    return o.ToString();
                }
                catch (Exception ex)
                {
                    problem = Busy(ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// 使っている範囲の大きさをつかむ: 行・列の数と、幅・高さ（ポイント。1回ずつ聞けば済むので、数万行でも速い）から、A4 で何枚分かを見積もり、
        /// 大まかな見込み（1枚に収まる書類・様式か、数枚分の表か、大きなデータの一覧か）と、画像にする行・列を決める。見込みは大きさからの推し量りで、決めつけない。
        /// </summary>
        private static void Measure(Scope scope, object used, int rows, int columns, out string note, out int pictureRows, out int pictureColumns)
        {
            double width = 0, height = 0;
            try
            {
                width = Convert.ToDouble(scope.Get(used, "Width"), CultureInfo.InvariantCulture);
                height = Convert.ToDouble(scope.Get(used, "Height"), CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (!IsBusy(ex))
            {
            }
            var o = new StringBuilder();
            o.Append(rows).Append(" 行 × ").Append(columns).Append(" 列");
            pictureColumns = Math.Min(columns, PictureColumns);
            if (width > 0 && height > 0)
            {
                // 縦向き・横向きのうち、少ない枚数で収まるほう
                var portrait = Math.Ceiling(width / PageShort) * Math.Ceiling(height / PageLong);
                var landscape = Math.Ceiling(width / PageLong) * Math.Ceiling(height / PageShort);
                var pages = Math.Min(portrait, landscape);
                o.Append("。幅 約 ").Append(Math.Round(width)).Append(" pt × 高さ 約 ").Append(Math.Round(height)).Append(" pt（A4 ")
                 .Append(portrait <= landscape ? "縦" : "横").Append("で約 ").Append(pages.ToString("0", CultureInfo.InvariantCulture)).Append(" 枚分）\n");
                if (pages <= 1)
                {
                    o.Append("見込み（大きさからの推し量り）: A4 1枚に収まる大きさ。書類・様式・1枚の表かもしれない。画像は全体を撮る予定（何を撮れたかは「## 画像」）\n");
                    pictureRows = Math.Min(rows, PictureRows);
                    Fit(width, height, rows, columns, ref pictureRows, ref pictureColumns);
                    note = o.ToString();
                    return;
                }
            }
            else
            {
                o.Append("（幅・高さは読めなかった）\n");
            }
            if (rows > DataRows)
            {
                o.Append("見込み（大きさからの推し量り）: 行がとても多い。大きなデータの一覧かもしれない。画像は見出しとデータの並び方が分かる上の ")
                 .Append(DataPictureRows).Append(" 行だけを撮る予定（何を撮れたかは「## 画像」。中身は -Range で絞って読む）\n");
                pictureRows = Math.Min(rows, DataPictureRows);
                Fit(width, height, rows, columns, ref pictureRows, ref pictureColumns);
                note = o.ToString();
                return;
            }
            pictureRows = Math.Min(rows, PictureRows);
            Fit(width, height, rows, columns, ref pictureRows, ref pictureColumns);
            o.Append("見込み（大きさからの推し量り）: 数枚分の表など。画像は ")
             .Append(pictureRows < rows || pictureColumns < columns ? "左上から " + pictureRows + " 行 × " + pictureColumns + " 列まで" : "全体を").Append("撮る予定（何を撮れたかは「## 画像」）\n");
            note = o.ToString();
        }

        /// <summary>撮る範囲を、幅・高さが PicturePoints に収まる行・列までに縮める（行・列の大きさは平均とみなした、およその値）。</summary>
        private static void Fit(double width, double height, int rows, int columns, ref int pictureRows, ref int pictureColumns)
        {
            if (width > 0 && columns > 0)
                pictureColumns = Math.Max(1, Math.Min(pictureColumns, (int)(PicturePoints / (width / columns))));
            if (height > 0 && rows > 0)
                pictureRows = Math.Max(1, Math.Min(pictureRows, (int)(PicturePoints / (height / rows))));
        }

        /// <summary>
        /// 使っている範囲（多ければ左上から PictureRows 行 × PictureColumns 列まで）を、画面の見た目のまま PNG にする（Range.CopyPicture → クリップボード）。
        /// 撮れなければ範囲を半分にして、3回まで撮り直す。返すのは、概要に書く1行（画像のパスと範囲、か撮れなかったわけ）。
        /// </summary>
        private static string Picture(Scope scope, object ws, int top, int left, int takeRows, int takeColumns, int rows, int columns, string path, Job job)
        {
            if (string.IsNullOrEmpty(path)) return "作っていない";
            try
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);   // 前のシートの画像を、今のシートのものと取り違えないように
            }
            catch (Exception)
            {
            }
            string why = null;
            var shrunk = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Func<bool> late = () => job.Abandoned || clock.ElapsedMilliseconds > PictureMillis;
            for (var attempt = 0; attempt < 3 && !late(); attempt++)
            {
                try
                {
                    var range = scope.Get(ws, "Range", scope.Get(ws, "Cells", top, left), scope.Get(ws, "Cells", top + takeRows - 1, left + takeColumns - 1));
                    try
                    {
                        System.Windows.Clipboard.Clear();
                    }
                    catch (Exception)
                    {
                    }
                    scope.Call(range, "CopyPicture", 1, 2);   // xlScreen（画面の見た目）・xlBitmap
                    System.Windows.Media.Imaging.BitmapSource image = null;
                    for (var i = 0; i < 30 && image == null && !late(); i++)
                    {
                        try
                        {
                            if (System.Windows.Clipboard.ContainsImage()) image = System.Windows.Clipboard.GetImage();
                        }
                        catch (Exception)
                        {
                        }
                        if (image == null) System.Threading.Thread.Sleep(100);
                    }
                    if (image == null)
                    {
                        why = late() ? "時間内にクリップボードから画像を受け取れなかった" : "クリップボードから画像を受け取れなかった";
                        continue;
                    }
                    try
                    {
                        System.Windows.Clipboard.Clear();   // 受け取ったら消す（利用者が次に貼り付けたとき、シートの画像が出ないように）
                    }
                    catch (Exception)
                    {
                    }
                    // 透けた画像にならないように、透明の度合いを捨てる（HBITMAP から作ると、透明の度合いが 0 になることがある）
                    image = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                    var side = Math.Max(image.PixelWidth, image.PixelHeight);
                    if (side > PictureSide)
                    {
                        var scale = (double)PictureSide / side;
                        image = new System.Windows.Media.Imaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
                    }
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
                    var shown = RangeName(top, left, top + takeRows - 1, left + takeColumns - 1);
                    return path + "（" + shown + " を画面の見た目で。どういうファイルか・配置・色分けをつかむ用。字は小さくて読めないことがあるので、中身は下の文字で読む"
                           + (shrunk ? "。撮れなかったので範囲を縮めた" : takeRows < rows || takeColumns < columns ? "。使っている範囲の左上のここだけ" : string.Empty)
                           + "。真っ白・何も無いように見えたら、Excel の窓が隠れているなどで撮れていないので、空のシートと取り違えず、文字で判断する）";
                }
                catch (Exception ex) when (!IsBusy(ex))
                {
                    why = (ex.InnerException ?? ex).Message;
                    takeRows = Math.Max(1, takeRows / 2);   // 大きすぎて撮れないことがあるので、範囲を縮めて撮り直す
                    takeColumns = Math.Max(1, takeColumns / 2);
                    shrunk = true;
                }
            }
            return "作れなかった（" + (why ?? "時間切れ") + "）。文字で判断する";
        }

        /// <summary>見出しと中身を足す。中身を読めなければ、そう書く（ほかの段は続ける）。Excel が応えないときは、全体をやめる。</summary>
        private static void Section(StringBuilder o, string title, Func<string> body, Job job)
        {
            string text;
            if (job.Abandoned)
            {
                o.Append("\n## ").Append(title).Append("\n（時間切れで調べなかった）\n");
                return;
            }
            try
            {
                text = body();
            }
            catch (Exception ex) when (!IsBusy(ex))
            {
                text = "（読めなかった: " + (ex.InnerException ?? ex).Message + "）";
            }
            o.Append("\n## ").Append(title).Append('\n').Append(string.IsNullOrEmpty(text) ? "なし\n" : text);
        }

        private static string SheetFacts(Scope scope, object ws)
        {
            var o = new StringBuilder();
            var visible = Convert.ToInt32(scope.Get(ws, "Visible"), CultureInfo.InvariantCulture);
            o.Append("表示: ").Append(visible == -1 ? "見えている" : visible == 2 ? "隠している（シートの一覧にも出ない）" : "隠している").Append('\n');
            o.Append("保護: ").Append(scope.Get(ws, "ProtectContents") as bool? == true ? "している（セルを変えられない）" : "していない").Append('\n');
            try
            {
                var tab = scope.Get(ws, "Tab");
                var index = Convert.ToInt32(scope.Get(tab, "ColorIndex"), CultureInfo.InvariantCulture);
                o.Append("見出しの色: ").Append(index == -4142 || index == -4105 ? "なし" : ColorName(scope.Get(tab, "Color"))).Append('\n');
            }
            catch (Exception ex) when (!IsBusy(ex))
            {
            }
            o.Append("ふだんの列の幅: ").Append(Number(scope.Get(ws, "StandardWidth"))).Append("、ふだんの行の高さ: ").Append(Number(scope.Get(ws, "StandardHeight"))).Append(" pt\n");
            return o.ToString();
        }

        // ---------------------------------------------------------------
        // 書式
        // ---------------------------------------------------------------

        /// <summary>範囲の書式がそろっていれば1つに、そろっていなければ半分に分けて聞き直す（行を先に、1行になったら列を）。</summary>
        private static void WalkStyles(Scope scope, object ws, int top, int left, int bottom, int right, List<StyledBlock> blocks, ref int budget, Func<bool> stop)
        {
            if (budget <= 0 || stop()) return;
            budget--;
            var range = scope.Get(ws, "Range", scope.Get(ws, "Cells", top, left), scope.Get(ws, "Cells", bottom, right));
            bool merged, mixed;
            var parts = StyleOf(scope, range, out merged, out mixed);
            if (!mixed)
            {
                blocks.Add(new StyledBlock { Top = top, Left = left, Bottom = bottom, Right = right, Parts = parts, Merged = merged });
                return;
            }
            if (top == bottom && left == right)
            {
                // 1つのセルでそろわない: 文字の一部だけ書式が違う（太字の語がある、など）
                blocks.Add(new StyledBlock { Top = top, Left = left, Bottom = bottom, Right = right, Parts = null, Merged = merged });
                return;
            }
            if (bottom > top)
            {
                var middle = top + (bottom - top) / 2;
                WalkStyles(scope, ws, top, left, middle, right, blocks, ref budget, stop);
                WalkStyles(scope, ws, middle + 1, left, bottom, right, blocks, ref budget, stop);
            }
            else
            {
                var middle = left + (right - left) / 2;
                WalkStyles(scope, ws, top, left, bottom, middle, blocks, ref budget, stop);
                WalkStyles(scope, ws, top, middle + 1, bottom, right, blocks, ref budget, stop);
            }
        }

        /// <summary>範囲の書式を、フォント・文字の色・塗り・表示形式・横の配置の文字にする。そろっていない項目があれば mixed。</summary>
        private static string[] StyleOf(Scope scope, object range, out bool merged, out bool mixed)
        {
            var font = scope.Get(range, "Font");
            var interior = scope.Get(range, "Interior");
            var values = new[]
            {
                scope.Get(font, "Name"), scope.Get(font, "Size"), scope.Get(font, "Bold"), scope.Get(font, "Italic"), scope.Get(font, "Color"),
                scope.Get(interior, "ColorIndex"), scope.Get(interior, "Color"), scope.Get(range, "NumberFormat"), scope.Get(range, "HorizontalAlignment"),
                scope.Get(range, "MergeCells"),
            };
            merged = values[9] as bool? == true;
            mixed = values.Any(value => value == null || value is DBNull);
            if (mixed) return null;

            var fontText = Text(values[0]) + " " + Number(values[1]) + (values[2] as bool? == true ? " 太字" : string.Empty) + (values[3] as bool? == true ? " 斜体" : string.Empty);
            var fillIndex = Convert.ToInt32(values[5], CultureInfo.InvariantCulture);
            return new[]
            {
                fontText, ColorName(values[4]), fillIndex == -4142 ? null : ColorName(values[6]), Text(values[7]), AlignName(Convert.ToInt32(values[8], CultureInfo.InvariantCulture)),
            };
        }

        /// <summary>
        /// 書式のあらまし: 使っているフォント・文字の色・塗りの色・表示形式・横の配置を、多い順に並べる（どれが「ふだん」かは決めない）。
        /// reasons に、書式を整えたと見たわけを足す（表示形式は入れない。日付・数の列があるだけで「整えた」にしないため）。
        /// </summary>
        private static string StyleText(List<StyledBlock> blocks, bool truncated, List<string> reasons)
        {
            if (blocks.Count == 0) return null;
            var styled = blocks.Where(block => block.Parts != null).ToList();
            Func<int, List<string>> used = index => styled.Where(block => block.Parts[index] != null)
                                                          .GroupBy(block => block.Parts[index])
                                                          .OrderByDescending(group => group.Sum(block => (long)(block.Bottom - block.Top + 1) * (block.Right - block.Left + 1)))
                                                          .Select(group => group.Key).ToList();
            var fonts = used(0);
            var colors = used(1);
            var fills = used(2);
            var formats = used(3);
            var aligns = used(4);
            var partial = blocks.Count(block => block.Parts == null);
            if (fonts.Count > 1) reasons.Add("フォントの種類・大きさ・太字");
            if (colors.Any(color => color != "#000000")) reasons.Add("文字の色");
            if (fills.Count > 0) reasons.Add("塗り");
            if (aligns.Any(align => align != "標準")) reasons.Add("配置");
            if (partial > 0) reasons.Add("文字の一部の書式");

            var o = new StringBuilder();
            Func<List<string>, string> list = items => string.Join("、", items.Take(12)) + (items.Count > 12 ? " ほか " + (items.Count - 12) + " 通り" : string.Empty);
            o.Append("使っているフォント（多い順）: ").Append(list(fonts)).Append('\n');
            o.Append("文字の色: ").Append(list(colors)).Append('\n');
            o.Append("塗りの色: ").Append(fills.Count == 0 ? "なし" : list(fills)).Append('\n');
            o.Append("表示形式: ").Append(list(formats)).Append('\n');
            o.Append("横の配置: ").Append(list(aligns)).Append('\n');
            if (partial > 0) o.Append("文字の一部だけ書式が違うセル: ").Append(partial).Append(" か所\n");
            if (truncated) o.Append("（書式の種類が多いので、途中まで調べた）\n");
            o.Append("（どのセルがどの書式か・罫線・縦の配置・折り返しは入れていない。書式を変える・真似るときは、先に台本で調べる）\n");
            return o.ToString();
        }

        /// <summary>結合した範囲（書式を聞いたときに「結合あり」とそろった範囲の中を、左上から1つずつ見る）。</summary>
        private static string MergeText(Scope scope, object ws, List<StyledBlock> blocks, bool styleCut)
        {
            var areas = new List<int[]>();
            var truncated = false;
            foreach (var block in blocks.Where(block => block.Merged))
            {
                for (var r = block.Top; r <= block.Bottom && !truncated; r++)
                    for (var c = block.Left; c <= block.Right && !truncated; c++)
                    {
                        var row = r;
                        var column = c;
                        if (areas.Any(known => row >= known[0] && row <= known[2] && column >= known[1] && column <= known[3])) continue;
                        if (areas.Count >= OverviewItems * 4)
                        {
                            truncated = true;
                            break;
                        }
                        var area = scope.Get(scope.Get(ws, "Cells", r, c), "MergeArea");
                        var areaTop = Convert.ToInt32(scope.Get(area, "Row"), CultureInfo.InvariantCulture);
                        var areaLeft = Convert.ToInt32(scope.Get(area, "Column"), CultureInfo.InvariantCulture);
                        areas.Add(new[] { areaTop, areaLeft, areaTop + Count(scope, area, "Rows") - 1, areaLeft + Count(scope, area, "Columns") - 1 });
                    }
            }
            if (areas.Count == 0) return styleCut ? "（書式を途中まで調べたので、その範囲では見つからなかった）\n" : null;
            return string.Join(", ", areas.Select(area => RangeName(area[0], area[1], area[2], area[3]))) + (truncated ? " ほか（多いので途中まで）" : string.Empty)
                   + (styleCut ? "（書式を途中まで調べたので、ほかにもあるかもしれない）" : string.Empty) + "\n";
        }

        private static string ColumnText(Scope scope, object ws, int left, int right, out bool widths)
        {
            var standard = Number(scope.Get(ws, "StandardWidth"));
            var o = new StringBuilder();
            var hidden = new List<string>();
            string runWidth = null;
            var runStart = left;
            for (var c = left; c <= right + 1; c++)
            {
                string width = null;
                if (c <= right)
                {
                    var column = scope.Get(scope.Get(ws, "Columns"), "Item", c);
                    if (scope.Get(column, "Hidden") as bool? == true) hidden.Add(ColumnName(c));
                    width = Number(scope.Get(column, "ColumnWidth"));
                }
                if (width != runWidth || c > right)
                {
                    if (runWidth != null && runWidth != standard)
                        o.Append(runStart == c - 1 ? ColumnName(runStart) : ColumnName(runStart) + ":" + ColumnName(c - 1)).Append(' ').Append(runWidth).Append(", ");
                    runWidth = width;
                    runStart = c;
                }
            }
            widths = o.Length > 0;
            var text = new StringBuilder();
            if (o.Length > 0) text.Append("ふだんと違う幅: ").Append(o.ToString().TrimEnd(',', ' ')).Append('\n');
            if (hidden.Count > 0) text.Append("隠した列: ").Append(string.Join(", ", hidden)).Append('\n');
            return text.ToString();
        }

        /// <summary>行の高さと隠した行（行の範囲でまとめて聞き、そろっていなければ半分に分ける）。</summary>
        private static string RowText(Scope scope, object ws, int top, int bottom, Func<bool> stop, out bool heights)
        {
            var standard = Number(scope.Get(ws, "StandardHeight"));
            var hidden = new List<string>();
            var budget = OverviewBlocks;
            var found = new List<KeyValuePair<string, string>>();
            WalkRows(scope, ws, top, bottom, found, hidden, ref budget, stop);
            var text = new StringBuilder();
            var odd = found.Where(pair => pair.Value != standard).ToList();
            heights = odd.Count > 0;
            if (odd.Count > 0)
                text.Append("ふだんと違う高さ（pt）: ").Append(string.Join(", ", odd.Take(OverviewRanges).Select(pair => pair.Key + " " + pair.Value)))
                    .Append(odd.Count > OverviewRanges ? " ほか" : string.Empty).Append('\n');
            if (hidden.Count > 0) text.Append("隠した行: ").Append(string.Join(", ", hidden.Take(OverviewRanges))).Append(hidden.Count > OverviewRanges ? " ほか" : string.Empty).Append('\n');
            if (budget <= 0 || stop()) text.Append("（多いか時間がかかるので、途中まで）\n");
            return text.ToString();
        }

        private static void WalkRows(Scope scope, object ws, int top, int bottom, List<KeyValuePair<string, string>> heights, List<string> hidden, ref int budget, Func<bool> stop)
        {
            if (budget <= 0 || stop()) return;
            budget--;
            var rows = scope.Get(scope.Get(ws, "Range", scope.Get(ws, "Cells", top, 1), scope.Get(ws, "Cells", bottom, 1)), "EntireRow");
            var height = scope.Get(rows, "RowHeight");
            var isHidden = scope.Get(rows, "Hidden");
            if ((height != null && !(height is DBNull) && isHidden is bool) || top == bottom)
            {
                var name = top == bottom ? top.ToString(CultureInfo.InvariantCulture) : top + ":" + bottom;
                if (isHidden as bool? == true) hidden.Add(name);
                else if (height != null && !(height is DBNull)) heights.Add(new KeyValuePair<string, string>(name, Number(height)));
                return;
            }
            var middle = top + (bottom - top) / 2;
            WalkRows(scope, ws, top, middle, heights, hidden, ref budget, stop);
            WalkRows(scope, ws, middle + 1, bottom, heights, hidden, ref budget, stop);
        }

        // ---------------------------------------------------------------
        // 図形・グラフ・表
        // ---------------------------------------------------------------

        private static string ShapeText(Scope scope, object ws)
        {
            var shapes = scope.Get(ws, "Shapes");
            var count = Convert.ToInt32(scope.Get(shapes, "Count"), CultureInfo.InvariantCulture);
            var o = new StringBuilder();
            var listed = 0;
            for (var i = 1; i <= count && listed < OverviewItems; i++)
            {
                var shape = scope.Pick(shapes, "Item", i);
                var type = Convert.ToInt32(scope.Get(shape, "Type"), CultureInfo.InvariantCulture);
                if (type == 3 || type == 4) continue;   // グラフは下の段に。メモ（古い形のコメント）の図形は、コメント・メモの段に
                listed++;
                o.Append("- 「").Append(Text(scope.Get(shape, "Name"))).Append("」 ").Append(ShapeTypeName(type)).Append("・").Append(Place(scope, shape));
                var text = Attempt(() => Text(scope.Get(scope.Get(scope.Get(shape, "TextFrame2"), "TextRange"), "Text")));
                if (!string.IsNullOrEmpty(text)) o.Append("・文字「").Append(Clip(text)).Append("」");
                var fill = Attempt(() => scope.Get(scope.Get(shape, "Fill"), "Visible") as int? == 0 ? "なし" : ColorName(scope.Get(scope.Get(scope.Get(shape, "Fill"), "ForeColor"), "RGB")));
                if (fill != null) o.Append("・塗り ").Append(fill);
                o.Append('\n');
            }
            if (count > listed && listed >= OverviewItems) o.Append("（多いので、ここまで。全部で ").Append(count).Append(" 個）\n");
            return o.ToString();
        }

        private static string ChartText(Scope scope, object ws)
        {
            var charts = scope.Call(ws, "ChartObjects");
            var count = Convert.ToInt32(scope.Get(charts, "Count"), CultureInfo.InvariantCulture);
            var o = new StringBuilder();
            for (var i = 1; i <= count && i <= OverviewItems; i++)
            {
                var holder = scope.Pick(charts, "Item", i);
                var chart = scope.Get(holder, "Chart");
                o.Append("- 「").Append(Text(scope.Get(holder, "Name"))).Append("」 ").Append(ChartTypeName(Convert.ToInt32(scope.Get(chart, "ChartType"), CultureInfo.InvariantCulture)))
                    .Append("・").Append(Place(scope, holder));
                var title = Attempt(() => scope.Get(chart, "HasTitle") as bool? == true ? Text(scope.Get(scope.Get(chart, "ChartTitle"), "Text")) : null);
                if (!string.IsNullOrEmpty(title)) o.Append("・タイトル「").Append(Clip(title)).Append("」");
                var series = Attempt(() =>
                {
                    var list = scope.Call(chart, "SeriesCollection");
                    var n = Convert.ToInt32(scope.Get(list, "Count"), CultureInfo.InvariantCulture);
                    var formulas = new List<string>();
                    for (var j = 1; j <= n && j <= 6; j++) formulas.Add(Text(scope.Get(scope.Call(chart, "SeriesCollection", j), "Formula")));
                    return string.Join(" / ", formulas) + (n > 6 ? " ほか " + (n - 6) + " 系列" : string.Empty);
                });
                if (!string.IsNullOrEmpty(series)) o.Append("・系列 ").Append(series);
                o.Append('\n');
            }
            if (count > OverviewItems) o.Append("（多いので、ここまで。全部で ").Append(count).Append(" 個）\n");
            return o.ToString();
        }

        private static string TableText(Scope scope, object ws)
        {
            var o = new StringBuilder();
            var tables = scope.Get(ws, "ListObjects");
            var count = Convert.ToInt32(scope.Get(tables, "Count"), CultureInfo.InvariantCulture);
            for (var i = 1; i <= count && i <= OverviewItems; i++)
            {
                var table = scope.Pick(tables, "Item", i);
                o.Append("- テーブル「").Append(Text(scope.Get(table, "Name"))).Append("」 ").Append(Address(scope, scope.Get(table, "Range"))).Append('\n');
            }
            var pivots = scope.Call(ws, "PivotTables");
            var pivotCount = Convert.ToInt32(scope.Get(pivots, "Count"), CultureInfo.InvariantCulture);
            for (var i = 1; i <= pivotCount && i <= OverviewItems; i++)
            {
                var pivot = scope.Call(ws, "PivotTables", i);
                o.Append("- ピボットテーブル「").Append(Text(scope.Get(pivot, "Name"))).Append("」 ").Append(Address(scope, scope.Get(pivot, "TableRange1"))).Append('\n');
            }
            return o.ToString();
        }

        private static string NameText(Scope scope, object wb)
        {
            var names = scope.Get(wb, "Names");
            var count = Convert.ToInt32(scope.Get(names, "Count"), CultureInfo.InvariantCulture);
            var o = new StringBuilder();
            var listed = 0;
            for (var i = 1; i <= count && listed < OverviewItems; i++)
            {
                var name = scope.Pick(names, "Item", i);
                if (scope.Get(name, "Visible") as bool? == false) continue;
                listed++;
                o.Append("- ").Append(Text(scope.Get(name, "Name"))).Append(" = ").Append(Clip(Text(scope.Get(name, "RefersTo")))).Append('\n');
            }
            if (count > listed && listed >= OverviewItems) o.Append("（多いので、ここまで）\n");
            return o.ToString();
        }

        private static string ConditionText(Scope scope, object ws)
        {
            var conditions = scope.Get(scope.Get(ws, "Cells"), "FormatConditions");
            var count = Convert.ToInt32(scope.Get(conditions, "Count"), CultureInfo.InvariantCulture);
            var o = new StringBuilder();
            for (var i = 1; i <= count && i <= OverviewItems; i++)
            {
                var condition = scope.Pick(conditions, "Item", i);
                var type = Convert.ToInt32(scope.Get(condition, "Type"), CultureInfo.InvariantCulture);
                o.Append("- ").Append(Attempt(() => Address(scope, scope.Get(condition, "AppliesTo"))) ?? "?").Append(' ').Append(ConditionTypeName(type));
                var formula = Attempt(() => Text(scope.Get(condition, "Formula1")));
                if (!string.IsNullOrEmpty(formula)) o.Append(" ").Append(Clip(formula));
                var fill = Attempt(() => ColorName(scope.Get(scope.Get(condition, "Interior"), "Color")));
                if (fill != null && (type == 1 || type == 2)) o.Append("・塗り ").Append(fill);
                o.Append('\n');
            }
            if (count > OverviewItems) o.Append("（多いので、ここまで。全部で ").Append(count).Append(" 個）\n");
            return o.ToString();
        }

        private static string ValidationText(Scope scope, object ws)
        {
            try
            {
                return "入力規則のあるセル: " + Address(scope, scope.Call(scope.Get(ws, "Cells"), "SpecialCells", -4174)) + "（中身は台本で調べる）\n";   // xlCellTypeAllValidation
            }
            catch (Exception ex) when (!IsBusy(ex))
            {
                // 無いと「見つからない」のエラーになる（番号はほかの失敗と同じ 0x800A03EC なので、文で見分ける）。ほかのわけ（シートの保護など）は、調べられなかったと書く
                var notFound = (ex.InnerException ?? ex).Message.IndexOf("見つかりません", StringComparison.Ordinal) >= 0
                               || (ex.InnerException ?? ex).Message.IndexOf("No cells were found", StringComparison.OrdinalIgnoreCase) >= 0;
                return notFound ? null : "（調べられなかった: " + (ex.InnerException ?? ex).Message + "）\n";
            }
        }

        private static string CommentText(Scope scope, object ws)
        {
            var o = new StringBuilder();
            // スレッド形式のコメント（今の Excel）。1つごとに古い形のメモもできるので、そのセルのメモは重ねて出さない
            var threadedCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var threaded = Attempt(() =>
            {
                var list = scope.Get(ws, "CommentsThreaded");
                var n = Convert.ToInt32(scope.Get(list, "Count"), CultureInfo.InvariantCulture);
                var text = new StringBuilder();
                for (var i = 1; i <= n && i <= OverviewItems * 4; i++)
                {
                    var comment = scope.Pick(list, "Item", i);
                    var cell = Address(scope, scope.Get(comment, "Parent"));
                    threadedCells.Add(cell);   // 並べるのは 50 個までだが、重ねて出さないための覚えは多めに
                    if (i <= OverviewItems) text.Append("- コメント ").Append(cell).Append("「").Append(Clip(Text(scope.Pick(comment, "Text")))).Append("」\n");
                }
                if (n > OverviewItems) text.Append("（コメントが多いので、ここまで。全部で ").Append(n).Append(" 個）\n");
                return text.ToString();
            });
            if (!string.IsNullOrEmpty(threaded)) o.Append(threaded);
            var comments = scope.Get(ws, "Comments");
            var count = Convert.ToInt32(scope.Get(comments, "Count"), CultureInfo.InvariantCulture);
            var listedNotes = 0;
            for (var i = 1; i <= count && i <= OverviewItems * 5; i++)
            {
                var comment = scope.Pick(comments, "Item", i);
                var cell = Address(scope, scope.Get(comment, "Parent"));
                if (threadedCells.Contains(cell)) continue;
                if (listedNotes++ >= OverviewItems)
                {
                    o.Append("（メモが多いので、ここまで）\n");
                    break;
                }
                o.Append("- メモ ").Append(cell).Append("「").Append(Clip(Text(scope.Pick(comment, "Text")))).Append("」\n");
            }
            return o.ToString();
        }

        private static string LinkText(Scope scope, object ws)
        {
            var links = scope.Get(ws, "Hyperlinks");
            var count = Convert.ToInt32(scope.Get(links, "Count"), CultureInfo.InvariantCulture);
            var o = new StringBuilder();
            for (var i = 1; i <= count && i <= OverviewItems; i++)
            {
                var link = scope.Pick(links, "Item", i);
                var target = Text(scope.Get(link, "Address"));
                var sub = Text(scope.Get(link, "SubAddress"));
                o.Append("- ").Append(Attempt(() => Address(scope, scope.Get(link, "Range"))) ?? "?").Append(" → ").Append(Clip(target.Length > 0 ? target : sub)).Append('\n');
            }
            if (count > OverviewItems) o.Append("（多いので、ここまで）\n");
            return o.ToString();
        }

        // ---------------------------------------------------------------
        // 字にする
        // ---------------------------------------------------------------

        /// <summary>読めなければ null（Excel が応えないときは投げ直す）。</summary>
        private static string Attempt(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (!IsBusy(ex))
            {
                return null;
            }
        }

        private static string Place(Scope scope, object shape)
        {
            return Text(scope.Get(scope.Get(shape, "TopLeftCell"), "Address", false, false)) + "〜" + Text(scope.Get(scope.Get(shape, "BottomRightCell"), "Address", false, false));
        }

        private static string RangeName(int top, int left, int bottom, int right)
        {
            var first = ColumnName(left) + top.ToString(CultureInfo.InvariantCulture);
            return top == bottom && left == right ? first : first + ":" + ColumnName(right) + bottom.ToString(CultureInfo.InvariantCulture);
        }

        private static string Number(object value)
        {
            if (value is double) return ((double)value).ToString("0.##", CultureInfo.InvariantCulture);
            return Text(value);
        }

        /// <summary>Excel の色（BGR の数）を #RRGGBB にする。</summary>
        private static string ColorName(object value)
        {
            var color = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            var red = color & 0xFF;
            var green = (color >> 8) & 0xFF;
            var blue = (color >> 16) & 0xFF;
            return "#" + red.ToString("X2", CultureInfo.InvariantCulture) + green.ToString("X2", CultureInfo.InvariantCulture) + blue.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static string AlignName(int value)
        {
            switch (value)
            {
                case 1: return "標準";
                case -4131: return "左";
                case -4108: return "中央";
                case -4152: return "右";
                case 5: return "繰り返し";
                case -4130: return "両端";
                case 7: return "選択範囲内で中央";
                case -4117: return "均等割り付け";
                default: return value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string ShapeTypeName(int type)
        {
            switch (type)
            {
                case 1: return "図形";
                case 5: return "フリーフォーム";
                case 6: return "グループ";
                case 7: case 12: return "埋め込みオブジェクト";
                case 8: return "フォームの部品";
                case 9: return "線";
                case 11: return "リンクした画像";
                case 13: return "画像";
                case 15: return "ワードアート";
                case 17: return "テキストボックス";
                case 24: return "SmartArt";
                default: return "種類 " + type.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string ChartTypeName(int type)
        {
            switch (type)
            {
                case 51: return "集合縦棒";
                case 52: return "積み上げ縦棒";
                case 57: return "集合横棒";
                case 58: return "積み上げ横棒";
                case 4: return "折れ線";
                case 65: return "マーカー付き折れ線";
                case 5: return "円";
                case -4120: return "ドーナツ";
                case -4169: return "散布図";
                case 1: return "面";
                case -4151: return "レーダー";
                default: return "グラフの種類 " + type.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string ConditionTypeName(int type)
        {
            switch (type)
            {
                case 1: return "セルの値";
                case 2: return "式";
                case 3: return "カラースケール";
                case 4: return "データバー";
                case 5: return "上位・下位";
                case 6: return "アイコン";
                case 8: return "一意・重複";
                case 9: return "文字を含む";
                case 10: return "空白";
                case 11: return "日付";
                case 12: return "平均より上・下";
                default: return "種類 " + type.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
