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
    // MainWindow の一部: ツールバーと書き出し。
    // MainWindow.xaml.cs が大きくなりすぎたので、受け持ちごとにファイルを分けた（2026-10-14。中身は変えていない）。
    public partial class MainWindow
    {
        // ---------------------------------------------------------------
        // ツールバー（SPEC 11章）
        //
        // メニューバーは持たない。操作はすべてツールバー・右クリック・
        // ドラッグ&ドロップから届く。ファイルを開くのはファイラーとランチャーから（2026-10-14。「新規」「開く」はやめた）。
        // 新しく作るのは、ファイラーの右クリックと、ツールバーの「新規作成」（2026-11-01 に戻した）。
        // ---------------------------------------------------------------

        /// <summary>
        /// ファイラーの右クリック「新しいファイル…」で .md を作るとき。そのパスに作って開く。
        /// 同じ名前があれば作らない（ファイラーが先に確かめるが、その間に作られることもある）。
        /// </summary>
        private bool CreateFromFiler(string path)
        {
            if (File.Exists(path))
            {
                Status("同じ名前のファイルがあります: " + Path.GetFileName(path));
                return false;
            }

            if (!TryCreatePage(path, Path.GetFileNameWithoutExtension(path))) return false;

            // ファイラーで作ったときだけ、.文書名/ も黙って作る（SPEC 4章）。作れなければ開くときに尋ねる。
            try
            {
                Directory.CreateDirectory(Attachments.FolderOf(path));
            }
            catch (Exception)
            {
            }

            OpenRoot(path);
            return true;
        }

        /// <summary>
        /// そのファイル（フォルダならその中のどれか）をエディタで開いているか。
        /// ファイラーで名前を変える・消す前に確かめる。開いているものを外で変えると、エディタが保存先を失うため。
        /// </summary>
        private bool IsInUse(string path)
        {
            // ファイラーから来たパスを本当のパスに揃える。開いているファイルのパスは開く入口で揃えてある。
            var target = ShellActions.CanonicalPath(path);

            foreach (var view in _stack)
            {
                var open = view.FilePath;

                if (string.Equals(open, target, StringComparison.OrdinalIgnoreCase)) return true;
                if (open.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!HasDocument)
            {
                Status("開いているファイルがありません。");
                return;
            }

            // 失敗したときは SaveAll が理由を出しているので、上書きしない。
            if (!SaveAll()) return;

            var wordPress = WordPressLinkOf(Active);
            if (wordPress != null) Status("保存しました（手元の一時ファイル）。WordPress へは「WordPress」→「アップロード」で送ります。");
            else Status("保存しました: " + Active.FilePath);
        }

        /// <summary>「終了」。アプリごと（全部の画面を）閉じる（2026-10-31。画面1つだけを閉じるのはタブの ×）。</summary>
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            CloseAllWindows();
        }

        /// <summary>
        /// ツールバーの左端の「新規作成」（2026-11-01。SPEC 4章）。.md を作り、新しい画面（タブ）で開く。
        /// 何も開いていない画面なら、新しい画面を作らずにその画面で開く。ほかの画面で開いている文書を選んだら、その画面を前に出す。
        /// </summary>
        private void New_Click(object sender, RoutedEventArgs e)
        {
            var path = AskNewDocument("新しい Markdown を作る");
            if (path == null) return;

            if (!HasDocument)
            {
                OpenRoot(path);
                return;
            }

            if (_stack.Any(view => SamePath(view.FilePath, path)))
            {
                Status("この文書はこの画面で開いています: " + path);
                return;
            }

            var holder = WindowHolding(path);
            if (holder != null)
            {
                holder.BringForward(this);
                holder.Status("この文書はこの画面で開いています: " + path);
                return;
            }

            // 開くのは、新しい画面の支度（Loaded）が済んでから。
            var window = new MainWindow(_settings, this, null);
            window.Loaded += delegate { window.OpenRoot(path); };
            window.BringForward(this);
        }

        /// <summary>
        /// Windows のファイルを作る画面で .md の場所と名前を決めてもらい、作る（2026-11-01）。作ったファイルのパスを返す。やめたら null。
        /// 作り方はファイラーの「新しいファイル…」と同じ（見出し「# 名前」を書き、.文書名/ も黙って作る）。
        /// 今あるファイルを選んだら、書き換えずにそのパスを返す（呼び出し側はそれを開く）。
        /// ツールバーの「新規作成」と、何も開いていない画面で AI を始めるとき（CanStartAi）に使う。
        /// </summary>
        private string AskNewDocument(string title)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = "Markdown (*.md)|*.md",
                DefaultExt = ".md",
                FileName = "新しい文書.md",
                OverwritePrompt = false,   // 今あるファイルを選んだら、書き換えずにそれを開く
            };
            if (_filer != null && !string.IsNullOrEmpty(_filer.Folder) && Directory.Exists(_filer.Folder))
                dialog.InitialDirectory = _filer.Folder;

            if (dialog.ShowDialog(this) != true) return null;

            var path = dialog.FileName;
            if (File.Exists(path)) return path;

            if (!TryCreatePage(path, Path.GetFileNameWithoutExtension(path))) return null;
            try
            {
                Directory.CreateDirectory(Attachments.FolderOf(path));
            }
            catch (Exception)
            {
                // 作れなければ開くときに尋ねる（CreateFromFiler と同じ）
            }
            return path;
        }

        /// <summary>ツールバーの「ランチャー」（2026-10-31。前はページの上端の段）。ランチャーとファイラーを出し入れする（SPEC 4章）。</summary>
        private void Launcher_Click(object sender, RoutedEventArgs e)
        {
            ToggleLauncher();
        }

        /// <summary>ツールバーの「AI」（2026-10-31。前はページの上端の段）。AI の欄を出し入れする（SPEC 19章）。</summary>
        private void Ai_Click(object sender, RoutedEventArgs e)
        {
            ToggleAi();
        }

        /// <summary>
        /// タイトルバーの代わり（2026-10-17。SPEC 2章）。ツールバーの空いているところをドラッグするとウィンドウを動かし、
        /// ダブルクリックすると最大化と元の大きさを切り替える。ボタンの上では、ボタンが先に受け取るのでここには来ない。
        /// </summary>
        private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            if (WindowState == WindowState.Normal) DragMove();
        }

        // ---------------------------------------------------------------
        // 図（SPEC 8.4.3。2026-10-18）
        //
        // 図エディタ（DrawingEditor）で直線・四角・丸・文字・寸法線を描き、文書の添付フォルダに
        // 同じ名前の .png（本文に表示する・Word に載る）と .json（描き直すため）で置く。本文には画像のリンクを入れる。
        // 本文の図を押すと、同じ名前の .json がある画像なら、図エディタで描き直せる（OnLinkActivated）。
        // ---------------------------------------------------------------

        /// <summary>
        /// ツールバーの「図を追加」。図エディタで描き、保存したら、カーソルの位置に図のリンクを入れる。
        /// 図を書けなければ、控えを残して知らせ、図エディタで開き直すか尋ねる（2026-10-27。WriteDrawing）。
        /// </summary>
        private void InsertDrawing_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (!CanInsertInto(view)) return;

            string json = null;   // 開き直すときは、書けなかった図から
            while (true)
            {
                DrawingEditor.DrawingResult result;
                try
                {
                    result = DrawingEditor.DrawingEditorHost.Edit(this, json);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "図エディタでエラーが起きました。" + Environment.NewLine + Environment.NewLine + ex,
                        "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (result == null) return;   // 保存せずに閉じた

                string folder = null, stem;
                try
                {
                    // フォルダは開いたときに必ずある（SPEC 4章）。名前は画像の貼り付けと同じく日時から。
                    folder = Attachments.FolderOf(view.FilePath);
                    stem = "図 " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                    for (var n = 2; File.Exists(Path.Combine(folder, stem + ".png")) || File.Exists(Path.Combine(folder, stem + ".json")); n++)
                        stem = "図 " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + " (" + n + ")";
                }
                catch (Exception)
                {
                    stem = "図 " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");   // 書くところで失敗として扱う（控えは一時フォルダへ）
                }

                // isNew: 万一同じ名前が先にできていても上書きしない。
                bool reopen;
                if (WriteDrawing(folder, stem, result, true, out reopen))
                {
                    view.InsertAtCaret("![" + EscapeLabel(stem) + "](" + ToLinkTarget(stem + ".png") + ")");
                    Status("図を入れました: " + Path.GetFileName(folder) + "/" + stem + ".png");
                    return;
                }

                if (!reopen) return;
                json = result.Json;
            }
        }

        /// <summary>
        /// 本文の画像を押したとき、それが図（同じ名前の .json が図エディタの図）なら、図エディタで描き直す。
        /// 図でなければ false（いつもどおり Windows の既定のアプリで開く）。
        /// </summary>
        private bool TryEditDrawing(string imagePath)
        {
            if (!string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase)) return false;

            var jsonPath = Path.ChangeExtension(imagePath, ".json");
            if (!File.Exists(jsonPath)) return false;

            string json;
            try
            {
                json = File.ReadAllText(jsonPath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return false;
            }

            if (!DrawingEditor.DrawingEditorHost.IsDrawing(json)) return false;

            var folder = Path.GetDirectoryName(imagePath);
            var stem = Path.GetFileNameWithoutExtension(imagePath);

            while (true)
            {
                DrawingEditor.DrawingResult result;
                try
                {
                    result = DrawingEditor.DrawingEditorHost.Edit(this, json);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "図エディタでエラーが起きました。図は変えていません。" + Environment.NewLine + Environment.NewLine + ex,
                        "図エディタ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                }

                if (result == null) return true;   // 保存せずに閉じた

                // 図を書けなければ、控えを残して知らせ、図エディタで開き直すか尋ねる（2026-10-27。WriteDrawing）。
                bool reopen;
                if (WriteDrawing(folder, stem, result, false, out reopen)) break;

                if (!reopen) return true;
                json = result.Json;
            }

            // 同じ図を見せているページすべてで、画像を読み直す（読んだ画像は更新時刻で見分けて覚えている）。
            foreach (var view in _stack) view.RefreshImages();

            Status("図を保存しました: " + Path.GetFileName(imagePath));
            return true;
        }

        /// <summary>
        /// 図エディタで保存した図を、folder に stem.json・stem.png で書く。書けたら true（2026-10-27）。
        ///
        /// 前は図エディタを閉じてから書き、書けないと（読み取り専用・Dropbox がつかんでいる・共有が切れている等）描いた図がすべて消えた。
        /// .json を書いてから .png を書いたので、.png で失敗すると2つが食い違った。いまは:
        /// ・先に両方の一時ファイル（.saving）を書き、両方書けてから置き換える。一時ファイルで失敗したら、どちらも元のまま
        /// ・書けなければ、描いた図をなくさないよう控え（stem.recovered-日時.json と .png）を残し、どこに残したかと、どのファイルで失敗したかを知らせる。
        ///   控えは添付フォルダに、そこへ書けなければ一時フォルダ（%TEMP%）に置く。控えの .png を本文にリンクして押せば、図エディタで開ける
        /// ・続けて「図エディタで開き直して、保存し直すか」を尋ねる（reopen）。開き直すと、書けなかった図から描き直せる
        /// </summary>
        /// <param name="isNew">新しい図。同じ名前が先にできていても上書きしない。</param>
        private bool WriteDrawing(string folder, string stem, DrawingEditor.DrawingResult result, bool isNew, out bool reopen)
        {
            reopen = false;

            var error = folder == null
                ? "添付フォルダが分かりませんでした。"
                : WriteDrawingFiles(Path.Combine(folder, stem + ".json"), Path.Combine(folder, stem + ".png"), result, isNew);
            if (error == null) return true;

            var recovered = SaveDrawingRecovery(folder, stem, result);

            var message = new StringBuilder();
            message.Append("図を保存できませんでした。").Append(Environment.NewLine).Append(error).Append(Environment.NewLine).Append(Environment.NewLine);
            if (recovered != null)
            {
                message.Append("描いた図は、控えに残しました:").Append(Environment.NewLine).Append(recovered).Append(Environment.NewLine);
                message.Append("（同じ名前の .png を本文にリンクして押すと、図エディタで開けます）").Append(Environment.NewLine).Append(Environment.NewLine);
            }
            else
            {
                message.Append("控えも残せませんでした。図エディタで開き直さないと、描いた図は失われます。").Append(Environment.NewLine).Append(Environment.NewLine);
            }
            message.Append("図エディタで開き直して、保存し直しますか？");

            var answer = MessageBox.Show(this, message.ToString(), "図エディタ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            reopen = answer == MessageBoxResult.Yes;

            if (!reopen && recovered != null) Status("図を保存できませんでした。控え: " + recovered);
            return false;
        }

        /// <summary>
        /// 図の .json と .png を、両方の一時ファイルを書いてから置き換える（2026-10-27）。書けたら null、書けなければ、どのファイルで何が起きたかの説明。
        /// </summary>
        private static string WriteDrawingFiles(string jsonPath, string pngPath, DrawingEditor.DrawingResult result, bool isNew)
        {
            var jsonTemp = jsonPath + ".saving";
            var pngTemp = pngPath + ".saving";
            var failed = jsonPath;

            try
            {
                WriteDrawingTemp(jsonTemp, new UTF8Encoding(false).GetBytes(result.Json));
                failed = pngPath;
                WriteDrawingTemp(pngTemp, result.Png);
            }
            catch (Exception ex)
            {
                TryDeleteDrawingTemp(jsonTemp);
                TryDeleteDrawingTemp(pngTemp);
                return Path.GetFileName(failed) + " を書けませんでした（.json・.png のどちらも変えていません）。" + Environment.NewLine + ex.Message;
            }

            // 置き換えは .json から。.png だけ失敗したときは、.json は新しい図なので、開き直して保存すれば .png も揃う。
            var jsonReplaced = false;
            failed = jsonPath;
            try
            {
                ReplaceDrawingFile(jsonTemp, jsonPath, isNew);
                jsonReplaced = true;
                failed = pngPath;
                ReplaceDrawingFile(pngTemp, pngPath, isNew);
                return null;
            }
            catch (Exception ex)
            {
                TryDeleteDrawingTemp(jsonTemp);
                TryDeleteDrawingTemp(pngTemp);
                return Path.GetFileName(failed) + " を置き換えられませんでした"
                    + (jsonReplaced ? "（" + Path.GetFileName(jsonPath) + " は新しい図になりましたが、.png は前のままです）。" : "（.json・.png のどちらも変えていません）。")
                    + Environment.NewLine + ex.Message;
            }
        }

        /// <summary>一時ファイルを書く。ディスクまで書き切る（PageDocument.WriteAtomically と同じ）。</summary>
        private static void WriteDrawingTemp(string path, byte[] data)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
        }

        /// <summary>一時ファイルで置き換える。新しい図（isNew）か、まだ無ければ名前を変えるだけ（先にできていれば投げる）。</summary>
        private static void ReplaceDrawingFile(string temp, string path, bool isNew)
        {
            if (isNew || !File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception)
            {
                // \\Mac\ 共有は置き換えに対応していないので、上書きで代える（PageDocument.WriteAtomically と同じ。SPEC 10章）。
                File.Copy(temp, path, true);
                TryDeleteDrawingTemp(temp);
            }
        }

        private static void TryDeleteDrawingTemp(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 残っても本物には触れない一時ファイル。次の保存で上書きされる。
            }
        }

        /// <summary>
        /// 書けなかった図の控え（stem.recovered-日時.json と .png）を残す。添付フォルダに書けなければ一時フォルダへ。
        /// 残した .json のパスを返す。どこにも書けなければ null。
        /// </summary>
        private static string SaveDrawingRecovery(string folder, string stem, DrawingEditor.DrawingResult result)
        {
            var name = stem + ".recovered-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var places = new List<string>();
            if (folder != null) places.Add(folder);
            places.Add(Path.GetTempPath());

            foreach (var place in places)
            {
                try
                {
                    var jsonPath = Path.Combine(place, name + ".json");
                    File.WriteAllBytes(jsonPath, new UTF8Encoding(false).GetBytes(result.Json));

                    try
                    {
                        File.WriteAllBytes(Path.Combine(place, name + ".png"), result.Png);
                    }
                    catch (Exception)
                    {
                        // .json があれば描き直せる
                    }

                    return jsonPath;
                }
                catch (Exception)
                {
                    // 次の場所へ
                }
            }

            return null;
        }

        // ---------------------------------------------------------------
        // 書き出し（SPEC 13章）
        //
        // 社外に渡す資料の下書きとして Word に出す。仕上げ（表紙・体裁）は Word で行い、
        // PDF も Word から作ってもらう。手元のファイルは一切書き換えない。
        // ---------------------------------------------------------------

        /// <summary>
        /// ツールバーの「エクスポート」。ボタンの下に、範囲・用紙・向き・余白・サブページの扱いを選ぶ小さな画面を出す（SPEC 13章。2026-10-14）。
        /// 「書き出す」で保存先を尋ねて書き出す。その文書を前回書き出したときの選び方から始める（.文書名/state.json に覚える）。
        /// 範囲の「現在のページ」はサブページを見ているときだけ選べるので、ルートを見ているときは前回が「現在のページ」でも「全体」から（2026-10-19）。
        /// </summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!HasDocument)
            {
                Status("先にファイルを開いてください。");
                return;
            }

            var view = Active;

            // 範囲。「現在のページ」は、サブページを見ているときだけ選べる。
            var scope = ExportChoice(new[] { "全体", "現在のページ" }, new object[] { PageDocument.RootId, view.PageId });
            ((ComboBoxItem)scope.Items[1]).IsEnabled = !string.IsNullOrEmpty(view.PageId);

            var paper = ExportChoice(new[] { "A4", "A3" }, new object[] { Paper.A4, Paper.A3 });
            var orientation = ExportChoice(new[] { "縦", "横" }, new object[] { false, true });
            var margin = ExportChoice(new[] { "10mm", "15mm", "20mm", "25mm", "30mm" }, new object[] { 10, 15, 20, 25, 30 });
            var subpages = ExportChoice(new[] { "本文に戻す", "除外する" }, new object[] { true, false });

            // その文書を前回エクスポートしたときの選び方から（.文書名/state.json。初めては 全体・A4・縦・20mm・本文に戻す）。
            var state = StateOf(view.FilePath) ?? new DocumentState();   // 無ければ初めの選び方（2026-10-27。前は落ちた）
            scope.SelectedIndex = state.ExportCurrentPage && !string.IsNullOrEmpty(view.PageId) ? 1 : 0;
            var marginIndex = new[] { 10, 15, 20, 25, 30 }.ToList().IndexOf(state.ExportMarginMm);
            paper.SelectedIndex = state.ExportPaper == "A3" ? 1 : 0;
            orientation.SelectedIndex = state.ExportLandscape ? 1 : 0;
            margin.SelectedIndex = marginIndex >= 0 ? marginIndex : 2;
            subpages.SelectedIndex = state.ExportExcludeSubpages ? 1 : 0;

            // 見た目はアプリに揃える（SPEC 13章・16章。2026-10-15）。サイドバーの地に角丸の枠と影、区分の見出しは小さめの太字、
            // 項目名は控えめの色、選ぶ欄は暗い配色のコンボ（App.xaml の DarkComboBox）。ボタンはツールバーと同じ形。
            // 書き出す中身（範囲・サブページ）と、ページの形（用紙・向き・余白）を線で分ける。
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            AddExportHeader(grid, "Word に書き出す");
            AddExportRow(grid, "範囲", scope);
            AddExportRow(grid, "サブページ", subpages);
            AddExportDivider(grid);
            AddExportRow(grid, "用紙", paper);
            AddExportRow(grid, "向き", orientation);
            AddExportRow(grid, "余白", margin);
            AddExportDivider(grid);

            var cancel = new Button();
            cancel.Content = "キャンセル";
            cancel.Style = (Style)FindResource("ToolButton");

            // 「書き出す」も「キャンセル」と同じ形（2026-10-18。前は青みのボタンで、青い字だった）。
            var run = new Button();
            run.Content = "書き出す";
            run.Style = (Style)FindResource("ToolButton");
            run.Margin = new Thickness(6, 0, 0, 0);

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Children.Add(cancel);
            buttons.Children.Add(run);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(buttons, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            var frame = new Border();
            frame.Background = (Brush)FindResource("SidebarBackground");
            frame.BorderBrush = (Brush)FindResource("FrameBorder");
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(8);
            frame.Padding = new Thickness(18, 14, 18, 14);
            frame.Margin = new Thickness(10, 4, 10, 14);   // 影の分の空き
            frame.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.45,
            };
            frame.Child = grid;

            // ポップアップは別の窓なので、ウィンドウの字の設定を引き継がない。揃えておく。
            System.Windows.Documents.TextElement.SetFontFamily(frame, FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(frame, 13);
            TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);

            var popup = new Popup();
            popup.PlacementTarget = (UIElement)sender;
            popup.Placement = PlacementMode.Bottom;
            popup.HorizontalOffset = -10;   // 影の分の空きを差し引いて、枠の左端をボタンの左端に揃える
            popup.AllowsTransparency = true;
            popup.StaysOpen = false;
            popup.Child = frame;

            Action export = delegate
            {
                var page = new PageSetup();
                page.Paper = (Paper)((ComboBoxItem)paper.SelectedItem).Tag;
                page.Landscape = (bool)((ComboBoxItem)orientation.SelectedItem).Tag;
                page.MarginMm = (int)((ComboBoxItem)margin.SelectedItem).Tag;
                var pageId = (string)((ComboBoxItem)scope.SelectedItem).Tag;
                var includeSubpages = (bool)((ComboBoxItem)subpages.SelectedItem).Tag;

                // この文書を次に書き出すときも、同じ選び方から始める。
                state.ExportCurrentPage = scope.SelectedIndex == 1;
                state.ExportPaper = page.Paper == Paper.A3 ? "A3" : "A4";
                state.ExportLandscape = page.Landscape;
                state.ExportMarginMm = page.MarginMm;
                state.ExportExcludeSubpages = !includeSubpages;
                SaveState(view.FilePath, state);

                popup.IsOpen = false;
                ExportFrom(view, pageId, page, includeSubpages);
            };

            run.Click += delegate { export(); };
            cancel.Click += delegate { popup.IsOpen = false; };

            // Enter で書き出す、Esc でやめる。コンボの一覧を開いているときは、コンボに任せる。
            frame.PreviewKeyDown += (s, args) =>
            {
                if (scope.IsDropDownOpen || subpages.IsDropDownOpen || paper.IsDropDownOpen
                    || orientation.IsDropDownOpen || margin.IsDropDownOpen) return;

                if (args.Key == Key.Escape) { args.Handled = true; popup.IsOpen = false; }
                else if (args.Key == Key.Enter) { args.Handled = true; export(); }
            };

            popup.Opened += delegate { scope.Focus(); };
            popup.IsOpen = true;
        }

        /// <summary>選ぶ欄。tags[i] が texts[i] の値。最初のものを選んでおく。</summary>
        private ComboBox ExportChoice(string[] texts, object[] tags)
        {
            var combo = new ComboBox();
            combo.Style = (Style)FindResource("DarkComboBox");
            for (var i = 0; i < texts.Length; i++)
                combo.Items.Add(new ComboBoxItem { Content = texts[i], Tag = tags[i] });

            combo.SelectedIndex = 0;
            return combo;
        }

        /// <summary>画面の見出し。区分の見出しと同じく、小さめの太字（SPEC 16章）。</summary>
        private void AddExportHeader(Grid grid, string title)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock();
            text.Text = title;
            text.FontSize = 12;
            text.FontWeight = FontWeights.SemiBold;
            text.Foreground = (Brush)FindResource("SidebarHeaderText");
            text.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(text, row);
            Grid.SetColumnSpan(text, 2);
            grid.Children.Add(text);
        }

        private void AddExportRow(Grid grid, string label, ComboBox combo)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock();
            text.Text = label;
            text.FontSize = 12;
            text.Foreground = (Brush)FindResource("TextMuted");
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 18, 8);
            Grid.SetRow(text, row);
            grid.Children.Add(text);

            combo.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(combo, row);
            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
        }

        /// <summary>区切りの線。パーツの区切り線と同じ色。</summary>
        private void AddExportDivider(Grid grid)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var line = new Border();
            line.Height = 1;
            line.Background = (Brush)FindResource("BorderBrushSoft");
            line.Margin = new Thickness(0, 4, 0, 12);
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, 2);
            grid.Children.Add(line);
        }

        /// <summary>そのページから書き出す。ルートならファイル全体。</summary>
        private void ExportFrom(MarkdownView view, string pageId, PageSetup page, bool includeSubpages)
        {
            var document = view.Owner;
            var isSubpage = !string.IsNullOrEmpty(pageId);

            var dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Filter = "Word 文書 (*.docx)|*.docx";
            dialog.DefaultExt = ".docx";
            dialog.FileName = Path.GetFileNameWithoutExtension(document.FilePath)
                              + (isSubpage ? " - " + SafeFileName(view.Title) : string.Empty) + ".docx";
            dialog.InitialDirectory = Path.GetDirectoryName(document.FilePath);

            if (dialog.ShowDialog(this) != true) return;

            // ファイル名欄に memo.md と打つと拡張子が足されず、元の Markdown を docx で塗り潰せてしまう。
            // 開いているのは Markdown だけなので、.docx に限れば開いているファイルには当たらない。
            var target = dialog.FileName;

            if (!string.Equals(Path.GetExtension(target), ".docx", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "書き出し先には .docx のファイルを指定してください。" + Environment.NewLine +
                    "開いている Markdown ファイルには書き出せません。",
                    "書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ExportReport report;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                var exporter = new DocxExporter(OpenForExport, VisualTreeHelper.GetDpi(this).DpiScaleX, page);
                report = exporter.Export(document, target, pageId, includeSubpages);
            }
            catch (Exception ex)
            {
                var hint = ex is IOException
                    ? Environment.NewLine + "書き出し先のファイルを Word で開いたままになっていませんか？"
                    : string.Empty;

                MessageBox.Show(this, "書き出せませんでした。" + Environment.NewLine + ex.Message + hint,
                    "書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            Status("書き出しました: " + report.DocxPath);

            var answer = MessageBox.Show(this, DescribeExport(report) + Environment.NewLine + "Word で開きますか？",
                "書き出し", MessageBoxButton.YesNo,
                report.Problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes) Launch(report.DocxPath);
        }

        /// <summary>ファイル名に使えない文字を外す。空になったら「サブページ」。</summary>
        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string((name ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (safe.Length > 60) safe = safe.Substring(0, 60).Trim();
            return safe.Length > 0 ? safe : "サブページ";
        }

        /// <summary>編集中の文書は保存前の内容で書き出す。開いていなければディスクから読む。</summary>
        private PageDocument OpenForExport(string path)
        {
            return FindOpenDocument(ShellActions.CanonicalPath(path)) ?? PageDocument.Load(path);
        }

        private static string DescribeExport(ExportReport report)
        {
            const int MaxProblems = 12;

            var text = new StringBuilder();
            text.Append("書き出しました。").Append(Environment.NewLine);
            text.Append(report.DocxPath).Append(Environment.NewLine);

            if (report.AttachmentFolder != null)
            {
                text.Append(Environment.NewLine);
                text.Append("添付 ").Append(report.AttachmentCount).Append(" 件をコピーしました:").Append(Environment.NewLine);
                text.Append(report.AttachmentFolder).Append(Environment.NewLine);
            }

            if (report.OrphanCount > 0)
            {
                text.Append(Environment.NewLine);
                text.Append("どこからもリンクされていないページ ").Append(report.OrphanCount)
                    .Append(" 件は含めていません。").Append(Environment.NewLine);
            }

            if (report.Problems.Count > 0)
            {
                text.Append(Environment.NewLine).Append("確認してください:").Append(Environment.NewLine);

                for (var i = 0; i < report.Problems.Count && i < MaxProblems; i++)
                {
                    text.Append("・").Append(report.Problems[i]).Append(Environment.NewLine);
                }

                if (report.Problems.Count > MaxProblems)
                    text.Append("ほか ").Append(report.Problems.Count - MaxProblems).Append(" 件").Append(Environment.NewLine);
            }

            return text.ToString();
        }
    }
}
