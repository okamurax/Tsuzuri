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
    // 送り先はフローの会話（下）。利用者が送るのは最初のカードだけで、つながるカードは Claude の答えが終わる（Stop のフック）たびに順に送る（2026-11-17）。
    // PowerShell のカード・途中から新しい会話にするのは無くした。
    // 結果のカード（MainWindow.FlowResult.cs）と添付のカードは送らず、つないだ先の Claude のカードに中身・ファイルの場所を渡す。
    public partial class MainWindow
    {
        private FlowView _flowView;
        private PageDocument _flowDocument;
        private string _flowPage;

        /// <summary>
        /// フローの会話（2026-11-17）。フローに1つ。最初に送ったときに新しい会話を始め、続くカードもその会話へ送る。
        /// 見出しは「文書#ページ」。この画面の会話のタブを指す（閉じた・終わったら、最初のカードを送るときに作り直す）。
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
            _flowView.StopRequested += delegate { StopFlowRun("次のカードへ進むのをやめました"); };

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

        /// <summary>
        /// 自動で進めている送り（2026-11-17）。最初のカードを送ったら、つながるカードを、Claude の答えが終わる（Stop のフック）たびに1枚ずつ送る。
        /// </summary>
        private sealed class FlowRun
        {
            public FlowView View;
            public AiTab Tab;
            public readonly Queue<string> Rest = new Queue<string>();
            public string Current;

            /// <summary>送った（Enter まで押した）カードの答えを待っている。</summary>
            public bool Waiting;
            public bool Stopped;
        }

        private FlowRun _flowRun;

        /// <summary>最初のカードの「▶ 送る」。続くカードを順に送る送りを始める。</summary>
        private void SendFlowCard(FlowView view, FlowCard card, bool enter)
        {
            if (!view.IsStart(card)) return;

            StopFlowRun(null);
            foreach (var watch in _flowWatches.Where(each => each.Key == view.FlowKey).ToList()) StopFlowWatch(watch);   // 前の送りの結果はもう待たない
            var run = new FlowRun { View = view };
            foreach (var next in FlowResults.Order(view.Flow, card)) run.Rest.Enqueue(next.Title);
            _flowRun = run;
            view.SetRunning(true);
            SendFlowStep(run, card);
        }

        /// <summary>自動で進めるのをやめる（「止める」・送れなかった・フローを閉じた）。why があれば知らせる。</summary>
        private void StopFlowRun(string why)
        {
            var run = _flowRun;
            if (run == null) return;
            _flowRun = null;
            run.Stopped = true;
            run.View.SetRunning(false);
            if (why != null)
            {
                run.View.ShowStatus(why);
                Status(why);
            }
        }

        /// <summary>1枚送る。送れなければ送りをやめる。</summary>
        private void SendFlowStep(FlowRun run, FlowCard card)
        {
            string problem;
            var text = FlowResults.Compose(run.View.Flow, card, out problem);
            if (problem == null)
            {
                text = CleanForTerminal(text);
                if (text.Trim().Length == 0) problem = "送るテキストが空のカードがあるので止めました";
            }
            if (problem != null)
            {
                StopFlowRun(problem);
                return;
            }
            run.Current = card.Title;
            SendFlowToClaude(run, card, text);
        }

        /// <summary>
        /// claude が答え終わった（Stop のフック。2026-11-17）。送りの会話なら、受け取る結果を読み切ってから、次のカードを送る。
        /// フックはタブの番号（パイプ）か会話の番号（HTTP）で来る。
        /// </summary>
        private void OnAiTurnEnded(string tabId, string session)
        {
            var run = _flowRun;
            if (run == null || !run.Waiting) return;
            var tab = _aiTabs.FirstOrDefault(each => (tabId != null && each.Id == tabId) || (session != null && each.SessionId == session));
            if (!ReferenceEquals(tab, run.Tab)) return;

            run.Waiting = false;
            AfterFlowTurn(run, 0);
        }

        /// <summary>答え終わったあと。記録が書き終わるのを少し待ち、結果を読んでから次へ。結果がまだ読めなければ、1 秒おきに 5 回まで待つ。</summary>
        private void AfterFlowTurn(FlowRun run, int tries)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(tries == 0 ? 800 : 1000) };
            timer.Tick += delegate
            {
                timer.Stop();
                if (run.Stopped) return;
                foreach (var watch in _flowWatches.Where(each => ReferenceEquals(each.Tab, run.Tab)).ToList()) PollFlowWatch(watch);

                if (!ReferenceEquals(_flowView, run.View))
                {
                    StopFlowRun("フローを閉じたので、次のカードへ進むのをやめました");
                    return;
                }
                if (_flowWatches.Any(each => ReferenceEquals(each.Tab, run.Tab) && each.Key == run.View.FlowKey))
                {
                    if (tries < 5)
                    {
                        AfterFlowTurn(run, tries + 1);
                        return;
                    }
                    StopFlowRun("Claude の答えに結果の印が見つからなかったので止めました（結果のカードに直接書いて、続きのカードを送り直せます）");
                    return;
                }

                FlowCard next = null;
                while (next == null && run.Rest.Count > 0)
                {
                    var card = run.View.Flow.Find(run.Rest.Dequeue());
                    if (card != null && card.Kind == FlowKind.Claude) next = card;
                }
                if (next == null)
                {
                    StopFlowRun("フローを最後まで送りました");
                    return;
                }
                SendFlowStep(run, next);
            };
            timer.Start();
        }

        private void SendFlowToClaude(FlowRun run, FlowCard card, string text)
        {
            var view = run.View;
            var wants = FlowResults.WantsOf(view.Flow, card);
            if (wants.Count > 0) text += FlowResults.Instruction(wants);

            // つないだ添付のカードのファイルは、場所を書き添えて Claude に読ませる（Claude Code はパスのファイルを読める。画像も）
            var attachments = FlowResults.FilesFor(view.Flow, card);
            if (attachments.Count > 0)
            {
                var files = new StringBuilder();
                foreach (var attachment in attachments)
                {
                    var path = ResolveFlowAttachment(attachment);
                    if (path == null || !File.Exists(path))
                    {
                        StopFlowRun("添付のファイルが見つからないので止めました: " + attachment.Target);
                        return;
                    }
                    files.Append("\n- ").Append(path);
                }
                text += "\n\n添付ファイル（読んでから答えて）:" + files;
            }

            // 会話はフローに1つ（2026-11-17。途中から新しい会話にするのは無くした）。閉じた・終わっていたら作り直す
            AiTab tab;
            if (_flowSessions.TryGetValue(view.FlowKey, out tab) && _aiTabs.Contains(tab) && tab.View != null && tab.View.IsRunning)
            {
                run.Tab = tab;
                if (AiPane.Visibility != Visibility.Visible) ShowAiPane();
                if (!ReferenceEquals(_aiTab, tab)) SelectAiTab(tab);
                WriteFlowToClaude(run, card, tab, text, wants);
                return;
            }
            if (!view.IsStart(card))   // 新しい会話を始めるのは最初のカードだけ
            {
                StopFlowRun("フローの会話のタブが閉じたので止めました");
                return;
            }

            // 新しい会話を始めて、動き出す（claude のフックで会話の番号が届く）のを待ってから送る
            if (!CanStartAi())
            {
                StopFlowRun(null);
                return;
            }
            AddAiTab(string.Empty);
            tab = _aiTab;
            run.Tab = tab;
            _flowSessions[view.FlowKey] = tab;
            if (AiPane.Visibility != Visibility.Visible) ShowAiPane();
            view.ShowStatus("新しい会話（" + tab.Name + "）を始めています…");

            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += delegate
            {
                if (run.Stopped)
                {
                    timer.Stop();
                    return;
                }
                if (!_aiTabs.Contains(tab))
                {
                    timer.Stop();
                    StopFlowRun("会話のタブが閉じられたので送りませんでした");
                    return;
                }
                if (tab.SessionId == null)
                {
                    if (DateTime.UtcNow - started < FlowSessionWait) return;
                    timer.Stop();
                    StopFlowRun("会話（" + tab.Name + "）が始まらなかったので送りませんでした。始まったら、もう一度送ってください");
                    return;
                }

                // 番号が届いてから入力欄が出るまで、少し待つ
                timer.Stop();
                var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                settle.Tick += delegate
                {
                    settle.Stop();
                    if (!run.Stopped) WriteFlowToClaude(run, card, tab, text, wants);
                };
                settle.Start();
            };
            timer.Start();
        }

        private void WriteFlowToClaude(FlowRun run, FlowCard card, AiTab tab, string text, List<FlowWant> wants)
        {
            var view = run.View;
            var terminal = tab.View;
            if (terminal == null || !terminal.IsRunning)
            {
                StopFlowRun("会話（" + tab.Name + "）が動いていないので止めました");
                return;
            }

            // 複数行は貼り付けの印で囲む（中の改行で送らないため。Claude Code は受け付ける）。改行は CR で送る（xterm の貼り付けと同じ）。
            // 1行は印を付けずに打つ（2026-11-17。印を付けると、続けて送った Enter が貼り付けの続きと見なされ、送信されなかった）
            var data = text.IndexOf('\n') < 0 ? text.Replace("\t", "    ") : "\x1b[200~" + text.Replace("\n", "\r") + "\x1b[201~";
            var watch = wants.Count > 0 ? PrepareFlowWatch(view, tab, wants) : null;
            if (!terminal.WriteInput(data))
            {
                StopFlowRun("会話のタブに書けなかったので止めました");
                return;
            }
            if (watch != null) StartFlowWatch(view, watch);

            // 少し待って Enter を押す（文字の直後だと、Claude Code は貼り付けの一部と見なして送信しない。
            // 2026-11-17。0.15 秒では Enter が効かず、次のカードの文字が後ろにつながった。今は 0.6 秒）
            var where = tab.Name ?? "会話";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += delegate
            {
                timer.Stop();
                if (!terminal.WriteInput("\r"))
                {
                    StopFlowRun("Enter を送れなかったので止めました（文字は入っています）");
                    return;
                }
                view.MarkSent(card, where);
                if (run.Stopped) return;
                run.Waiting = true;
                view.ShowStatus(run.Rest.Count > 0
                    ? where + " に送りました。答えが終わったら次のカードを送ります（残り " + run.Rest.Count + " 枚以内）"
                    : where + " に送りました（最後のカード）");
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
    }
}
