using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: フロー（2026-11-17。SPEC 21章）。AI の欄のターミナルへ流し込むテキストを、カードとして並べてつなぎ、1枚ずつ送る。
    // フローは今の文書のサブページ（図エディタの図と同じく、別のファイルを作らない。最初の行が印 FlowFile.Marker）。
    // 本文にはそのページへのリンクを置き、押すとフロー図（Views/FlowView）を本文の場所の上に重ねて出す（ページとしては開かない）。
    // ランチャーと同じ重ね方だが、ほぼフロー図で直すので、高さを多めに取る（既定 75%。仕切りで変えられ、覚える）。下の本文は見えたまま。
    // 送り先: Claude のカードはフローの会話（下）、PowerShell のカードは共通ターミナル。送るのは利用者が1枚ずつ（自動では進めない）。
    public partial class MainWindow
    {
        private FlowView _flowView;
        private PageDocument _flowDocument;
        private string _flowPage;

        /// <summary>
        /// フローの会話（2026-11-17）。最初のカードで新しい会話を始め、続くカードはその会話へ送る。「新しい会話」の印のカードで、また新しい会話を始める
        /// （そこから続くカードはその会話へ）。見出しは「文書#ページ/会話を始めるカードの題」。この画面の会話のタブを指す（閉じた・終わったら作り直す）。
        /// </summary>
        private readonly Dictionary<string, AiTab> _flowSessions = new Dictionary<string, AiTab>(StringComparer.OrdinalIgnoreCase);

        /// <summary>フロー図の高さの割合の既定（仕切りのダブルクリックで戻す）。</summary>
        private const double DefaultFlowSplit = 0.75;

        /// <summary>新しい会話が動き出すのを待つ上限（claude の起動。初めてのフォルダでは信頼の確認を待つことがある）。</summary>
        private static readonly TimeSpan FlowSessionWait = TimeSpan.FromSeconds(90);

        /// <summary>ツールバーの「フローを追加」。今の文書にフローのサブページを作り、カーソルの位置にリンクを入れて開く。</summary>
        private void InsertFlow_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (!CanInsertInto(view) || view.Owner == null) return;

            var flow = new FlowFile { Preamble = FlowFile.Marker };
            flow.Cards.Add(new FlowCard { Kind = FlowKind.Claude, Title = "1", X = 40, Y = 40, HasPosition = true });

            var document = view.Owner;
            var id = document.AddPage(flow.Serialize());
            var page = document.PageOf(id);
            var at = document.IndexOf(id);

            // ページの追加とリンクを、元に戻す1回の操作にする（サブページ作成と同じ。MainWindow.Pages.cs）
            var replaced = view.ReplaceRange(view.CaretOffset, 0, "[フロー](#" + id + ")",
                () => document.RestorePage(page, at),
                () => document.RemovePage(id));
            if (!replaced)
            {
                document.RemovePage(id);
                Status("リンクを入れられなかったので、フローを作るのをやめました。");
                return;
            }

            UpdateChrome();
            Status("フローを作りました（保存で文書に書きます）");
            OpenFlow(document, id);
        }

        /// <summary>本文のリンクの先がフローのページなら、フロー図で開く。フローでなければ false。</summary>
        private bool TryOpenFlow(PageDocument document, string id)
        {
            if (document == null || !document.Contains(id) || !FlowFile.IsFlow(document.TextOf(id))) return false;
            OpenFlow(document, id);
            return true;
        }

        private void OpenFlow(PageDocument document, string id)
        {
            if (_launcher != null) CloseLauncher();

            if (_flowView != null && ReferenceEquals(_flowDocument, document) && _flowPage == id)
            {
                _flowView.ReloadIfChanged();
                _flowView.Focus();
                return;
            }
            CloseFlow();

            _flowDocument = document;
            _flowPage = id;
            var name = "フロー — " + Path.GetFileName(document.FilePath);
            _flowView = new FlowView(
                document.FilePath + "#" + id,
                name,
                () => document.TextOf(id),
                text =>
                {
                    if (!document.SetText(id, text))
                    {
                        Status("フローのページが文書にありません（消えた・読み直した）。閉じます");
                        Dispatcher.BeginInvoke(new Action(CloseFlow));
                        return;
                    }
                    UpdateChrome();   // 未保存の印
                });
            _flowView.CloseRequested += CloseFlow;
            _flowView.SaveRequested += delegate { Save_Click(this, new RoutedEventArgs()); };
            _flowView.ImportFiles = paths => ImportFlowFiles(document, paths);
            var flowView = _flowView;
            _flowView.SendRequested += (card, enter) => SendFlowCard(flowView, card, enter);

            FlowLayer.Children.Clear();
            FlowLayer.RowDefinitions.Clear();
            FlowLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_settings.FlowSplit, GridUnitType.Star), MinHeight = 160 });
            FlowLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            FlowLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - _settings.FlowSplit, GridUnitType.Star), MinHeight = 60 });

            var frame = new Border
            {
                Child = _flowView,
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            FlowLayer.Children.Add(frame);

            // 下の本文との仕切り。下の段は何も置かない（押すと下の本文に届く）
            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                Style = (Style)FindResource("RowSplitter"),
                Background = (System.Windows.Media.Brush)FindResource("PageBackground"),
                ToolTip = "ドラッグで高さを変える（ダブルクリックで標準の高さ）",
            };
            Grid.SetRow(splitter, 1);
            splitter.DragCompleted += (sender, e) =>
            {
                if (e.VerticalChange == 0) return;
                var top = FlowLayer.RowDefinitions[0].ActualHeight;
                var total = top + FlowLayer.RowDefinitions[2].ActualHeight;
                if (total < 1) return;
                _settings.FlowSplit = Math.Max(0.2, Math.Min(0.95, top / total));
                _settings.Save();
                ApplyFlowSplit();
            };
            splitter.MouseDoubleClick += (sender, e) =>
            {
                e.Handled = true;
                _settings.FlowSplit = DefaultFlowSplit;
                _settings.Save();
                ApplyFlowSplit();
            };
            FlowLayer.Children.Add(splitter);

            FlowLayer.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(delegate { if (_flowView != null) _flowView.Focus(); }), DispatcherPriority.Loaded);
        }

        private void ApplyFlowSplit()
        {
            if (FlowLayer.RowDefinitions.Count < 3) return;
            FlowLayer.RowDefinitions[0].Height = new GridLength(_settings.FlowSplit, GridUnitType.Star);
            FlowLayer.RowDefinitions[2].Height = new GridLength(1 - _settings.FlowSplit, GridUnitType.Star);
        }

        private void CloseFlow()
        {
            if (_flowView == null) return;

            var hadFocus = _flowView.IsKeyboardFocusWithin;
            _flowView.Flush();
            _flowView = null;
            _flowDocument = null;
            _flowPage = null;
            FlowLayer.Children.Clear();
            FlowLayer.Visibility = Visibility.Collapsed;
            if (hadFocus && Active != null) Active.TextArea.Focus();
        }

        /// <summary>
        /// 文書を読み直した・ページの並びが変わったあと。フローの文書がもう画面に無い・ページが消えたなら閉じ、ページが変わっていれば読み直す。
        /// </summary>
        private void ReloadFlowIfChanged()
        {
            if (_flowView == null) return;

            if (!_stack.Any(view => ReferenceEquals(view.Owner, _flowDocument)) || !_flowDocument.Contains(_flowPage))
            {
                CloseFlow();
                return;
            }
            _flowView.ReloadIfChanged();
        }

        /// <summary>保存の前。直しかけのフローをページに書く。</summary>
        private void FlushFlow()
        {
            if (_flowView != null) _flowView.Flush();
        }

        // ---------------------------------------------------------------
        // 添付
        // ---------------------------------------------------------------

        /// <summary>
        /// カードに落としたファイルを添付にする。本文へのドロップと同じく、添付フォルダの中のものはそのまま、外のものは添付フォルダにコピーする（Localize）。
        /// 1つでも取り込めなければ、何も添付しない。
        /// </summary>
        private List<FlowAttachment> ImportFlowFiles(PageDocument document, string[] paths)
        {
            var result = new List<FlowAttachment>();
            try
            {
                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) throw new InvalidOperationException("フォルダは添付できません: " + path);
                    var target = Localize(document.FilePath, path);
                    result.Add(new FlowAttachment
                    {
                        Label = EscapeLabel(Path.GetFileNameWithoutExtension(path)),
                        Target = ToLinkTarget(target),
                    });
                }
            }
            catch (Exception ex)
            {
                Status("添付できませんでした: " + ex.Message);
                return null;
            }
            return result;
        }

        /// <summary>添付のリンクを、ファイルの場所にする（文書のページと同じ解き方）。解けなければ null。</summary>
        private string ResolveFlowAttachment(FlowAttachment attachment)
        {
            var view = _stack.FirstOrDefault(each => ReferenceEquals(each.Owner, _flowDocument));
            if (view == null) return null;
            var target = attachment.Target.Trim();
            if (target.StartsWith("<", StringComparison.Ordinal) && target.EndsWith(">", StringComparison.Ordinal)) target = target.Substring(1, target.Length - 2);
            return view.ResolvePath(target);
        }

        // ---------------------------------------------------------------
        // 送る
        // ---------------------------------------------------------------

        /// <summary>カードのテキストをターミナルに入れる（enter: Enter まで押す）。入れられたらカードに ✓ を付ける。</summary>
        private void SendFlowCard(FlowView view, FlowCard card, bool enter)
        {
            var text = CleanForTerminal(card.Body);
            if (text.Trim().Length == 0)
            {
                view.ShowStatus("送るテキストがありません");
                return;
            }

            if (card.Kind == FlowKind.Claude) SendFlowToClaude(view, card, text, enter);
            else SendFlowToShell(view, card, text, enter, 0);
        }

        /// <summary>
        /// このカードを送る会話を始めるカード。「新しい会話」の印のカードか、さかのぼって行き着く最初のカード（どこからもつながっていない）。
        /// 前のカードが2枚以上あれば、ページの並びで先のほうをたどる。
        /// </summary>
        private static FlowCard SessionStartOf(FlowFile flow, FlowCard card)
        {
            var seen = new HashSet<FlowCard>();
            var current = card;
            while (seen.Add(current))
            {
                if (current.NewSession) return current;
                var title = current.Title;
                var parent = flow.Cards.FirstOrDefault(other => other.Edges.Any(edge => edge.Target == title));
                if (parent == null) return current;
                current = parent;
            }
            return current;   // 輪になっていた
        }

        private void SendFlowToClaude(FlowView view, FlowCard card, string text, bool enter)
        {
            // 添付は、場所を書き添えて Claude に読ませる（Claude Code はパスのファイルを読める。画像も）
            if (card.Attachments.Count > 0)
            {
                var files = new StringBuilder();
                foreach (var attachment in card.Attachments)
                {
                    var path = ResolveFlowAttachment(attachment);
                    if (path == null || !File.Exists(path))
                    {
                        view.ShowStatus("添付のファイルが見つかりません: " + attachment.Target);
                        return;
                    }
                    files.Append("\n- ").Append(path);
                }
                text += "\n\n添付ファイル（読んでから答えて）:" + files;
            }

            var start = SessionStartOf(view.Flow, card);
            var key = view.FlowKey + "/" + start.Title;
            AiTab tab;
            if (_flowSessions.TryGetValue(key, out tab) && _aiTabs.Contains(tab) && tab.View != null && tab.View.IsRunning)
            {
                if (AiPane.Visibility != Visibility.Visible) ShowAiPane();
                if (!ReferenceEquals(_aiTab, tab)) SelectAiTab(tab);
                WriteFlowToClaude(view, card, tab, text, enter);
                return;
            }

            // 新しい会話を始めて、動き出す（claude のフックで会話の番号が届く）のを待ってから送る
            if (!CanStartAi()) return;
            AddAiTab(string.Empty);
            tab = _aiTab;
            _flowSessions[key] = tab;
            if (AiPane.Visibility != Visibility.Visible) ShowAiPane();
            view.ShowStatus("新しい会話（" + tab.Name + "）を始めています…");

            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += delegate
            {
                if (!_aiTabs.Contains(tab))
                {
                    timer.Stop();
                    view.ShowStatus("会話のタブが閉じられたので送りませんでした");
                    return;
                }
                if (tab.SessionId == null)
                {
                    if (DateTime.UtcNow - started < FlowSessionWait) return;
                    timer.Stop();
                    view.ShowStatus("会話（" + tab.Name + "）が始まらなかったので送りませんでした。始まったら、もう一度送ってください");
                    return;
                }

                // 番号が届いてから入力欄が出るまで、少し待つ
                timer.Stop();
                var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                settle.Tick += delegate
                {
                    settle.Stop();
                    WriteFlowToClaude(view, card, tab, text, enter);
                };
                settle.Start();
            };
            timer.Start();
        }

        private void WriteFlowToClaude(FlowView view, FlowCard card, AiTab tab, string text, bool enter)
        {
            var terminal = tab.View;
            if (terminal == null || !terminal.IsRunning)
            {
                view.ShowStatus("会話（" + tab.Name + "）が動いていません");
                return;
            }

            // 複数行は貼り付けの印で囲む（中の改行で送らないため。Claude Code は受け付ける）。改行は CR で送る（xterm の貼り付けと同じ）。
            // 1行は印を付けずに打つ（2026-11-17。印を付けると、続けて送った Enter が貼り付けの続きと見なされ、送信されなかった）
            var data = text.IndexOf('\n') < 0 ? text.Replace("\t", "    ") : "\x1b[200~" + text.Replace("\n", "\r") + "\x1b[201~";
            if (!terminal.WriteInput(data))
            {
                view.ShowStatus("会話のタブに書けませんでした");
                return;
            }

            AfterFlowWrite(view, card, terminal, enter, tab.Name ?? "会話");
        }

        private void SendFlowToShell(FlowView view, FlowCard card, string text, bool enter, int tries)
        {
            if (AiPane.Visibility != Visibility.Visible) ShowAiPane();
            if (_aiShell == null || !_aiShellShown) ToggleAiShell();

            // 開いたばかりの PowerShell は、動き出すまで少し待つ（5 秒まで）
            if (_aiShell == null || !_aiShell.IsRunning)
            {
                if (tries >= 25)
                {
                    view.ShowStatus("共通ターミナルが動いていません");
                    return;
                }
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                timer.Tick += delegate
                {
                    timer.Stop();
                    SendFlowToShell(view, card, text, enter, tries + 1);
                };
                timer.Start();
                view.ShowStatus("共通ターミナルを開いています…");
                return;
            }

            if (_aiShellQueue.Count > 0 || _aiShellPending)
            {
                view.ShowStatus("共通ターミナルの入力欄に文字が残っています。Enter か Ctrl+C で空にしてから送ってください");
                return;
            }

            // 1行ならそのまま打つ。複数行は、一時の .ps1 に書いて読み込ませる（PowerShell 5.1 は貼り付けの印を受け付けず、
            // 改行ごとに実行になって、if { } などが途中で切れるため）。変数などは今の PowerShell に残る（ドットで読み込む）
            string line;
            var lines = text.Split('\n');
            if (lines.Length == 1)
            {
                line = text.Replace("\t", "    ");
            }
            else
            {
                string script;
                try
                {
                    script = WriteFlowScript(card, text);
                }
                catch (Exception ex)
                {
                    view.ShowStatus("一時のスクリプトを書けませんでした: " + ex.Message);
                    return;
                }
                line = ". '" + script.Replace("'", "''") + "'";
            }

            if (!_aiShell.WriteInput(line))
            {
                view.ShowStatus("共通ターミナルに書けませんでした");
                return;
            }
            _aiShellPending = !enter;

            AfterFlowWrite(view, card, _aiShell, enter, "共通ターミナル");
        }

        /// <summary>
        /// 書いたあと。Enter まで押すなら少し待って押す（文字の直後だと、Claude Code は貼り付けの一部と見なして送信しない。
        /// 2026-11-17。0.15 秒では Enter が効かず、次のカードの文字が後ろにつながった。今は 0.6 秒）。
        /// </summary>
        private void AfterFlowWrite(FlowView view, FlowCard card, TerminalView terminal, bool enter, string where)
        {
            if (!enter)
            {
                view.MarkSent(card, where);
                view.ShowStatus(where + " に置きました（Enter は押していません）");
                return;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += delegate
            {
                timer.Stop();
                if (!terminal.WriteInput("\r"))
                {
                    view.ShowStatus("Enter を送れませんでした（文字は入っています）");
                    return;
                }
                view.MarkSent(card, where);
                view.ShowStatus(where + " に送りました");
            };
            timer.Start();
        }

        /// <summary>制御の文字を除く（貼り付けの印の終わりや Esc を混ぜて、勝手に動かさないため）。改行は LF にそろえ、最後の改行は除く。</summary>
        private static string CleanForTerminal(string text)
        {
            var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var clean = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (c == '\n' || c == '\t' || (c >= ' ' && c != '\x7f')) clean.Append(c);
            }
            return clean.ToString().TrimEnd('\n', ' ', '\t');
        }

        /// <summary>複数行の PowerShell を一時のフォルダの .ps1 に書く（%TEMP%\Tsuzuri-flow。1 日より古いものは消す）。</summary>
        private static string WriteFlowScript(FlowCard card, string text)
        {
            var folder = Path.Combine(Path.GetTempPath(), "Tsuzuri-flow");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "*.ps1"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
                }
                catch (Exception)
                {
                }
            }

            var name = new string(card.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '\'' ? '_' : c).ToArray());
            if (name.Length > 40) name = name.Substring(0, 40);
            var path = Path.Combine(folder, DateTime.Now.ToString("HHmmss") + " " + name + ".ps1");
            // PowerShell 5.1 は BOM の無い UTF-8 を ANSI として読むので、BOM を付ける
            File.WriteAllText(path, text.Replace("\n", "\r\n") + "\r\n", new UTF8Encoding(true));
            return path;
        }
    }
}
