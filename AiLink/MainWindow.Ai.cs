using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiLink.Agents;
using AiLink.Views;

namespace AiLink
{
    // MainWindow の一部: AI の欄の会話のタブと、台本（Ai/*.ps1）からの頼みを受けるパイプ（Tsuzuri の MainWindow.Ai.cs から写したもの。2026-11-25）。
    //
    // 欄はいつも出しておく。会話のタブごとに別の PowerShell が動く（端末は Views/TerminalView。WebView2 の xterm.js と ConPTY）。
    // タブを開くと PowerShell の中で AI（Codex）を起動する。起動のコマンド・指示・フック・会話の続きは AiLink/SPEC.md（7〜9章）。
    // 中の PowerShell には環境変数でタブの番号（AILINK_TAB）とパイプの名前（AILINK_PIPE）を渡す。台本（state.ps1・excel.ps1・talk.ps1）はこのパイプで頼んでくる。
    public partial class MainWindow
    {
        /// <summary>AI に渡す台本・書き出したファイルを置くところ。Dropbox などの同期の外。</summary>
        private static readonly string AiFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiLink", "ai");

        /// <summary>このプロセスの番号。窓は1つだけなので、ファイルとパイプの名前はこれで分ける。</summary>
        private static readonly string AiProcessId = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

        /// <summary>台本が頼んでくるパイプの名前。</summary>
        private static readonly string AiPipeName = "AiLink-" + AiProcessId;

        /// <summary>exe の隣の Ai から AI のフォルダへ写す台本。</summary>
        private static readonly string[] AiScripts = { "excel.ps1", "state.ps1", "talk.ps1", "hook.ps1" };

        /// <summary>前に落ちた AiLink が残したファイルを片付けたか（最初にタブを開くとき1回だけ）。</summary>
        private bool _aiStaleFilesCleaned;

        private Thread _aiListener;

        /// <summary>つながりを待っている口（閉じるときに閉じて、待つスレッドを終わらせる）。</summary>
        private volatile NamedPipeServerStream _aiWaiting;

        /// <summary>窓を閉じている最中か。閉じるときに会話が終わっても、開いていたタブの覚えを消さないようにする。</summary>
        private volatile bool _aiStopping;

        /// <summary>AI の欄の会話のタブ1つ。タブごとに別の PowerShell（第3段からは AI も）が動く。裏のタブも動き続ける。</summary>
        private sealed class AiTab
        {
            public TerminalView View;
            public Border Header;
            public TextBlock Title;

            /// <summary>タブを見分ける番号。中の PowerShell に環境変数 AILINK_TAB で渡す。</summary>
            public readonly string Id = "t" + Guid.NewGuid().ToString("N");   // 頭に t（AI の起動のオプション -c で渡すとき、数字だけの番号が数として読まれないように）

            /// <summary>中で動いている AI の会話の番号と、そのフォルダ。AI を起動していなければ null（次の起動で戻さない）。</summary>
            public string SessionId;
            public string Folder;

            /// <summary>タブの名前（#1・#2…）。ほかの会話に伝言を頼むときの宛先になる。# を付けるのは、話の中の数と取り違えないため。</summary>
            public string Name;

            /// <summary>この会話の題（見出しに「#1  題」と出す）。無ければ null。</summary>
            public string Topic;

            /// <summary>端末のタイトル（ツールチップに出す）。</summary>
            public string TerminalTitle;

            /// <summary>このタブで AI を一度起動したか（起動するのは最初の1回だけ）と、その時刻。</summary>
            public bool AgentStarted;
            public DateTime AgentStartedAt;

            /// <summary>フックが動いていないことを、欄の下の行で知らせた。</summary>
            public bool HookWarned;

            /// <summary>起動するときに続きとして開く会話の番号（前の起動の会話を戻すとき）。無ければ null（新しい会話）。</summary>
            public string ResumeSession;

            /// <summary>AI が会話を始めて、頼みを受け付けられる（会話の始まりのフックが来た。終わりのフックで false）。</summary>
            public bool AgentReady;

            /// <summary>AI が頼みに取り組んでいる最中（話しかけたフックから、作業の終わりのフックまで。確かめを出している間もこの中）。</summary>
            public bool Busy
            {
                get { return _busy; }
                set
                {
                    if (value && !_busy) BusySince = DateTime.UtcNow;
                    _busy = value;
                }
            }
            private bool _busy;

            /// <summary>Busy になった時刻（作業の終わりのフックが来ないまま長く残ったら、画面の確かめに任せる。MainWindow.AiTalk.cs）。</summary>
            public DateTime BusySince;

            /// <summary>利用者が話しかけないまま、伝言を自動で渡した回数（AI どうしがやりとりし続けないための歯止め。MainWindow.AiTalk.cs）。</summary>
            public int AutoTalks;

            /// <summary>AI が最後に頼みを終えた（か会話を始めた）時刻。直後には伝言を打たない（起動の案内などの画面に Enter が効かないように）。</summary>
            public DateTime ReadySince;

            /// <summary>AiLink が伝言を打って Enter を送った時刻と、それが頼みとして受け付けられるのを待っているか。</summary>
            public DateTime TypedAt;
            public bool AwaitingPrompt;

            /// <summary>伝言を打つ前に、画面を読んで入力欄が空かを確かめている最中（重ねて確かめない）。</summary>
            public bool Checking;

            /// <summary>利用者がこのタブで最後にキーを打った時刻。</summary>
            public DateTime LastUserKey;

            /// <summary>前の Enter から利用者が打った字の数（2 字以上を打って Enter なら、AI に話しかけたとみなす。確かめの「2」と Enter は数えない）。</summary>
            public int TypedSinceEnter;

            /// <summary>利用者がこのタブで最後に AI に話しかけた（2 字以上を打って Enter）時刻。まだなら既定の値。</summary>
            public DateTime PromptedAt;

            /// <summary>
            /// このタブの AI からフックが一度でも来たか。来ていなければ（フックを信頼する前に始まった会話など）、頼みを受け付けたかが分からないので、
            /// 伝言は自動では渡さず、ボタンで渡す（MainWindow.AiTalk.cs）。
            /// </summary>
            public bool HooksSeen;

            /// <summary>このタブに最後に今の状態を渡したときの、欄の下の行の文。</summary>
            public string ContextLine;

            /// <summary>ほかの会話からの伝言の受け箱（まだ入力欄に置いていないもの。見出しに印を出す。MainWindow.AiTalk.cs）。</summary>
            public readonly List<string> Inbox = new List<string>();
        }

        /// <summary>会話のタブ。左から。</summary>
        private readonly List<AiTab> _aiTabs = new List<AiTab>();

        /// <summary>選んでいるタブ。無ければ null。</summary>
        private AiTab _aiTab;

        /// <summary>タブの段の「＋」。</summary>
        private Button _aiAddButton;

        /// <summary>アイコンのフォント。</summary>
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>
        /// 窓が出たら、前に開いていた会話のタブを戻す。1つも無ければ新しいタブを1つ開く（欄はいつも出しているので、空にしない）。
        /// AI が入っていなければ、タブは開かず案内を出す（入ったら RecheckAgent がもう一度呼ぶ）。
        /// </summary>
        private void StartAi()
        {
            if (_aiTabs.Count > 0) return;
            if (!EnsureAgent()) return;   // 入っていなければ、タブを開かずに入れ方の案内を出す（MainWindow.AiSetup.cs）

            var saved = _settings.AiTabs;
            if (saved != null)
            {
                // AI がその会話を残しているものだけ戻す（話しかける前に閉じた会話は残っていない）
                foreach (var state in saved.Where(state => _agent.SessionExists(state.Session)).ToList()) AddAiTab(state);
            }
            if (_aiTabs.Count == 0) AddAiTab(null);
            else SelectAiTab(_aiTabs[0]);
        }

        /// <param name="restore">null でなければ、前の起動で開いていた会話。</param>
        private void AddAiTab(AiTabState restore)
        {
            var tab = new AiTab();
            if (restore != null)
            {
                tab.SessionId = restore.Session;
                tab.ResumeSession = restore.Session;
                tab.Folder = restore.Folder;
                tab.Topic = string.IsNullOrWhiteSpace(restore.Topic) ? null : restore.Topic;
                var name = restore.Name != null && restore.Name.StartsWith("#", StringComparison.Ordinal) ? restore.Name : null;
                if (name != null && !AiTabNameUsed(name)) tab.Name = name;
            }
            if (tab.Name == null) tab.Name = NewAiTabName();

            tab.View = new TerminalView(() => AiCommandLine(tab), () => AiStartFolder(tab));
            tab.View.Visibility = Visibility.Collapsed;
            AiTerminals.Children.Add(tab.View);

            tab.Title = new TextBlock { Text = AiTabHeaderText(tab), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            tab.Header = CreateAiTabHeader(tab);
            tab.Header.ToolTip = AiTabToolTip(tab);
            tab.View.TitleChanged += title =>
            {
                tab.TerminalTitle = (title ?? string.Empty).Trim();
                tab.Header.ToolTip = AiTabToolTip(tab);
            };
            tab.View.UserInput += data => OnAiTabInput(tab, data);
            tab.View.FocusRequested += delegate
            {
                // 押したタブが今のタブなら渡し直す（タブを選んだときと同じ手順。日本語の変換の位置がずれないように）
                if (ReferenceEquals(_aiTab, tab)) FocusAiTerminal(tab);
            };
            _aiTabs.Add(tab);
            RebuildAiTabStrip();
            SelectAiTab(tab);
        }

        /// <summary>
        /// 会話を始めるフォルダ。いつも AiWorkFolder（指示の AGENTS.md があり、AI が書ける場所。前の会話を戻すときも同じ）。
        /// （プロジェクトフォルダは持たない。2026-11-25。利用者の判断。扱うのは開いている Excel で、ファイルが要るときは端末へ落としてもらう）
        /// </summary>
        private string AiStartFolder(AiTab tab)
        {
            // 端末は、コマンド（AiCommandLine）より先にフォルダを尋ねるので、ここでも作る。
            // 作れなかったときは利用者のフォルダで PowerShell だけを起動する（AiCommandLine の PrepareAiFolder も同じフォルダを作れずに失敗し、AI を起動しない）
            try
            {
                Directory.CreateDirectory(AiWorkFolder);
                return AiWorkFolder;
            }
            catch (Exception)
            {
                return AiHomeFolder;
            }
        }

        /// <summary>
        /// AI の会話を始めるフォルダ（%LOCALAPPDATA%\AiLink\work）。扱うのは開いている Excel なので、AI に利用者のフォルダ全体を作業の場所として渡さない
        /// （Codex は作業のフォルダの中を自由に読み、そこに書ける）。PrepareAiFolder が作る（作れなければ AI は起動しない）。
        /// </summary>
        private static string AiWorkFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiLink", "work"); }
        }

        /// <summary>利用者のフォルダ。</summary>
        private static string AiHomeFolder
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }

        /// <summary>タブの見出し。押すと選ぶ。右の × で閉じる（中の PowerShell も終わる）。</summary>
        private Border CreateAiTabHeader(AiTab tab)
        {
            var close = new Button
            {
                Content = "",
                ToolTip = "この会話を閉じる（中の PowerShell も終わる）",
                FontFamily = IconFont,
                FontSize = 9,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var style = TryFindResource("ToolButton") as Style;
            if (style != null) close.Style = style;
            close.Click += delegate { CloseAiTab(tab); };

            // 名前は「#1」のように短いので、タブに最小の幅を持たせ、× を右端に寄せて、残りを全部押せる所にする
            var panel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(close);
            panel.Children.Add(tab.Title);

            var header = new Border
            {
                Child = panel,
                Height = 26,
                MinWidth = 120,
                MaxWidth = 320,
                Background = Brushes.Transparent,   // 文字の無い所も押せるように
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.MouseLeftButtonUp += (sender, e) =>
            {
                if (IsButtonClick(e.OriginalSource as DependencyObject)) return;   // × は閉じる
                SelectAiTab(tab);
            };
            return header;
        }

        /// <summary>押した所がボタン（見出しの ×）の中か。</summary>
        private static bool IsButtonClick(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ButtonBase) return true;
                if (source is Border) return false;
                var visual = source as Visual;
                if (visual == null) return false;
                source = VisualTreeHelper.GetParent(visual);
            }
            return false;
        }

        private static string AiTabHeaderText(AiTab tab)
        {
            var text = string.IsNullOrWhiteSpace(tab.Topic) ? tab.Name : tab.Name + "  " + tab.Topic;
            return tab.Inbox.Count > 0 ? text + "  ●伝言" : text;
        }

        private static string AiTabToolTip(AiTab tab)
        {
            var text = new StringBuilder("会話の名前: ").Append(tab.Name);
            if (!string.IsNullOrEmpty(tab.Topic)) text.Append(Environment.NewLine).Append("題: ").Append(tab.Topic);
            text.Append(Environment.NewLine).Append(tab.HooksSeen ? "フック: 動いている" : "フック: まだ来ていない（Codex を開き直し、「Hooks need review」で信頼すると動く）");
            if (!string.IsNullOrEmpty(tab.TerminalTitle)) text.Append(Environment.NewLine).Append(tab.TerminalTitle);
            return text.ToString();
        }

        private bool AiTabNameUsed(string name)
        {
            return _aiTabs.Any(tab => tab.Name == name);
        }

        /// <summary>新しいタブの名前。使っていないいちばん小さい番号（#1・#2…）。</summary>
        private string NewAiTabName()
        {
            var number = 1;
            while (AiTabNameUsed("#" + number.ToString(CultureInfo.InvariantCulture))) number++;
            return "#" + number.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>選んでいるタブの端末だけを出し、ほかは隠す（隠しても動き続ける）。選んでいるタブは青。</summary>
        private void ShowAiTabs()
        {
            foreach (var other in _aiTabs)
            {
                var selected = ReferenceEquals(other, _aiTab);
                other.View.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                other.Header.Background = selected ? (Brush)FindResource("AccentFill") : Brushes.Transparent;
                other.Title.Foreground = (Brush)FindResource(selected ? "AccentText" : "TextMuted");
            }
        }

        /// <summary>タブの段を並べ直す（タブ、その右に「＋」）。</summary>
        private void RebuildAiTabStrip()
        {
            if (_aiAddButton == null)
            {
                _aiAddButton = new Button
                {
                    Content = "",
                    ToolTip = "新しい会話を足す",
                    FontFamily = IconFont,
                    FontSize = 11,
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var style = TryFindResource("ToolButton") as Style;
                if (style != null) _aiAddButton.Style = style;
                _aiAddButton.Click += delegate { AddAiTab(null); };
            }

            AiTabs.Children.Clear();
            foreach (var tab in _aiTabs) AiTabs.Children.Add(tab.Header);
            AiTabs.Children.Add(_aiAddButton);
        }

        /// <summary>タブを選ぶ。そのタブの端末を出し、ほかは隠す。</summary>
        private void SelectAiTab(AiTab tab)
        {
            _aiTab = tab;
            ShowAiInbox();
            AiContextText.Text = tab != null ? tab.ContextLine ?? string.Empty : string.Empty;
            ShowAiTabs();
            if (tab != null) FocusAiTerminal(tab);
        }

        /// <summary>端末にキーボードを渡す前に、いったん移す受け皿（見えない文字の欄）。</summary>
        private TextBox _aiFocusSink;

        /// <summary>
        /// タブの端末にキーボードを渡す。いったん受け皿の文字の欄に移してから渡す（端末（WebView2）にじかに移すと、
        /// 日本語の変換が窓の左上に出ることがあった。Tsuzuri で見つけたこと）。
        /// </summary>
        private void FocusAiTerminal(AiTab tab)
        {
            Keyboard.Focus(AiFocusSink());
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ReferenceEquals(_aiTab, tab)) tab.View.FocusTerminal();
            }), DispatcherPriority.Background);
        }

        private TextBox AiFocusSink()
        {
            if (_aiFocusSink == null)
            {
                _aiFocusSink = new TextBox
                {
                    Width = 1,
                    Height = 1,
                    Opacity = 0,
                    IsTabStop = false,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                AiTerminals.Children.Insert(0, _aiFocusSink);   // 端末の下（重ねて置いた一番奥）
                // 移している一瞬に打たれた文字は、端末に渡せないので捨てる
                _aiFocusSink.LostKeyboardFocus += delegate { _aiFocusSink.Clear(); };
            }
            return _aiFocusSink;
        }

        /// <summary>タブを閉じる。中の PowerShell も終わらせる。最後の1つを閉じたら、新しいタブを1つ開く（欄を空にしない）。</summary>
        private void CloseAiTab(AiTab tab)
        {
            var index = _aiTabs.IndexOf(tab);
            if (index < 0) return;

            tab.View.Dispose();
            AiTerminals.Children.Remove(tab.View);
            _aiTabs.RemoveAt(index);
            RebuildAiTabStrip();
            StoreAiTabs();   // 自分で閉じた会話は、次の起動で戻さない

            if (_aiTabs.Count == 0)
            {
                _aiTab = null;
                ShowAiInbox();
            AiContextText.Text = tab != null ? tab.ContextLine ?? string.Empty : string.Empty;
                AddAiTab(null);
                return;
            }
            if (ReferenceEquals(_aiTab, tab)) SelectAiTab(_aiTabs[Math.Min(index, _aiTabs.Count - 1)]);
        }

        /// <summary>
        /// 会話のあるタブを、並びのまま ailink.json に書く（次の起動で戻す）。AI を起動していないタブは書かない。
        /// 閉じている最中は書かない。
        /// </summary>
        private void StoreAiTabs()
        {
            if (_aiStopping) return;

            var tabs = _aiTabs.Where(tab => tab.SessionId != null)
                              .Select(tab => new AiTabState { Session = tab.SessionId, Folder = tab.Folder, Name = tab.Name, Topic = tab.Topic })
                              .ToList();
            _settings.AiTabs = tabs.Count > 0 ? tabs : null;
            _settings.Save();
        }

        // =====================================================================
        // 中の PowerShell の起動
        // =====================================================================

        /// <summary>
        /// タブの端末で起動するコマンド。PowerShell を起動し、AI が入っていれば続けて AI を起動する（起動の細かいことは第3段で IAgent へ移す）。
        /// 台本を AI のフォルダへ写し、パイプを開いてから、環境変数を置いた PowerShell を返す。用意に失敗しても PowerShell は起動する。
        /// </summary>
        private string AiCommandLine(AiTab tab)
        {
            var powershell = "\"" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") + "\" -NoLogo";

            // exe の隣に置いた git（MinGit）を PATH に足す。AI が変更の履歴を読むときに自分で git を動かすため
            var bundled = GitLocator.BundledGitFolder;
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (Directory.Exists(bundled) && path.IndexOf(bundled, StringComparison.OrdinalIgnoreCase) < 0)
                Environment.SetEnvironmentVariable("PATH", bundled + ";" + path);

            try
            {
                PrepareAiFolder();
            }
            catch (Exception ex)
            {
                Status("AI に渡す台本を用意できませんでした: " + ex.Message);
                return powershell;
            }

            StartAiListener();

            // Tsuzuri の環境変数は消す（Tsuzuri から起動したときに受け継がないように。excel.ps1 は Tsuzuri と同じファイル。2026-11-26）
            var command = "Remove-Item Env:TSUZURI_AI_PIPE,Env:TSUZURI_AI_NAME,Env:MDEDITOR_AI_TAB -ErrorAction SilentlyContinue;$env:AILINK_TAB=" + Quote(tab.Id) + ";$env:AILINK_PIPE=" + Quote(AiPipeName) + ";$env:AILINK_WORK=" + Quote(AiWorkFolder)
                          + ";$env:AILINK_NAME=" + Quote(AiTabFileName(tab));

            // AI を起動する（2026-11-25。第3段の最初。指示・フック・会話の続きはまだ渡さない）。
            // PowerShell の関数 codex を置き、終わったあとに codex と打っても同じ AI が動くようにする。PATH も足す（AiLink を起動したあとに入れた AI でも動くように）。
            // 起動するのはタブを開いた最初の1回だけ（終わったあとに Enter で開き直した PowerShell では起動しない）
            var agent = _agentExecutable;
            if (agent != null)
            {
                command += ";$env:PATH=" + Quote(Path.GetDirectoryName(agent) + ";") + "+$env:PATH"
                           + ";function codex { & " + Quote(agent)
                           // AI が動かすコマンドにも、タブ・パイプ・作業フォルダを渡す（Codex はタブの PowerShell の環境変数を、AI のコマンドに引き継がなかった。
                           // 2026-11-25 に Windows で試して分かった。state.ps1 が「AILINK_PIPE is not set」になった）。値は Codex が TOML として読めなければ文字のまま使う
                           + " -c " + Quote("shell_environment_policy.set.AILINK_TAB=" + tab.Id)
                           + " -c " + Quote("shell_environment_policy.set.AILINK_PIPE=" + AiPipeName)
                           + " -c " + Quote("shell_environment_policy.set.AILINK_WORK=" + AiWorkFolder)
                           + " -c " + Quote("shell_environment_policy.set.AILINK_NAME=" + AiTabFileName(tab))
                           // AGENTS.md を読む量の上限を上げる（2026-11-26。Codex は初めの値 32KiB を超えた分を黙って捨てる。AiLink の指示だけで約 39KB あり、
                           // 終わりの段（伝言・ファイル・Excel で AI にいつも伝えること）が渡っていなかった）
                           + " -c " + Quote("project_doc_max_bytes=262144")
                           + " @args }";
                if (!tab.AgentStarted)
                {
                    tab.AgentStarted = true;
                    tab.AgentStartedAt = DateTime.UtcNow;
                    EnsureAiHookWatch();
                    command += tab.ResumeSession != null ? ";codex resume " + tab.ResumeSession : ";codex";   // 番号は Guid の形のものだけ（AppSettings）
                }
            }
            return powershell + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        /// <summary>AI のフォルダを作り、台本・askpass・SSH の鍵を写す。中身が同じなら書かない。</summary>
        private void PrepareAiFolder()
        {
            Directory.CreateDirectory(AiFolder);
            if (!_aiStaleFilesCleaned)
            {
                _aiStaleFilesCleaned = true;
                DeleteStaleAiFiles();
            }

            foreach (var script in AiScripts)
            {
                var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ai", script);
                if (File.Exists(source)) WriteIfChanged(Path.Combine(AiFolder, script), File.ReadAllText(source, Encoding.UTF8));
            }

            // 指示は会話を始めるフォルダの AGENTS.md に書く（Codex が自分で読む。MainWindow.AiInstructions.cs）
            var work = AiWorkFolder;
            Directory.CreateDirectory(work);
            WriteIfChanged(Path.Combine(work, "AGENTS.md"), AiInstructionsText(work) + ExcelNotesSection());   // 利用者が書いた「Excel で AI にいつも伝えること」（MainWindow.Excel.cs）

            // フック（MainWindow.AiHooks.cs）。プロジェクトの単位のフック（work\.codex\hooks.json）にする（利用者の ~/.codex には書かない）。
            // 中身が変わると、Codex が利用者に信頼するかを尋ねる（信頼するまでは動かない）
            Directory.CreateDirectory(Path.Combine(work, ".codex"));
            WriteIfChanged(Path.Combine(work, ".codex", "hooks.json"), AiHooksConfig());

            // AiLink の台本（state.ps1・excel.ps1・talk.ps1）は、Codex に毎回は確かめさせない（2026-11-25。excel.ps1 のたびに
            // 「囲いの外で動かしてよいか」と聞かれた）。プロジェクトの単位のルール（work\.codex\rules）で、この形のコマンドだけを許す。
            // 許したコマンドは囲いの外で動く。台本はパイプで AiLink に頼むだけで、Excel に触るのは AiLink。ただし excel.ps1 -Script は AI が書いた PowerShell を
            // 利用者の権限で動かすので、AiLink が動かす前に書き方を構文解析で調べ、決まった形（Excel の COM など）だけを動かす（Ai/excel-guard.ps1。2026-11-26）
            Directory.CreateDirectory(Path.Combine(work, ".codex", "rules"));
            WriteIfChanged(Path.Combine(work, ".codex", "rules", "ailink.rules"), AiRules());

            // .codex には AiLink が置いたもの（hooks.json・rules\ailink.rules）だけを残す。ほかのルール・設定（config.toml など）が置かれていたら消す
            // （AI が「何でも許す」ルールや囲いの設定を置いて、次の起動から確かめなしに動けないように。2026-11-25 のレビュー。Codex は作業のフォルダの .codex を
            // AI から書けない場所にしているはずだが、念のため）
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(work, ".codex", "hooks.json"),
                Path.Combine(work, ".codex", "rules", "ailink.rules"),
            };
            // 消すのは、Codex の設定・ルール・フックになりうるもの（*.toml・*.rules・*.json）だけ（Codex がほかのものを置く版でも、それは残す）
            foreach (var file in Directory.GetFiles(Path.Combine(work, ".codex"), "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (keep.Contains(file) || !(extension.Equals(".toml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rules", StringComparison.OrdinalIgnoreCase)
                                             || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Codex のルール（Starlark の prefix_rule）。指示に書いた形「powershell -NoProfile -ExecutionPolicy Bypass -File "台本"」で AiLink の台本を動かすときは、確かめずに動かす。
        /// ほかのコマンドはふだんどおり（Codex が確かめる）。
        /// </summary>
        private static string AiRules()
        {
            var scripts = new[] { "state.ps1", "excel.ps1", "talk.ps1" }
                .Select(name => "\"" + Path.Combine(AiFolder, name).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
            return "# AiLink が書くルール（会話を始めるたびに書き直す）。AiLink の台本だけを、確かめずに動かす。\n"
                   + "prefix_rule(\n"
                   + "    pattern = [[\"powershell\", \"powershell.exe\"], \"-NoProfile\", \"-ExecutionPolicy\", \"Bypass\", \"-File\", [" + string.Join(", ", scripts) + "]],\n"
                   + "    decision = \"allow\",\n"
                   + "    justification = \"AiLink のパイプに頼むだけの台本（Excel に触るのは AiLink）\",\n"
                   + ")\n";
        }

        /// <summary>
        /// タブの名前をファイルの名前に使う形（#1 → tab1）。AI が書くファイル（excel-write-tab1.tsv など）をタブごとに分ける
        /// （タブが2つ以上あると、同じ名前のファイルを取り合い、別の会話の中身を書いてしまうため。2026-11-25 のレビュー）。
        /// 頭に tab を付けるのは、起動のオプション -c で渡すとき数として読まれないように。
        /// </summary>
        private static string AiTabFileName(AiTab tab)
        {
            // 数字だけを使う（ailink.json から戻した名前に \ や .. が入っていても、作業のフォルダの外を指さないように）
            return "tab" + new string((tab.Name ?? string.Empty).Where(char.IsDigit).ToArray());
        }

        /// <summary>フックが動いているかを見張る（5 秒ごと）。</summary>
        private DispatcherTimer _aiHookWatch;

        private void EnsureAiHookWatch()
        {
            if (_aiHookWatch != null) return;
            _aiHookWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _aiHookWatch.Tick += delegate
            {
                foreach (var tab in _aiTabs)
                {
                    // 話しかけて 20 秒たってもフックが一度も来ない: 信頼していない（Hooks need review で 3 を選んだ・Esc で飛ばした）。
                    // 起動からは数えない（2026-11-26。Codex は最初に話しかけるまでフックを動かさないので、起動して 20 秒で出すと、動いているのに出た）
                    if (!tab.AgentStarted || tab.HooksSeen || tab.HookWarned || tab.PromptedAt == default(DateTime)
                        || DateTime.UtcNow - tab.PromptedAt < TimeSpan.FromSeconds(20)) continue;
                    tab.HookWarned = true;
                    tab.ContextLine = "⚠ " + tab.Name + " は話しかけてもフックが動きません。AI に渡さない場所の見張りと、今の状態の受け渡しが効きません。"
                                      + "このタブで /exit と打ってから codex と打ち、「Hooks need review」で 2 を選んでから、もう一度話しかけてください";
                    if (ReferenceEquals(tab, _aiTab)) AiContextText.Text = tab.ContextLine;
                    tab.Header.ToolTip = AiTabToolTip(tab);
                }
            };
            _aiHookWatch.Start();
        }

        /// <summary>PowerShell の文字列（'...'）にする。中の ' は2つ重ねる。</summary>
        private static string Quote(string text)
        {
            return "'" + text.Replace("'", "''") + "'";
        }

        // =====================================================================
        // パイプ（台本からの頼み）
        // =====================================================================

        /// <summary>
        /// 台本からの頼みを受ける。待つのは別のスレッドで、つながったら、そのつながりは別のスレッドに任せて、すぐ次の口を開く
        /// （ある頼みに応えている間も、ほかのタブの頼みを落とさないため）。
        /// </summary>
        private void StartAiListener()
        {
            if (_aiListener != null) return;

            _aiListener = new Thread(delegate ()
            {
                while (!_aiStopping)
                {
                    NamedPipeServerStream server = null;
                    try
                    {
                        server = new NamedPipeServerStream(AiPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, AiPipeSecurity());
                        _aiWaiting = server;
                        if (_aiStopping) throw new ObjectDisposedException(AiPipeName);
                        server.WaitForConnection();
                        _aiWaiting = null;
                    }
                    catch (Exception)
                    {
                        // つないですぐ切られた等。少し置いて待ち直す。閉じたのなら終わる
                        _aiWaiting = null;
                        if (server != null) server.Dispose();
                        if (_aiStopping) break;
                        Thread.Sleep(100);
                        continue;
                    }

                    var connection = server;
                    var worker = new Thread(delegate () { ServeAiRequest(connection); });
                    worker.IsBackground = true;   // 終了を止めない
                    worker.Start();
                }
            });
            _aiListener.IsBackground = true;
            _aiListener.Start();
        }

        /// <summary>パイプは本人だけがつなげる（ネットワーク越しは断る）。</summary>
        private static PipeSecurity AiPipeSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }

        /// <summary>つながった1つの頼みに応える（そのつながり用のスレッドで）。答えは1行。</summary>
        private void ServeAiRequest(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                {
                    var reader = new StreamReader(server, new UTF8Encoding(false), false, 256, true);
                    var request = reader.ReadLine() ?? string.Empty;
                    string answer;

                    if (request.StartsWith("hook\t", StringComparison.Ordinal))
                    {
                        answer = ServeAiHook(request);
                    }
                    else if (request.StartsWith("talk\t", StringComparison.Ordinal))
                    {
                        answer = ServeAiTalk(request);
                    }
                    else if (request.StartsWith("state\t", StringComparison.Ordinal))
                    {
                        answer = ServeAiState(request.Substring("state\t".Length));
                    }
                    else if (request.StartsWith("excel-read\t", StringComparison.Ordinal))
                    {
                        answer = ServeExcelRead(request);
                    }
                    else if (request.StartsWith("excel-write\t", StringComparison.Ordinal))
                    {
                        answer = ServeExcelWrite(request);
                    }
                    else if (request.StartsWith("excel-overview\t", StringComparison.Ordinal))
                    {
                        answer = ServeExcelOverview(request);
                    }
                    else if (request.StartsWith("excel-script\t", StringComparison.Ordinal))
                    {
                        answer = ServeExcelScript(request);
                    }
                    else
                    {
                        answer = "error\t知らない頼み。";
                    }

                    var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                    writer.WriteLine(answer.Replace("\r", " ").Replace("\n", " "));
                    writer.Flush();
                    server.WaitForPipeDrain();
                }
            }
            catch (Exception)
            {
                // つないですぐ切られた・閉じている途中等
            }
        }

        // =====================================================================
        // 閉じる・片付ける
        // =====================================================================

        /// <summary>窓を閉じるとき。中の PowerShell を終わらせ、WebView2 も片付け、書き出したファイルを消す。</summary>
        private void StopAi()
        {
            _aiStopping = true;

            var waiting = _aiWaiting;
            if (waiting != null)
            {
                try
                {
                    waiting.Dispose();
                }
                catch (Exception)
                {
                }
            }

            foreach (var tab in _aiTabs) tab.View.Dispose();

            DeleteExcelSheets();
        }

        /// <summary>
        /// 前に落ちた AiLink が残したファイル（sheets-番号-中身の印.txt）を消す。番号のプロセスがもう動いていないか、
        /// 別のプログラムに番号が使い回されていれば消す。できる範囲で。
        /// </summary>
        private static void DeleteStaleAiFiles()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                // 頼みごとの名前で書いた Excel の結果は、半日より古いものを消す（どの起動のものかは名前から分からないので、古さで見る）
                foreach (var pattern in new[] { "excel-read-*.tsv", "excel-overview-*.txt", "excel-overview-*.png", "excel-script-*.ps1" })
                {
                    foreach (var old in Directory.GetFiles(AiFolder, pattern))
                    {
                        try
                        {
                            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromHours(12)) File.Delete(old);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                // 会話の作業フォルダの、excel.ps1 が送る途中で落ちて残したファイル（excel-write-tab1-sending-….tsv など）も、半日より古ければ消す
                if (Directory.Exists(AiWorkFolder))
                {
                    foreach (var old in Directory.GetFiles(AiWorkFolder, "excel-*-sending-*"))
                    {
                        try
                        {
                            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromHours(12)) File.Delete(old);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                foreach (var path in Directory.GetFiles(AiFolder, "sheets-*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    int id;
                    // 名前は sheets-番号-中身の印。番号で見る
                    var number = name.Substring(name.IndexOf('-') + 1);
                    if (number.IndexOf('-') >= 0) number = number.Substring(0, number.IndexOf('-'));
                    if (!int.TryParse(number, out id) || id == self.Id) continue;
                    if (IsAiLinkRunning(id, self.ProcessName)) continue;
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
                // 片付けられなくても起動は止めない
            }
        }

        private static bool IsAiLinkRunning(int id, string name)
        {
            try
            {
                using (var process = Process.GetProcessById(id))
                {
                    return string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>中身が同じなら書かない。書くときは一時ファイルから置き換える（ほかのタブが起動する瞬間に、途中のファイルを読ませない）。</summary>
        private static void WriteIfChanged(string path, string text)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return;
            }
            catch (Exception)
            {
                // 読めなければ書く
            }

            var temp = path + "." + AiProcessId + ".saving";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                }
                if (!File.Exists(path)) throw;
            }
        }
    }
}
