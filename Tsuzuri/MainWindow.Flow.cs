using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: フロー（2026-11-17。SPEC 21章）。AI の欄のターミナルへ流し込むテキストを、カードとして並べてつなぎ、1枚ずつ送る。
    // 元は文書の添付フォルダの `名前.flow.md`（テキスト）。本文にはそのリンクを置き、押すとフロー図（Views/FlowView）を本文の場所の上に重ねて出す。
    // ランチャーと同じ重ね方だが、ほぼフロー図で直すので、高さを多めに取る（既定 75%。仕切りで変えられ、覚える）。下の本文は見えたまま。
    // 送り先: Claude のカードは選んでいる会話のタブ、PowerShell のカードは共通ターミナル。送るのは利用者が1枚ずつ（自動では進めない）。
    public partial class MainWindow
    {
        private FlowView _flowView;

        /// <summary>フロー図の高さの割合の既定（仕切りのダブルクリックで戻す）。</summary>
        private const double DefaultFlowSplit = 0.75;

        /// <summary>フローのファイルか（名前が .flow.md で終わる）。</summary>
        internal static bool IsFlowFile(string path)
        {
            return path != null && path.EndsWith(".flow.md", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>ツールバーの「フローを追加」。添付フォルダにフローのファイルを作り、カーソルの位置にリンクを入れて開く。</summary>
        private void InsertFlow_Click(object sender, RoutedEventArgs e)
        {
            var view = Active;
            if (!CanInsertInto(view) || !HasAttachmentFolder(view, "フローを足せません")) return;

            string path;
            try
            {
                var folder = Attachments.FolderOf(view.FilePath);
                var stem = "フロー " + DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                path = Path.Combine(folder, stem + ".flow.md");
                for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, stem + " (" + n + ").flow.md");

                var flow = new FlowFile { Preamble = "# " + Path.GetFileName(path).Replace(".flow.md", string.Empty) };
                flow.Cards.Add(new FlowCard { Kind = FlowKind.Claude, Title = "頼む", X = 40, Y = 40, HasPosition = true });
                PageDocument.WriteAtomically(path, new UTF8Encoding(false).GetBytes(flow.Serialize()));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "フローのファイルを作れませんでした。" + Environment.NewLine + ex.Message, "フロー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var name = Path.GetFileName(path);
            view.InsertAtCaret("[" + EscapeLabel(name.Substring(0, name.Length - ".flow.md".Length)) + "](" + ToLinkTarget(name) + ")");
            Status("フローを入れました: " + name);
            OpenFlow(path);
        }

        /// <summary>本文のリンクがフローのファイルなら、フロー図で開く。無ければ作るか尋ねる。フローでなければ false。</summary>
        private bool TryOpenFlow(string path)
        {
            if (!IsFlowFile(path)) return false;

            if (!File.Exists(path))
            {
                var answer = MessageBox.Show(this, "フローのファイルがありません。作りますか？" + Environment.NewLine + path,
                    "フロー", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return true;
                try
                {
                    var folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    PageDocument.WriteAtomically(path, new byte[0]);
                }
                catch (Exception ex)
                {
                    Status("作れませんでした: " + ex.Message);
                    return true;
                }
            }

            OpenFlow(path);
            return true;
        }

        private void OpenFlow(string path)
        {
            if (_launcher != null) CloseLauncher();

            if (_flowView != null && string.Equals(_flowView.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _flowView.ReloadIfChanged();
                _flowView.Focus();
                return;
            }
            CloseFlow();

            _flowView = new FlowView(path);
            _flowView.CloseRequested += CloseFlow;
            _flowView.SendRequested += (card, enter) => SendFlowCard(_flowView, card, enter);

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
            FlowLayer.Children.Clear();
            FlowLayer.Visibility = Visibility.Collapsed;
            if (hadFocus && Active != null) Active.TextArea.Focus();
        }

        /// <summary>Claude がファイルを書き換えたあと（フック）。開いているフローなら読み直す。</summary>
        private void ReloadFlowIfChanged()
        {
            if (_flowView != null) _flowView.ReloadIfChanged();
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

        private void SendFlowToClaude(FlowView view, FlowCard card, string text, bool enter)
        {
            if (_aiTab == null || _aiTab.View == null || !_aiTab.View.IsRunning)
            {
                view.ShowStatus("AI の欄で会話を始めてから送ってください（ツールバーの「AI」）");
                return;
            }
            if (AiPane.Visibility != Visibility.Visible) ShowAiPane();

            // 貼り付けの印で囲む（中の改行で送らないため。Claude Code は受け付ける）。改行は CR で送る（xterm の貼り付けと同じ）
            var terminal = _aiTab.View;
            if (!terminal.WriteInput("\x1b[200~" + text.Replace("\n", "\r") + "\x1b[201~"))
            {
                view.ShowStatus("会話のタブに書けませんでした");
                return;
            }

            AfterFlowWrite(view, card, terminal, enter, "Claude（" + (_aiTab.Name ?? "会話") + "）");
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

        /// <summary>書いたあと。Enter まで押すなら少し待って押す（貼り付けの直後だと、貼り付けの一部と見なされることがある）。</summary>
        private void AfterFlowWrite(FlowView view, FlowCard card, TerminalView terminal, bool enter, string where)
        {
            if (!enter)
            {
                view.MarkSent(card);
                view.ShowStatus("「" + card.Title + "」を" + where + "に置きました（Enter は押していません）");
                return;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += delegate
            {
                timer.Stop();
                if (!terminal.WriteInput("\r"))
                {
                    view.ShowStatus("Enter を送れませんでした（文字は入っています）");
                    return;
                }
                view.MarkSent(card);
                view.ShowStatus("「" + card.Title + "」を" + where + "に送りました");
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
