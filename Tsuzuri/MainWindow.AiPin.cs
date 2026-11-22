using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: AI の会話ごとの「フォルダ」と「固定したカーソル」（2026-11-22。SPEC 19章。利用者の考え）。
    //
    // AI の欄の下端（渡した状態の行の下）に2段置き、選んでいる会話のタブの分を出す。どちらもタブごとに持ち、次の起動でも戻す（AiTabState）。
    // ・フォルダ: この会話で扱うプロジェクトのフォルダ。開いている文書（cmd.md など）とは別の所でソフトを作るときに入れる。
    //   入れてあれば、話しかけるたびに今の状態の頭に1行で書く（毎回。1行だけなので軽く、会話が長くなって前の話を縮めても忘れない）
    // ・カーソル: 「今の位置で固定」で、エディタの今のカーソル（ファイル・ページ・行）をその会話に固定する。固定していれば、その会話には
    //   エディタの今のカーソルの代わりに固定した位置を「この会話の話題」として渡し、エディタの今の位置は参考として添える（固定していることは明かす）。
    //   1つの文書のページ A と B の話を、2つの会話で同時に進めるため。行がずれたら、固定したときの行の文字で近くを探し直す
    // どの会話からの頼みかは、HTTP のフックなら session_id（会話が始まったときに覚えた番号）、パイプのフックならタブの番号（MDEDITOR_AI_TAB）で見分ける。
    public partial class MainWindow
    {
        private Grid _aiPinRows;
        private TextBox _aiProjectBox;
        private TextBlock _aiPinText;
        private Button _aiPinButton;

        /// <summary>タブを切り替えて欄に入れ直している最中（そのときの TextChanged はタブに書かない）。</summary>
        private bool _aiPinRefreshing;

        /// <summary>頼んできた会話のタブ。番号で探し、無ければ会話の番号で探す。分からなければ null（フォルダ・固定したカーソルは書かない）。</summary>
        private AiTab FindAiTab(string tabId, string session)
        {
            if (!string.IsNullOrEmpty(tabId))
            {
                var byId = _aiTabs.FirstOrDefault(tab => tab.Id == tabId);
                if (byId != null) return byId;
            }
            if (!string.IsNullOrEmpty(session))
                return _aiTabs.FirstOrDefault(tab => string.Equals(tab.SessionId, session, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        // =====================================================================
        // 下端の2段
        // =====================================================================

        /// <summary>下端の2段を作る（初めて要るとき）。渡した状態の行（AiContextText）のすぐ下に入れる。</summary>
        private void EnsureAiPinRows()
        {
            if (_aiPinRows != null) return;
            var parent = AiContextText.Parent as Panel;
            if (parent == null) return;

            var muted = TryFindResource("TextMuted") as Brush ?? Brushes.Gray;
            var buttonStyle = TryFindResource("ToolButton") as Style;

            _aiPinRows = new Grid { Margin = new Thickness(12, 0, 8, 4) };
            _aiPinRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _aiPinRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _aiPinRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _aiPinRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _aiPinRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Func<string, int, TextBlock> label = (text, row) =>
            {
                var block = new TextBlock { Text = text, FontSize = 12, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetRow(block, row);
                _aiPinRows.Children.Add(block);
                return block;
            };
            Func<string, string, int, Button> button = (text, tip, row) =>
            {
                var created = new Button { Content = text, ToolTip = tip, FontSize = 12, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 2, 0, 2), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
                if (buttonStyle != null) created.Style = buttonStyle;
                Grid.SetRow(created, row);
                Grid.SetColumn(created, 2);
                _aiPinRows.Children.Add(created);
                return created;
            };

            // フォルダ
            label("フォルダ", 0).ToolTip = "この会話で扱うプロジェクトのフォルダ。入れておくと、話しかけるたびに Claude に伝える（開いている文書の場所とは別だと伝える）";
            _aiProjectBox = new TextBox { FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2), AllowDrop = true,
                                          ToolTip = "空なら伝えない。エクスプローラーからフォルダを落としても入る" };
            // 暗い地に合わせる（白い既定の欄にしない。置換の欄と同じく、地は透かして下の線だけ）
            _aiProjectBox.Background = Brushes.Transparent;
            _aiProjectBox.BorderBrush = TryFindResource("BorderBrushSoft") as Brush ?? Brushes.Gray;
            _aiProjectBox.BorderThickness = new Thickness(0, 0, 0, 1);
            _aiProjectBox.CaretBrush = TryFindResource("TextPrimary") as Brush ?? Brushes.White;
            Grid.SetRow(_aiProjectBox, 0);
            Grid.SetColumn(_aiProjectBox, 1);
            _aiPinRows.Children.Add(_aiProjectBox);
            _aiProjectBox.TextChanged += delegate
            {
                if (_aiPinRefreshing || _aiTab == null) return;
                var text = _aiProjectBox.Text.Trim().Trim('"');
                _aiTab.Project = text.Length == 0 ? null : text;
                ShowAiProjectState();
            };
            _aiProjectBox.LostKeyboardFocus += delegate { StoreAiTabs(); };
            _aiProjectBox.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                StoreAiTabs();
                if (_aiTab != null) FocusAiTerminal(_aiTab);
            };
            _aiProjectBox.PreviewDragOver += (sender, e) =>
            {
                e.Effects = DroppedFolder(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            _aiProjectBox.PreviewDrop += (sender, e) =>
            {
                var folder = DroppedFolder(e.Data);
                if (folder == null) return;
                e.Handled = true;
                _aiProjectBox.Text = folder;
                StoreAiTabs();
            };
            button("…", "フォルダを選ぶ", 0).Click += delegate { PickAiProject(); };

            // カーソル
            label("カーソル", 1).ToolTip = "この会話に固定したカーソルの位置。固定していれば、エディタの今のカーソルの代わりに、ここを話題として Claude に渡す";
            _aiPinText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand };
            Grid.SetRow(_aiPinText, 1);
            Grid.SetColumn(_aiPinText, 1);
            _aiPinRows.Children.Add(_aiPinText);
            _aiPinText.MouseLeftButtonUp += delegate { GoToAiPin(); };
            _aiPinButton = button("今の位置で固定", null, 1);
            _aiPinButton.Click += delegate { ToggleAiPin(); };

            parent.Children.Insert(parent.Children.IndexOf(AiContextText) + 1, _aiPinRows);
        }

        /// <summary>選んでいるタブの分を2段に出す（タブを選んだとき・固定を変えたとき）。</summary>
        private void RefreshAiPinRows()
        {
            EnsureAiPinRows();
            if (_aiPinRows == null) return;

            var tab = _aiTab;
            _aiPinRows.IsEnabled = tab != null;
            _aiPinRefreshing = true;
            try
            {
                var project = tab != null ? tab.Project ?? string.Empty : string.Empty;
                if (_aiProjectBox.Text != project) _aiProjectBox.Text = project;
            }
            finally
            {
                _aiPinRefreshing = false;
            }
            ShowAiProjectState();

            var muted = TryFindResource("TextMuted") as Brush ?? Brushes.Gray;
            var pin = tab != null ? tab.Pin : null;
            if (pin == null)
            {
                _aiPinText.Text = "固定なし（エディタの今のカーソルを渡す）";
                _aiPinText.Foreground = muted;
                _aiPinText.ToolTip = null;
                _aiPinText.Cursor = null;
                _aiPinButton.Content = "今の位置で固定";
                _aiPinButton.ToolTip = "エディタの今のカーソル（ファイル・ページ・行）を、この会話の話題として固定する";
            }
            else
            {
                _aiPinText.Text = PinLabel(pin);
                _aiPinText.Foreground = TryFindResource("TextPrimary") as Brush ?? SystemColors.ControlTextBrush;
                _aiPinText.ToolTip = pin.File + "\n" + pin.Line + " 行目（固定したとき）: " + (pin.Text ?? string.Empty) + "\n押すとその位置へ移る";
                _aiPinText.Cursor = Cursors.Hand;
                _aiPinButton.Content = "解除";
                _aiPinButton.ToolTip = "固定をやめ、エディタの今のカーソルを渡すのに戻す";
            }
        }

        /// <summary>フォルダの欄の見た目（見つからない・AI に渡さない場所なら、ツールチップと色で知らせる）。</summary>
        private void ShowAiProjectState()
        {
            if (_aiProjectBox == null) return;
            var path = _aiTab != null ? _aiTab.Project : null;
            string problem = null;
            if (!string.IsNullOrEmpty(path))
            {
                if (AiGuard.IsGuarded(path)) problem = "AI に渡さない場所（" + AiGuard.MarkerName + " のあるフォルダ）なので、Claude には伝えない";
                else if (!DirectoryExists(path)) problem = "このフォルダは見つからない（Claude には「見つからない」と添えて伝える）";
            }
            _aiProjectBox.Foreground = problem != null ? Brushes.IndianRed : TryFindResource("TextPrimary") as Brush ?? SystemColors.ControlTextBrush;
            _aiProjectBox.ToolTip = problem ?? "空なら伝えない。エクスプローラーからフォルダを落としても入る";
        }

        private static bool DirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string DroppedFolder(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length != 1) return null;
            return DirectoryExists(paths[0]) ? paths[0] : null;
        }

        private void PickAiProject()
        {
            if (_aiTab == null) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "この会話で扱うプロジェクトのフォルダ", ShowNewFolderButton = false })
            {
                if (!string.IsNullOrEmpty(_aiTab.Project) && DirectoryExists(_aiTab.Project)) dialog.SelectedPath = _aiTab.Project;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedPath)) return;
                _aiProjectBox.Text = dialog.SelectedPath;
                StoreAiTabs();
            }
        }

        private static string PinLabel(AiPinState pin)
        {
            return Path.GetFileName(pin.File) + " › " + (string.IsNullOrEmpty(pin.Page) ? "ルート" : pin.Page)
                   + "「" + (pin.Title ?? string.Empty) + "」 " + pin.Line + " 行目";
        }

        /// <summary>「今の位置で固定」・「解除」。</summary>
        private void ToggleAiPin()
        {
            var tab = _aiTab;
            if (tab == null) return;

            if (tab.Pin != null)
            {
                tab.Pin = null;
                Status("カーソルの固定をやめました（" + tab.Name + "）。エディタの今のカーソルを渡します");
            }
            else
            {
                var view = Active;
                if (view == null || view.Owner == null || string.IsNullOrEmpty(view.FilePath))
                {
                    Status("固定するカーソルがありません（文書を開いていません）");
                    return;
                }
                if (AiGuard.IsGuarded(view.FilePath))
                {
                    Status("AI に渡さない文書のカーソルは固定できません");
                    return;
                }
                var line = view.TextArea.Caret.Line;
                tab.Pin = new AiPinState
                {
                    File = view.FilePath,
                    Page = view.PageId ?? string.Empty,
                    Title = view.Title,
                    Line = line,
                    Text = view.Document.GetText(view.Document.GetLineByNumber(line)),
                };
                Status("カーソルを " + tab.Name + " に固定しました: " + PinLabel(tab.Pin));
            }
            StoreAiTabs();
            RefreshAiPinRows();
        }

        /// <summary>固定した位置へ移る（2段目の文字を押したとき）。</summary>
        private void GoToAiPin()
        {
            var pin = _aiTab != null ? _aiTab.Pin : null;
            if (pin == null) return;
            if (!File.Exists(pin.File))
            {
                Status("固定した文書が見つかりません: " + pin.File);
                return;
            }

            OpenFileThen(pin.File, (window, document) =>
            {
                if (!document.Contains(pin.Page))
                {
                    window.Status("固定したページが文書にありません: " + PinLabel(pin));
                    return;
                }
                var line = ResolvePinLine(document.TextOf(pin.Page), pin, out _);
                var view = window.FindView(document, pin.Page);
                if (view == null || !window._visible.Contains(view)) view = window.ShowPage(document, pin.Page);
                if (view == null) return;
                window.Dispatcher.BeginInvoke(new Action(delegate { view.GoToLine(line); }), DispatcherPriority.Loaded);
            });
        }

        /// <summary>
        /// 固定した行の、今の行番号（ページの中。1 始まり）。固定したときの行に同じ文字があればそこ、無ければ近い順に同じ文字の行を探す。
        /// 見つからなければ固定したときの行番号（ページの行の数を越えれば最後の行）で、found は false。
        /// </summary>
        private static int ResolvePinLine(string pageText, AiPinState pin, out bool found)
        {
            var lines = (pageText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var at = Math.Max(1, pin.Line);
            var text = pin.Text ?? string.Empty;
            found = true;
            if (at <= lines.Length && lines[at - 1] == text) return at;
            for (var distance = 1; distance < lines.Length; distance++)
            {
                if (at - distance >= 1 && at - distance <= lines.Length && lines[at - distance - 1] == text) return at - distance;
                if (at + distance <= lines.Length && lines[at + distance - 1] == text) return at + distance;
            }
            found = false;
            return Math.Min(at, lines.Length);
        }

        // =====================================================================
        // 今の状態に書く
        // =====================================================================

        /// <summary>
        /// 話しかけてきた会話のフォルダと固定したカーソルを、今の状態に書く。カーソルを固定していて、その位置を書けたら true
        /// （そのときは、このあとに書くエディタの位置を「参考」とし、まず読む範囲は固定した位置の分だけにする）。
        /// </summary>
        private bool AppendAiTabContext(StringBuilder text, AiTab tab)
        {
            if (tab == null) return false;

            if (!string.IsNullOrEmpty(tab.Project))
            {
                if (AiGuard.IsGuarded(tab.Project))
                {
                    text.Append("この会話のプロジェクトのフォルダ: 利用者が指定したが、AI に渡さない場所なので伝えない。尋ねたり探したりしない。\n");
                }
                else
                {
                    text.Append("この会話のプロジェクトのフォルダ（利用者が指定）: ").Append(tab.Project);
                    if (!DirectoryExists(tab.Project)) text.Append("（今は見つからない）");
                    text.Append("。開いている文書の場所とは別。ソースを読む・書く・コマンドを動かすのは、断りが無ければこのフォルダで。\n");
                }
            }

            var pin = tab.Pin;
            if (pin == null) return false;
            if (AiGuard.IsGuarded(pin.File))
            {
                text.Append("この会話で固定した位置は、AI に渡さない場所の文書にある。場所も中身も渡していない。\n");
                return false;
            }

            text.Append("この会話の話題の位置（利用者がこの会話のために固定した。エディタの今のカーソルではない。「ここ」「このページ」「カーソル」は、断りが無ければこちらを指す）:\n");
            text.Append("ファイル: ").Append(pin.File).Append('\n');

            PageDocument document = null;
            foreach (var window in OpenWindows)
            {
                document = window.FindOpenDocument(pin.File);
                if (document != null) break;
            }
            if (document == null)
            {
                try
                {
                    if (File.Exists(pin.File)) document = PageDocument.Load(pin.File);
                }
                catch (Exception)
                {
                    document = null;
                }
            }
            if (document == null)
            {
                text.Append("（このファイルは今は見つからない・読めない。固定したときは ").Append(PinLabel(pin)).Append("）\n");
                return true;
            }
            if (!document.Contains(pin.Page))
            {
                text.Append("（固定したページ ").Append(string.IsNullOrEmpty(pin.Page) ? "ルート" : pin.Page).Append("「").Append(pin.Title).Append("」は、今はこのファイルに無い）\n");
                return true;
            }

            bool found;
            var line = ResolvePinLine(document.TextOf(pin.Page), pin, out found);
            int first, last;
            bool toEnd;
            var ranged = document.LineRangeOf(pin.Page, out first, out last, out toEnd);

            text.Append("ページ: ").Append(string.IsNullOrEmpty(pin.Page) ? "ルート" : pin.Page).Append("「").Append(pin.Title).Append("」");
            if (ranged)
            {
                text.Append("（中身はファイルの ").Append(first).Append("〜").Append(last).Append(" 行目");
                text.Append(toEnd ? "。ファイルの末尾まで" : "。" + (last + 1) + " 行目からは別のページ").Append('）');
            }
            text.Append('\n');

            text.Append("カーソル: ページの ").Append(line).Append(" 行目");
            if (ranged) text.Append("（ファイルの ").Append(first - 1 + line).Append(" 行目）");
            text.Append(found ? ": " + pin.Text : "（固定したときの行「" + pin.Text + "」が見つからない。書き換わったかもしれないので、およその位置）");
            text.Append('\n');

            if (ranged)
            {
                var at = first - 1 + line;
                int from = first, to = last;
                if (last - first + 1 > ReadAroundLines * 2)
                {
                    from = Math.Max(first, at - ReadAroundLines);
                    to = Math.Min(last, at + ReadAroundLines);
                }
                text.Append("まず読む範囲: ファイルの ").Append(from).Append("〜").Append(to).Append(" 行目（Read の offset ").Append(from)
                    .Append("・limit ").Append(to - from + 1).Append("）。先頭から読まない。足りなければ広げる\n");
            }
            return true;
        }
    }
}
