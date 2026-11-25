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
    // タブを開くと PowerShell の中で AI（Codex）を起動する。指示・フック・会話の続きを渡すのは、これから第3段で足す（PORTING.md）。
    // 中の PowerShell には環境変数でタブの番号（AILINK_TAB）とパイプの名前（AILINK_PIPE）を渡す。台本（excel.ps1・terminal.ps1）はこのパイプで頼んでくる。
    public partial class MainWindow
    {
        /// <summary>AI に渡す台本・書き出したファイルを置くところ。Dropbox などの同期の外。</summary>
        private static readonly string AiFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiLink", "ai");

        /// <summary>このプロセスの番号。窓は1つだけなので、ファイルとパイプの名前はこれで分ける。</summary>
        private static readonly string AiProcessId = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

        /// <summary>台本が頼んでくるパイプの名前。</summary>
        private static readonly string AiPipeName = "AiLink-" + AiProcessId;

        /// <summary>AI の欄から SSH でつなぐときの鍵・設定を置くフォルダ。exe の隣の ssh（Tsuzuri と同じ）。</summary>
        private static readonly string AiSshFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssh");

        /// <summary>
        /// ssh フォルダを写す先。exe が Mac の共有フォルダなどにあると Windows の権限を付けられず、Windows の ssh が鍵を使ってくれないため、
        /// 会話を始めるたびにここへ写し、本人だけが触れる権限にする。known_hosts はここで育てる（元から写さない・消さない）。
        /// </summary>
        private static readonly string AiSshLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiLink", "ssh");

        /// <summary>パスワードでつなぐ接続先のとき、ssh にパスワードを返す askpass。中身は隣の ssh-askpass.ps1（AILINK_SSH_HOST の名称の行の passwd を返す）。</summary>
        private const string AiSshAskpass =
            "@powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0ssh-askpass.ps1\"\r\n";

        /// <summary>exe の隣の Ai から AI のフォルダへ写す台本。</summary>
        private static readonly string[] AiScripts = { "ssh-askpass.ps1", "ssh-session.ps1", "terminal.ps1", "excel.ps1", "state.ps1", "talk.ps1", "hook-probe.ps1" };

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

            /// <summary>このタブで AI を一度起動したか（起動するのは最初の1回だけ）。</summary>
            public bool AgentStarted;

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
                // 第3段で、AI がその会話を残しているかを IAgent で確かめてから戻す
                foreach (var state in saved) AddAiTab(state);
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

        /// <summary>利用者のフォルダ。共通ターミナルもここで始める。</summary>
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

        /// <summary>タブの段を並べ直す（タブ、その右に「＋」と共通ターミナルのボタン）。</summary>
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
            AiTabs.Children.Add(AiShellButton());
        }

        /// <summary>タブを選ぶ。そのタブの端末を出し、ほかは隠す。</summary>
        private void SelectAiTab(AiTab tab)
        {
            _aiTab = tab;
            ShowAiInbox();
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

            var command = "$env:AILINK_TAB=" + Quote(tab.Id) + ";$env:AILINK_PIPE=" + Quote(AiPipeName) + ";$env:AILINK_WORK=" + Quote(AiWorkFolder);

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
                           + " @args }";
                if (!tab.AgentStarted)
                {
                    tab.AgentStarted = true;
                    command += ";codex";
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

            WriteIfChanged(Path.Combine(AiFolder, "ssh-askpass.cmd"), AiSshAskpass);
            foreach (var script in AiScripts)
            {
                var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ai", script);
                if (File.Exists(source)) WriteIfChanged(Path.Combine(AiFolder, script), File.ReadAllText(source, Encoding.UTF8));
            }
            CopySshFolder();

            // 指示は会話を始めるフォルダの AGENTS.md に書く（Codex が自分で読む。MainWindow.AiInstructions.cs）
            var work = AiWorkFolder;
            Directory.CreateDirectory(work);
            WriteIfChanged(Path.Combine(work, "AGENTS.md"), AiInstructionsText(work));

            // 試しのフック（2026-11-25。Codex のフックが Windows でどう動くかを確かめるため。確かめたら本物に置き換える）。
            // プロジェクトの単位のフック（work\.codex\hooks.json）にする（利用者の ~/.codex には書かない）。利用者が Codex の /hooks で信頼するまでは動かない
            Directory.CreateDirectory(Path.Combine(work, ".codex"));
            WriteIfChanged(Path.Combine(work, ".codex", "hooks.json"), HookProbeConfig());

            // AiLink の台本（state.ps1・excel.ps1・terminal.ps1）は、Codex に毎回は確かめさせない（2026-11-25。excel.ps1 のたびに
            // 「囲いの外で動かしてよいか」と聞かれた）。プロジェクトの単位のルール（work\.codex\rules）で、この形のコマンドだけを許す。
            // 許したコマンドは囲いの外で動くが、台本はパイプで AiLink に頼むだけで、Excel に触るのは AiLink（台本の中の禁止の語も AiLink が断る）
            Directory.CreateDirectory(Path.Combine(work, ".codex", "rules"));
            WriteIfChanged(Path.Combine(work, ".codex", "rules", "ailink.rules"), AiRules());
        }

        /// <summary>
        /// Codex のルール（Starlark の prefix_rule）。指示に書いた形「powershell -NoProfile -ExecutionPolicy Bypass -File "台本"」で AiLink の台本を動かすときは、確かめずに動かす。
        /// ほかのコマンドはふだんどおり（Codex が確かめる）。
        /// </summary>
        private static string AiRules()
        {
            var scripts = new[] { "state.ps1", "excel.ps1", "terminal.ps1", "talk.ps1" }
                .Select(name => "\"" + Path.Combine(AiFolder, name).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
            return "# AiLink が書くルール（会話を始めるたびに書き直す）。AiLink の台本だけを、確かめずに動かす。\n"
                   + "prefix_rule(\n"
                   + "    pattern = [[\"powershell\", \"powershell.exe\"], \"-NoProfile\", \"-ExecutionPolicy\", \"Bypass\", \"-File\", [" + string.Join(", ", scripts) + "]],\n"
                   + "    decision = \"allow\",\n"
                   + "    justification = \"AiLink のパイプに頼むだけの台本（Excel に触るのは AiLink）\",\n"
                   + ")\n";
        }

        /// <summary>試しのフックの設定（hooks.json）。どのイベントでも hook-probe.ps1 を動かす。道具のイベントは全部の道具に当てる。</summary>
        private static string HookProbeConfig()
        {
            var probe = Path.Combine(AiFolder, "hook-probe.ps1");
            Func<string, string, string> entry = (name, matcher) =>
            {
                // exe はパスを書かず powershell（Codex がフックのコマンドを PowerShell で動かすと、"…\powershell.exe" -NoProfile は
                // 文字列のあとに引数が続く文法の誤りになり、code 1 で終わった。2026-11-25 に Windows で試して分かった。Tsuzuri のフックと同じ形）
                var command = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + probe + "\" " + name;
                var escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return "\"" + name + "\": [{" + (matcher != null ? "\"matcher\": \"" + matcher + "\", " : string.Empty)
                       + "\"hooks\": [{\"type\": \"command\", \"command\": \"" + escaped + "\", \"timeout\": " + (name == "SessionEnd" ? "3" : "30") + "}]}]";
            };
            return "{\n  \"description\": \"AiLink hook probe (temporary)\",\n  \"hooks\": {\n    "
                   + string.Join(",\n    ", new[]
                   {
                       entry("SessionStart", null),
                       entry("UserPromptSubmit", null),
                       entry("PreToolUse", "*"),
                       entry("PostToolUse", "*"),
                       entry("Stop", null),
                       entry("SessionEnd", null),
                   })
                   + "\n  }\n}\n";
        }

        /// <summary>PowerShell の文字列（'...'）にする。中の ' は2つ重ねる。</summary>
        private static string Quote(string text)
        {
            return "'" + text.Replace("'", "''") + "'";
        }

        /// <summary>中に ``` があっても囲みが切れないように、それより長い ``` で囲む。</summary>
        private static string Fenced(string content)
        {
            var longest = 0;
            var run = 0;
            foreach (var c in content)
            {
                run = c == '`' ? run + 1 : 0;
                if (run > longest) longest = run;
            }
            var fence = new string('`', Math.Max(3, longest + 1));
            return fence + "\n" + content + "\n" + fence + "\n";
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

                    if (request.StartsWith("talk\t", StringComparison.Ordinal))
                    {
                        answer = ServeAiTalk(request);
                    }
                    else if (request.StartsWith("state\t", StringComparison.Ordinal))
                    {
                        answer = ServeAiState(request.Substring("state\t".Length));
                    }
                    else if (request == "shell-open")
                    {
                        answer = OpenAiShellForAi();
                    }
                    else if (request == "shell-dump")
                    {
                        answer = DumpAiShell();
                    }
                    else if (request.StartsWith("shell-type\t", StringComparison.Ordinal))
                    {
                        string text = null;
                        try
                        {
                            text = Encoding.UTF8.GetString(Convert.FromBase64String(request.Substring("shell-type\t".Length)));
                        }
                        catch (FormatException)
                        {
                        }
                        var screen = ReadAiShellScreen(1, 2000);
                        answer = text == null
                            ? "error\t置く文字を読めなかった。"
                            : Dispatcher.Invoke(new Func<string>(() => TypeIntoAiShell(text, screen != null && screen.Bracketed)));
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
            CloseAiShell();

            DeleteExcelSheets();
            foreach (var path in new[] { AiShellDumpPath })
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // 消せなくても閉じるのは止めない。次に起動したとき DeleteStaleAiFiles が片付ける
                }
            }
        }

        /// <summary>
        /// 前に落ちた AiLink が残したファイル（terminal-番号.txt・sheets-番号.txt）を消す。番号のプロセスがもう動いていないか、
        /// 別のプログラムに番号が使い回されていれば消す。できる範囲で。
        /// </summary>
        private static void DeleteStaleAiFiles()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                foreach (var path in Directory.GetFiles(AiFolder, "terminal-*.txt").Concat(Directory.GetFiles(AiFolder, "sheets-*.txt")))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    int id;
                    // 名前は terminal-番号・sheets-番号-中身の印。番号で見る
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

        /// <summary>
        /// exe の隣の ssh フォルダを AiSshLocal へ写し、本人だけが触れる権限にする。
        /// 元で消したファイルは写した先からも消す（known_hosts は除く）。失敗しても起動は止めない。
        /// </summary>
        private void CopySshFolder()
        {
            try
            {
                if (!Directory.Exists(AiSshFolder)) return;

                Directory.CreateDirectory(AiSshLocal);
                var folderSecurity = new DirectorySecurity();
                folderSecurity.SetAccessRuleProtection(true, false);
                folderSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(AiSshLocal, folderSecurity);

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in Directory.GetFiles(AiSshFolder))
                {
                    var name = Path.GetFileName(source);
                    if (string.Equals(name, "known_hosts", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.StartsWith("._", StringComparison.Ordinal)) continue;

                    names.Add(name);
                    var target = Path.Combine(AiSshLocal, name);
                    var bytes = File.ReadAllBytes(source);
                    // config.txt は UTF-8 にして写す（メモ帳などで Shift_JIS で保存されていると、日本語の名称が一致しないため）
                    if (string.Equals(name, "config.txt", StringComparison.OrdinalIgnoreCase)) bytes = ToUtf8(bytes);
                    File.WriteAllBytes(target, bytes);

                    var fileSecurity = new FileSecurity();
                    fileSecurity.SetAccessRuleProtection(true, false);
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                    File.SetAccessControl(target, fileSecurity);
                }

                foreach (var target in Directory.GetFiles(AiSshLocal))
                {
                    var name = Path.GetFileName(target);
                    if (string.Equals(name, "known_hosts", StringComparison.OrdinalIgnoreCase) || names.Contains(name)) continue;
                    File.Delete(target);
                }
            }
            catch (Exception ex)
            {
                Status("SSH の鍵を写せませんでした: " + ex.Message);
            }
        }

        /// <summary>UTF-8 として読めればそのまま。読めなければ ANSI（日本語の Windows なら Shift_JIS）として読み、BOM 無しの UTF-8 にする。</summary>
        private static byte[] ToUtf8(byte[] bytes)
        {
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                return bytes;
            }
            catch (DecoderFallbackException)
            {
                return new UTF8Encoding(false).GetBytes(Encoding.Default.GetString(bytes));
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
