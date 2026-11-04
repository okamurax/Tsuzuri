using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Editing;
using Tsuzuri.Export;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: リンクのクリック・本文へのドロップ・クリップボードからの貼り付け。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // リンクのクリック（SPEC 5章）
        // ---------------------------------------------------------------

        private void OnLinkActivated(object sender, LinkActivatedEventArgs e)
        {
            var view = sender as MarkdownView;
            if (view == null) return;

            var target = e.Target;

            if (target.StartsWith("#", StringComparison.Ordinal))
            {
                var anchor = target.Substring(1);

                // 同じファイルの中のページ（SPEC 5章）。ファイル操作は一切起きない。
                if (view.Owner != null && view.Owner.Contains(anchor))
                {
                    PushPage(view, view.Owner, anchor);
                    return;
                }

                // ページでなければ、同じページの中の見出しへ飛ぶ（HTML と同じページ内リンク）。
                var line = view.FindHeading(anchor);
                if (line > 0)
                {
                    view.GoToLine(line);
                    return;
                }

                // 同じページに無ければ、同じファイルのほかのページの見出しを探す（2026-11-02。行番号の右クリックでコピーしたリンクは
                // 見出しの名前だけで書くので、ほかのページの見出しを指すことがある）。
                if (view.Owner != null && GoToAnchor(view.Owner, anchor)) return;

                Status("見つかりません: " + target);
                return;
            }

            // 外部の画像（WordPress の記事の画像など）は、ブラウザで開かず、画像をクリップボードに入れる（2026-10-30。SPEC 5.4・20章）。
            // 手元に保存はしない。描画ソフトに貼って直し、コピーしてエディタに貼り直せば、アップロードのときにその画像が上がる。
            if (e.IsImage && (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                              target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                CopyRemoteImage(target);
                return;
            }

            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                Launch(target);
                return;
            }

            var path = view.ResolvePath(target);
            if (path == null)
            {
                Status("リンク先を解決できません: " + target);
                return;
            }

            // 図（同じ名前の .json がある画像）は、図エディタで描き直す（SPEC 8.4.3）。
            if (File.Exists(path) && TryEditDrawing(path)) return;

            var isMarkdown = IsMarkdown(path);

            if (!File.Exists(path))
            {
                if (!isMarkdown)
                {
                    Status("ファイルが見つかりません: " + target);
                    return;
                }

                // リンク切れはソフトで管理しない方針なので、その場で作るかだけ尋ねる。
                var answer = MessageBox.Show(this,
                    "リンク先のファイルがありません。作成しますか？" + Environment.NewLine + path,
                    "サブページ", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) return;

                var title = string.IsNullOrEmpty(e.Label)
                    ? Path.GetFileNameWithoutExtension(path)
                    : e.Label;

                if (!TryCreatePage(path, title)) return;
            }

            if (isMarkdown) OpenLinkedFile(path, AnchorOf(target));
            else Launch(path);
        }

        /// <summary>リンク先の # の後ろ（見出しの名前かページの番号）。無ければ null。</summary>
        private static string AnchorOf(string target)
        {
            var hash = target.IndexOf('#');
            if (hash < 0 || hash == target.Length - 1) return null;

            return target.Substring(hash + 1);
        }

        /// <summary>
        /// リンクで別の .md を開く。新しい画面（タブ）で開く（2026-11-02。前はクリックしたページの右隣に積んでいた）。
        /// 別の文書を並べて行き来するため。anchor（見出しの名前・ページの番号）があれば、開いてからそこへ移る。
        /// ・この画面ですでに開いている（同じ文書を指すリンク・前の版で右に積んだ文書）なら、この画面の中で移る
        /// ・ほかの画面で開いていれば、その画面を前に出して移る（1つの文書は1つの画面だけ）。休止している画面なら再開する（移るのは再開したあとに自分で）
        /// </summary>
        private void OpenLinkedFile(string path, string anchor)
        {
            path = ShellActions.CanonicalPath(path);

            var document = FindOpenDocument(path);
            if (document != null)
            {
                GoToAnchor(document, anchor);
                return;
            }

            var holder = WindowHolding(path);
            if (holder != null)
            {
                holder.BringForward(this);
                var held = holder.FindOpenDocument(path);
                if (held != null) holder.GoToAnchor(held, anchor);
                return;
            }

            if (ShowHolderOf(path)) return;

            // 開くのは、新しい画面の支度（Loaded）が済んでから（「新規作成」と同じ）。開けなければ（フォルダを作るのを断られた等）、空の画面を閉じて戻る。
            var window = new MainWindow(_settings, this, null);
            window.Loaded += delegate
            {
                window.OpenRoot(path);

                var opened = window.FindOpenDocument(path);
                if (opened != null)
                {
                    window.GoToAnchor(opened, anchor);
                    return;
                }

                window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    window.CloseShowingFirst();
                    if (OpenWindows.Contains(this)) BringForward(null);
                }), DispatcherPriority.Background);
            };
            window.BringForward(this);
        }

        /// <summary>
        /// その文書の、anchor（ページの番号か見出しの名前）の所へ移る。anchor が無ければルートページを出す。見つかれば true。
        /// 見出しは、今出ているその文書のページを先に探し、無ければルートから順に全部のページを探す。ページはルートからのリンクの連鎖どおりに積む（ブックマークと同じ）。
        /// </summary>
        private bool GoToAnchor(PageDocument document, string anchor)
        {
            if (string.IsNullOrEmpty(anchor) || document.Contains(anchor))
            {
                return ShowPage(document, string.IsNullOrEmpty(anchor) ? PageDocument.RootId : anchor) != null;
            }

            var order = new List<string>();
            var shown = Active != null && ReferenceEquals(Active.Owner, document) ? Active.PageId : null;
            if (shown != null) order.Add(shown);
            order.AddRange(document.Pages.Select(page => page.Id).Where(id => !string.Equals(id, shown, StringComparison.Ordinal)));

            foreach (var id in order)
            {
                var heading = MarkdownParser.Headings(document.TextOf(id)).FirstOrDefault(entry => MarkdownView.AnchorMatches(entry.Text, anchor));
                if (heading == null) continue;

                var view = FindView(document, id);
                if (view == null || !_visible.Contains(view)) view = ShowPage(document, id);
                if (view == null) return true;   // 孤立ページ・保存を取りやめた（ShowPage が知らせる）

                var line = heading.Line;
                Dispatcher.BeginInvoke(new Action(delegate { view.GoToLine(line); }), DispatcherPriority.Loaded);
                return true;
            }

            Status("見出しが見つかりません: " + anchor);
            return true;
        }

        // ---------------------------------------------------------------
        // 見出しへのリンクのコピー（SPEC 5.5。2026-11-02）
        //
        // 行番号の右クリックで、その行の見出しへのリンクをクリップボードに入れる。中身は文書のパスと見出しの名前で持ち、
        // 貼るときに、貼る先の文書から見たリンクに組み立てる（同じ文書なら #見出し、ほかの文書なら 相対パス#見出し）。
        // コピーする側は貼る先を知らないので、1つの操作で両方に使えるよう、組み立ては貼るときにする。
        // ほかのアプリに貼ったときは、文字（絶対パスのリンク）が入る。
        // ---------------------------------------------------------------

        private const string HeadingLinkFormat = "Tsuzuri.HeadingLink";

        private void CopyHeadingLink(MarkdownView view, int lineNumber)
        {
            if (string.IsNullOrEmpty(view.FilePath)) return;

            if (WordPressLinkOf(view) != null)
            {
                Status("WordPress の記事へのリンクは作れません（記事は一時ファイルで開いているため）。");
                return;
            }

            // 見出しより前の行は、そのページを指す（ルートページならファイルだけ）。
            var heading = view.HeadingAbove(lineNumber);
            var anchor = heading != null ? HeadingText.Display(heading.Text).Trim()
                : view.PageId != PageDocument.RootId ? view.PageId : string.Empty;
            var label = heading != null ? anchor : view.Title;

            var data = new DataObject();
            data.SetData(HeadingLinkFormat, view.FilePath + "\n" + anchor + "\n" + label);
            data.SetText(LinkText(label, Slashes(view.FilePath) + (anchor.Length > 0 ? "#" + anchor : string.Empty)));

            try
            {
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex)
            {
                Status("クリップボードに入れられませんでした: " + ex.Message);
                return;
            }

            Status("リンクをコピーしました: " + label + "（本文に貼ると、そこからのリンクになります）");
        }

        /// <summary>コピーした見出しへのリンクを、貼る先の文書から見たリンクにして入れる。</summary>
        private void PasteHeadingLink(string data, MarkdownView view)
        {
            var parts = data.Split('\n');
            if (parts.Length < 3) return;
            if (!CanInsertInto(view)) return;

            if (WordPressLinkOf(view) != null)
            {
                Status("WordPress の記事には、手元の文書へのリンクは入れられません。");
                return;
            }

            var path = parts[0];
            var anchor = parts[1];
            var label = parts[2];

            string target;
            if (SamePath(view.FilePath, path))
            {
                target = anchor.Length > 0 ? "#" + anchor : Path.GetFileName(path);
            }
            else
            {
                target = ToRelativePath(Path.GetDirectoryName(view.FilePath), path) + (anchor.Length > 0 ? "#" + anchor : string.Empty);
            }

            view.InsertAtCaret(LinkText(label, target));
            Status("リンクを入れました: " + label);
        }

        private static string LinkText(string label, string target)
        {
            return "[" + EscapeLabel(label) + "](" + ToLinkTarget(target) + ")";
        }

        /// <summary>
        /// ランチャー・ファイラーのスクリプト（.bat・.cmd・.ps1）を別の窓で動かす（2026-10-30。前の 2026-10-27 からは AI の欄のタブで動かしていた）。
        /// .bat・.cmd はダブルクリックと同じ（コマンドプロンプトの窓）。.ps1 は Windows に任せるとメモ帳が開くので、PowerShell の窓で動かす。
        /// 実行ポリシーで断られないよう、その実行だけ Bypass にする（利用者が自分で登録して押したものなので）。
        /// 本文のリンクからは使わない（リンクの .ps1 を押しただけで動かさない。リンクは Launch）。
        /// </summary>
        private void RunScript(string script)
        {
            if (!string.Equals(Path.GetExtension(script), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                Launch(script);
                return;
            }

            try
            {
                var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                var start = new ProcessStartInfo(powershell, "-NoLogo -ExecutionPolicy Bypass -File \"" + script + "\"");
                start.UseShellExecute = true;   // 自分の窓を持たせる

                var folder = Path.GetDirectoryName(script);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) start.WorkingDirectory = folder;

                Process.Start(start);
            }
            catch (Exception ex)
            {
                Status("動かせませんでした: " + ex.Message);
            }
        }

        /// <summary>
        /// 外部の画像を取ってきて、クリップボードに入れる（裏のスレッドで取る）。ファイルには保存しない。
        /// "PNG" 形式（透明を保つ。PNG ならそのままのバイト列）と、ふつうの画像の形式の両方で入れる。
        /// このエディタの貼り付け（Ctrl+V）は "PNG" を先に使う。描画ソフト（ペイントなど）はふつうの画像の形式を使う。
        /// </summary>
        private async void CopyRemoteImage(string url)
        {
            Status("画像を取ってきています… " + url);

            byte[] data;
            BitmapSource image;
            try
            {
                data = await System.Threading.Tasks.Task.Run(() =>
                {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                    var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    request.UserAgent = "Tsuzuri";
                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 30000;

                    using (var response = request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        return memory.ToArray();
                    }
                });

                using (var stream = new MemoryStream(data))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    image = ImageOrientation.Apply(frame, ImageOrientation.Read(frame));   // 写真の向きの印を直してから（9章）
                    image.Freeze();
                }
            }
            catch (Exception ex)
            {
                Status("画像を取ってこられませんでした: " + ex.Message);
                return;
            }

            // PNG はそのままのバイト列を使う（向きの印を持たないので直す必要が無く、作り直すと透明や色が変わりかねない）。
            // JPEG などは、向きを直した画像から PNG を作る。
            var isPng = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
            var png = isPng ? data : null;
            if (png == null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var memory = new MemoryStream())
                {
                    encoder.Save(memory);
                    png = memory.ToArray();
                }
            }

            var dataObject = new DataObject();
            dataObject.SetImage(image);
            dataObject.SetData("PNG", new MemoryStream(png));

            try
            {
                Clipboard.SetDataObject(dataObject, true);
            }
            catch (COMException)
            {
                // ほかのアプリがクリップボードを開いている。少し待ってもう一度だけ。
                try
                {
                    await System.Threading.Tasks.Task.Delay(200);
                    Clipboard.SetDataObject(dataObject, true);
                }
                catch (Exception ex)
                {
                    Status("クリップボードに入れられませんでした: " + ex.Message);
                    return;
                }
            }

            Status("画像をクリップボードに入れました（" + image.PixelWidth + "×" + image.PixelHeight + "）。描画ソフトに貼って直し、ここに貼り直せばアップロードで上がります。");
        }

        private void Launch(string target)
        {
            try
            {
                // 作業フォルダはそのファイルの場所にする。バッチが自分の隣のファイルを相対パスで探せるように。
                var start = new ProcessStartInfo(target);
                start.UseShellExecute = true;

                var folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) start.WorkingDirectory = folder;

                Process.Start(start);
            }
            catch (Exception ex)
            {
                Status("開けませんでした: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 本文へのドロップ = リンク挿入（SPEC 9章）
        //
        // ファイルのコピーも移動もしない。元の場所を指すリンクを書くだけ。
        //
        // AvalonEdit も自前でドロップを受け取るため、Preview（親から子へ降りる
        // 経路）で先に捕まえて Handled にする。そうしないとエディタが
        // ファイルのパスをただの文字列として貼り付けてしまう。
        // ---------------------------------------------------------------

        /// <summary>
        /// 入る・動く・落とすの全部を、素の経路と処理済みの経路の両方で受ける。
        /// どれか1つでも届けば動くようにしておく。
        /// </summary>
        private void AddDropHandlers(TextArea area)
        {
            var over = new DragEventHandler(Editor_DragOver);
            var drop = new DragEventHandler(Editor_Drop);

            area.AddHandler(DragDrop.PreviewDragEnterEvent, over, true);
            area.AddHandler(DragDrop.PreviewDragOverEvent, over, true);
            area.AddHandler(DragDrop.DragEnterEvent, over, true);
            area.AddHandler(DragDrop.DragOverEvent, over, true);

            area.AddHandler(DragDrop.PreviewDropEvent, drop, true);
            area.AddHandler(DragDrop.DropEvent, drop, true);
        }

        private MarkdownView OwnerOf(object sender)
        {
            var area = sender as TextArea;
            if (area == null) return null;

            foreach (var view in _stack)
            {
                if (ReferenceEquals(view.TextArea, area)) return view;
            }

            return null;
        }

        private void Editor_DragOver(object sender, DragEventArgs e)
        {
            // 文字のドラッグ（選択範囲を掴んで移動・Ctrl でコピー）は AvalonEdit に任せる。
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Handled = true;

            try
            {
                var view = OwnerOf(sender);
                var paths = GetDroppedFiles(e);
                var ready = view != null && !string.IsNullOrEmpty(view.FilePath) && paths.Length > 0;

                e.Effects = ready ? DragDropEffects.Copy : DragDropEffects.None;
            }
            catch (Exception ex)
            {
                e.Effects = DragDropEffects.None;
                Status("ドラッグ中に例外: " + ex.GetType().Name + " — " + ex.Message);
            }
        }

        private void Editor_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            e.Handled = true;

            // 素の経路と処理済みの経路の両方で呼ばれるので、二重に挿さないようにする。
            if (ReferenceEquals(_handledDrop, e.Data)) return;
            _handledDrop = e.Data;

            try
            {
                InsertFiles(GetDroppedFiles(e), OwnerOf(sender));
            }
            catch (Exception ex)
            {
                Status("ドロップで例外: " + ex.GetType().Name + " — " + ex.Message);
            }
        }

        private bool CanInsertInto(MarkdownView view)
        {
            if (view != null && view.IsReadOnly)
            {
                Status("このページはファイルから消えているため、入れられません。");
                return false;
            }

            if (view != null && !string.IsNullOrEmpty(view.FilePath)) return true;

            Status("リンクを入れる本文がありません。先にファイルを開いてください。");
            return false;
        }

        private void InsertFiles(string[] paths, MarkdownView view)
        {
            if (paths.Length == 0) return;
            if (!CanInsertInto(view)) return;

            var newLine = view.NewLine;
            var text = new StringBuilder();

            // 1つでも取り込めなければ、何も入れない。元の場所を指すリンクで代えると、文書と添付が離れてしまう。
            try
            {
                for (var i = 0; i < paths.Length; i++)
                {
                    if (i > 0) text.Append(newLine);
                    text.Append(BuildFileLink(view.FilePath, paths[i]));
                }
            }
            catch (Exception ex)
            {
                Status("取り込めなかったため、リンクを入れませんでした: " + ex.Message);
                return;
            }

            view.InsertAtCaret(text.ToString());

            Status(paths.Length == 1
                ? "リンクを挿入しました: " + paths[0]
                : paths.Length + " 件のリンクを挿入しました。");
        }

        // ---------------------------------------------------------------
        // クリップボードからの貼り付け（SPEC 9章）
        //
        // ファイルと画像はドロップと同じく添付フォルダに取り込んでリンクを入れる。
        // 文字は AvalonEdit の貼り付けにそのまま任せる。
        // ---------------------------------------------------------------

        private void AddPasteHandlers(TextArea area)
        {
            CommandManager.AddPreviewCanExecuteHandler(area, Editor_CanPaste);
            CommandManager.AddPreviewExecutedHandler(area, Editor_Paste);
        }

        /// <summary>
        /// AvalonEdit は文字があるときしか貼り付けを許さない。
        /// 画像だけのクリップボードでは Ctrl+V が素通りするので、ここで許す。
        /// </summary>
        private void Editor_CanPaste(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Command != ApplicationCommands.Paste) return;
            if (!HasAttachmentOnClipboard()) return;

            e.CanExecute = true;
            e.Handled = true;
        }

        private void Editor_Paste(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command != ApplicationCommands.Paste) return;

            try
            {
                // 行番号の右クリックでコピーした見出しへのリンク（文字も一緒に入っているので、文字より先に見る）。
                if (Clipboard.ContainsData(HeadingLinkFormat))
                {
                    var link = Clipboard.GetData(HeadingLinkFormat) as string;
                    if (link != null)
                    {
                        e.Handled = true;
                        PasteHeadingLink(link, OwnerOf(sender));
                        return;
                    }
                }

                if (Clipboard.ContainsFileDropList())
                {
                    e.Handled = true;

                    var paths = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToArray();
                    InsertFiles(paths, OwnerOf(sender));
                    return;
                }

                // 文字もあるなら文字を優先する。Excel や Word は文字と一緒に
                // 表の絵も載せるので、画像を先に見ると表が画像で貼られてしまう。
                if (Clipboard.ContainsText()) return;

                var png = ReadClipboardPng();
                if (png == null) return;

                e.Handled = true;
                InsertPastedImage(png, OwnerOf(sender));
            }
            catch (Exception ex)
            {
                e.Handled = true;
                Status("貼り付けで例外: " + ex.GetType().Name + " — " + ex.Message);
            }
        }

        /// <summary>取り込んでリンクにすべきものがクリップボードにあるか。</summary>
        private static bool HasAttachmentOnClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) return true;
                if (Clipboard.ContainsText()) return false;

                return Clipboard.ContainsData("PNG") || Clipboard.ContainsImage();
            }
            catch (Exception)
            {
                // 他のアプリがクリップボードを握っていると例外になる。
                return false;
            }
        }

        /// <summary>
        /// クリップボードの画像を PNG のバイト列で取り出す。
        ///
        /// "PNG" 形式があればそのまま使う。Bitmap 形式から作り直すと、
        /// 透明部分が黒くつぶれたり全体が透明になったりすることがある。
        /// </summary>
        private static byte[] ReadClipboardPng()
        {
            if (Clipboard.ContainsData("PNG"))
            {
                var stream = Clipboard.GetData("PNG") as MemoryStream;
                if (stream != null) return stream.ToArray();
            }

            if (!Clipboard.ContainsImage()) return null;

            var image = Clipboard.GetImage();
            if (image == null) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return output.ToArray();
            }
        }

        /// <summary>貼り付けた画像を添付フォルダに保存し、カーソル位置にリンクを入れる。</summary>
        private void InsertPastedImage(byte[] png, MarkdownView view)
        {
            if (!CanInsertInto(view)) return;

            string folder, name;

            try
            {
                // フォルダは開いたときに必ずある（SPEC 4章）。ここでは作らない。
                folder = Attachments.FolderOf(view.FilePath);

                name = "貼り付け " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png";
                if (File.Exists(Path.Combine(folder, name))) name = UniqueName(folder, name);

                // CreateNew: 万一同じ名前が先にできていても上書きしない。
                using (var file = new FileStream(Path.Combine(folder, name), FileMode.CreateNew))
                {
                    file.Write(png, 0, png.Length);
                }
            }
            catch (Exception ex)
            {
                Status("画像を保存できませんでした: " + ex.Message);
                return;
            }

            var label = Path.GetFileNameWithoutExtension(name);
            view.InsertAtCaret("![" + EscapeLabel(label) + "](" + ToLinkTarget(name) + ")");

            Status("画像を取り込みました: " + Path.GetFileName(folder) + "/" + name);
        }

        private string BuildFileLink(string documentPath, string source)
        {
            var label = Path.GetFileNameWithoutExtension(source);
            var target = Localize(documentPath, source);
            var prefix = IsImage(source) ? "!" : string.Empty;

            return prefix + "[" + EscapeLabel(label) + "](" + ToLinkTarget(target) + ")";
        }

        /// <summary>
        /// 文書の隣の、文書と同じ名前のフォルダに取り込み、そこからの相対パスを返す（SPEC 9章）。
        /// リンクにはフォルダ名を書かない。文書の名前を変えてフォルダも揃えたとき、切れないように。
        ///
        /// 文書と添付が同じ場所にあるので、まとめてどこへ移しても切れない。
        /// 実行ファイルの隣に集めると、文書が外にあるときに長い相対パスになり、
        /// 文書か実行ファイルのどちらかを動かした時点で切れてしまう。
        /// </summary>
        private string Localize(string documentPath, string source)
        {
            var baseDirectory = Path.GetDirectoryName(documentPath);

            // Markdown は文書なので取り込まない。複製すると本体が二つに分かれる。
            if (IsMarkdown(source)) return ToRelativePath(baseDirectory, source);

            var folder = Attachments.FolderOf(documentPath);

            // すでに添付フォルダの中のものは取り込み直さない。
            if (IsInside(folder, source)) return ToRelativePath(folder, source);

            // フォルダは開いたときに必ずある（SPEC 4章）。ここでは作らない。取り込めなければ例外（InsertFiles が止める）。
            var name = Path.GetFileName(source);
            var destination = Path.Combine(folder, name);

            if (!File.Exists(destination))
            {
                File.Copy(source, destination);
            }
            else if (!SameContent(destination, source))
            {
                // 同じ名前で中身が違うものは、別名で取り込む。
                name = UniqueName(folder, name);
                File.Copy(source, Path.Combine(folder, name));
            }

            return name;
        }

        /// <summary>同じものとみなせるか。大きさと更新時刻で足りる。</summary>
        private static bool SameContent(string a, string b)
        {
            try
            {
                var left = new FileInfo(a);
                var right = new FileInfo(b);

                return left.Length == right.Length
                    && left.LastWriteTimeUtc == right.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string UniqueName(string folder, string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);

            for (var n = 2; n < 1000; n++)
            {
                var candidate = stem + " (" + n + ")" + extension;
                if (!File.Exists(Path.Combine(folder, candidate))) return candidate;
            }

            return Guid.NewGuid().ToString("N") + extension;
        }

        private static bool IsInside(string folder, string path)
        {
            try
            {
                var full = Path.GetFullPath(folder);
                var separator = Path.DirectorySeparatorChar.ToString();

                if (!full.EndsWith(separator, StringComparison.Ordinal)) full += separator;

                return Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 同じドライブにあれば相対パス、違えば絶対パスにする。
        /// .NET Framework には Path.GetRelativePath が無いので Uri で求める。
        ///
        /// 区切りは必ず "/" にする。Markdown ではバックスラッシュがエスケープ文字なので、
        /// Windows のパスをそのまま書くと \\Mac\Home のような先頭が1文字に潰れて壊れる。
        /// Windows は "/" 区切りのパスも受け付けるため、これで両立する。
        /// </summary>
        private static string ToRelativePath(string baseDirectory, string target)
        {
            try
            {
                if (!string.Equals(Path.GetPathRoot(baseDirectory), Path.GetPathRoot(target),
                        StringComparison.OrdinalIgnoreCase))
                    return Slashes(target);

                var separator = Path.DirectorySeparatorChar.ToString();
                if (!baseDirectory.EndsWith(separator, StringComparison.Ordinal))
                    baseDirectory += separator;

                var relative = new Uri(baseDirectory).MakeRelativeUri(new Uri(target));

                return Slashes(Uri.UnescapeDataString(relative.ToString()));
            }
            catch (UriFormatException)
            {
                return Slashes(target);
            }
            catch (ArgumentException)
            {
                return Slashes(target);
            }
        }

        private static string Slashes(string path)
        {
            return path.Replace('\\', '/');
        }

        private static bool IsMarkdown(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImage(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension)) return false;

            switch (extension.ToLowerInvariant())
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                case ".svg":
                    return true;
                default:
                    return false;
            }
        }

        private static string[] GetDroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return new string[0];

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            return paths == null ? new string[0] : paths.Where(File.Exists).ToArray();
        }
    }
}
