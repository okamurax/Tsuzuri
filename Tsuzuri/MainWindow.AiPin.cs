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
    // MainWindow の一部: AI の会話ごとの「プロジェクトフォルダ」と「固定したカーソル」（2026-11-22。SPEC 19章。利用者の考え）。
    //
    // AI の欄の下端（渡した状態の行の下）に2段置き、選んでいる会話のタブの分を出す。どちらもタブごとに持ち、次の起動でも戻す（AiTabState）。
    // ・プロジェクトフォルダ: この会話で扱うプロジェクトのフォルダ（2026-11-24 に「フォルダ」から改名。開いている文書のフォルダ・会話を始めたフォルダと取り違えないため）。開いている文書（cmd.md など）とは別の所でソフトを作るときに入れる。
    //   話しかけるたびに今の状態の頭に1行で書く（入っていなくても「指定なし」と毎回。1行だけなので軽く、会話が長くなって前の話を縮めても忘れない）
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

            // プロジェクトフォルダ
            label("プロジェクトフォルダ", 0).ToolTip = "この会話で扱うプロジェクトのフォルダ。入れておくと、話しかけるたびに Claude に伝える（開いている文書のフォルダとは別だと伝える）";
            _aiProjectBox = new TextBox { FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2), AllowDrop = true,
                                          ToolTip = "空なら「指定なし」と伝える。ランチャーのファイラーの「このフォルダをプロジェクトフォルダにする」で入れる（打つ・エクスプローラーから落とすのでも入る）" };
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

        /// <summary>プロジェクトフォルダの欄の見た目（見つからない・AI に渡さない場所なら、ツールチップと色で知らせる）。</summary>
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
            _aiProjectBox.ToolTip = problem ?? "空なら「指定なし」と伝える。ランチャーのファイラーの「このフォルダをプロジェクトフォルダにする」で入れる（打つ・エクスプローラーから落とすのでも入る）";
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
                PinMatch match;
                var line = ResolvePinLine(document.TextOf(pin.Page), pin, out match);
                var view = window.FindView(document, pin.Page);
                if (view == null || !window._visible.Contains(view)) view = window.ShowPage(document, pin.Page);
                if (view == null) return;
                window.Dispatcher.BeginInvoke(new Action(delegate { view.GoToLine(line); }), DispatcherPriority.Loaded);
            });
        }

        private enum PinMatch { Found, Short, Missing }

        /// <summary>
        /// 固定した行の、今の行番号（ページの中。1 始まり）。固定したときの行に同じ文字があればそこ、無ければ近い順に同じ文字の行を探す（ページ全体）。
        /// 空行や4文字に満たない行は探し直さない（ほかにもいくらでもあり、近くの別の行を当ててしまう）。
        /// 見つからなければ固定したときの行番号（ページの行の数を越えれば最後の行）。
        /// </summary>
        private static int ResolvePinLine(string pageText, AiPinState pin, out PinMatch match)
        {
            var lines = (pageText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var at = Math.Max(1, pin.Line);
            var text = pin.Text ?? string.Empty;
            var fallback = Math.Min(at, lines.Length);

            if (text.Trim().Length < 4)
            {
                match = PinMatch.Short;
                return fallback;
            }

            match = PinMatch.Found;
            if (at <= lines.Length && lines[at - 1] == text) return at;
            var reach = Math.Max(at, lines.Length);   // at がページの行の数を越えていても、上へ1行目まで探す
            for (var distance = 1; distance <= reach; distance++)
            {
                var up = at - distance;
                if (up >= 1 && up <= lines.Length && lines[up - 1] == text) return up;
                var down = at + distance;
                if (down <= lines.Length && lines[down - 1] == text) return down;
            }
            match = PinMatch.Missing;
            return fallback;
        }

        // =====================================================================
        // 今の状態に書く
        // =====================================================================

        /// <summary>この会話のプロジェクトフォルダの行（入っていなくても毎回）。</summary>
        private void AppendAiProject(StringBuilder text, AiTab tab)
        {
            if (tab == null) return;
            if (string.IsNullOrEmpty(tab.Project))
            {
                // 無いときも書く（2026-11-22。消したあと、前のフォルダで作業を続けないように）
                text.Append("プロジェクトフォルダ: 指定なし（前の頼みで指定があっても、今は外されている）。\n");
                return;
            }

            if (AiGuard.IsGuarded(tab.Project))
            {
                text.Append("プロジェクトフォルダ: 利用者が指定したが、AI に渡さない場所なので伝えない。その場所を推し量ったり探したりしない。")
                    .Append("「このフォルダ」「プロジェクト」を頼まれたら、尋ねず、渡されていないので扱えないと利用者に伝える。\n");
                return;
            }
            text.Append("プロジェクトフォルダ（利用者がこの会話のために指定した）: ").Append(tab.Project);
            if (!DirectoryExists(tab.Project)) text.Append("（今は見つからない）");
            text.Append("。文書のフォルダ（下の「ファイル」の行の文書があるフォルダ。あれば）・会話を始めたフォルダとは別。ソースを読む・書く・コマンドを動かすのは、断りが無ければこのフォルダで。")
                .Append("話題の文書（メモ・手順など）を読み書きするのは、今までどおり「ファイル」の行の文書。\n");
        }

        /// <summary>
        /// カーソルを固定しているときの、話題の文書・ページ・カーソル。呼び名は固定していないときと同じ（ファイル・見ているページ・カーソル・まず読む範囲）。
        /// 頭に、固定していること・エディタの画面の位置とは違うことを書く。書いた話題の文書を返す（ファイルが無い・読めない・AI に渡さない場所なら null）。
        /// </summary>
        private PageDocument AppendAiPinnedSubject(StringBuilder text, AiPinState pin)
        {
            if (AiGuard.IsGuarded(pin.File))
            {
                text.Append("この会話は、利用者がカーソルの位置を固定しているが、その位置は AI に渡さない場所の文書にあるので、場所も中身も渡していない。")
                    .Append("この会話の話題の位置は分からない。「ここ」「このページ」などで頼まれたら、どこのことかを利用者に尋ねる（下の「エディタの画面（参考）」を話題の位置と思わない）。\n");
                return null;
            }

            text.Append("この会話は、利用者がカーソルの位置を固定している（同じ文書の別々のページの話を、別々の会話で同時に進めるため）。")
                .Append("下の「ファイル」「見ているページ」「カーソル」「まず読む範囲」は固定した位置で、エディタの画面で今カーソルがある所ではない。")
                .Append("「ここ」「この節」「このページ」「カーソル」「この文書」は、断りが無ければこの固定した位置（とその文書）を指す。")
                .Append("エディタの画面の位置は、そのあとの「エディタの画面（参考）」に書いた。\n");

            // 開いている文書があればそれを使う。ただし未保存の編集が無く、ファイルが外で書き換わっていれば（ほかの画面の文書は、その画面に移るまで読み直さない）、
            // ファイルから読む（2026-11-22。古い中身の行番号を渡さないように）
            PageDocument document = null;
            foreach (var window in OpenWindows)
            {
                document = window.FindOpenDocument(pin.File);
                if (document != null) break;
            }
            string unreadable = null;
            var changed = false;
            try
            {
                changed = document != null && !document.IsDirty && document.IsChangedOnDisk;
            }
            catch (Exception)
            {
            }
            if (document == null || changed)
            {
                try
                {
                    if (File.Exists(pin.File)) document = PageDocument.Load(pin.File);
                    else if (document == null) unreadable = "ファイルが無い";
                }
                catch (Exception ex)
                {
                    unreadable = ex.Message;
                    if (changed)
                    {
                        // 読めなければ、開いている（古い）中身で書く。古いことは AppendUnsaved が断る（StaleReason）
                        unreadable = null;
                    }
                    else
                    {
                        document = null;
                    }
                }
            }

            var pageLabel = (string.IsNullOrEmpty(pin.Page) ? "ルート" : pin.Page) + "「" + (pin.Title ?? string.Empty) + "」";
            if (document == null)
            {
                // 「ファイル」の呼び名は使わない（指示は「ファイル」の行があれば履歴・添付の行で決めるため。読めないのに「記録が無い」と取られないように）
                text.Append("固定した文書（今は読めない）: ").Append(pin.File).Append('\n')
                    .Append("読めないわけ: ").Append(unreadable ?? "分からない").Append('\n')
                    .Append("固定したときのページは ").Append(pageLabel).Append("、ページの ").Append(pin.Line)
                    .Append(" 行目。この文書の添付フォルダ・変更の履歴は分からない。読む・書く前に、利用者に確かめる。\n");
                return null;
            }

            AppendDocumentFacts(text, document);
            if (!document.Contains(pin.Page))
            {
                text.Append("見ているページ: ").Append(pageLabel).Append("（固定したページは、今はこのファイルに無い。消えたか、ID が変わった。読む・書く前に、どこのことかを利用者に確かめる）\n");
                return document;
            }

            PinMatch match;
            var line = ResolvePinLine(document.TextOf(pin.Page), pin, out match);
            int first, last;
            bool toEnd;
            var ranged = document.LineRangeOf(pin.Page, out first, out last, out toEnd);
            var tail = match == PinMatch.Found
                ? ": " + pin.Text
                : match == PinMatch.Short
                    ? (string.IsNullOrWhiteSpace(pin.Text) ? "（固定した行は空行" : "（固定した行は「" + pin.Text + "」という短い行")
                      + "で、同じ形の行がほかにもありうるので、探し直していない。固定したときの行番号のままの、およその位置）"
                    : "（固定したときの行「" + pin.Text + "」が、ページの中に見つからない。書き換わったらしい。固定したときの行番号のままの、およその位置）";
            AppendPageAndCursor(text, ranged, first, last, toEnd, pageLabel, line, tail);
            text.Append("（「見ているページ」の題は固定したときのもので、今は変わっていることがある。ページは ID で探す）\n");
            return document;
        }
    }
}
