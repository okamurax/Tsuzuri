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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: AI（Claude Code）と並べて使う（SPEC 19章。2026-10-24）。
    //
    // 段の右端の「AI」（ランチャーの隣）で、本文の右に AI の欄（Views/TerminalView）を出し入れする。
    // 欄の中は端末（WebView2 の中の xterm.js と ConPTY）で、Claude Code の画面がそのまま動く。
    // 見張り続けることはせず、次のきっかけだけで動く。
    // ・話しかけたとき: Claude Code のフックが名前付きパイプで頼んでくる。保存して今の状態を書き出し、フックがそれを Claude に渡す
    // ・Claude がファイルを書き換えた直後: フックがパイプで知らせてくる。外で変わった文書を読み直す
    // ・エディタに戻ったとき（ほかのアプリから）: 念のため、外で変わった文書を読み直す（AI 以外の書き換えも拾う）
    // 欄の中の端末に移っても、ウィンドウとしてはエディタから離れない（Deactivated が来ない）ので、保存と状態の書き出しは話しかけたときに頼まれてやる。
    public partial class MainWindow
    {
        /// <summary>Claude Code に渡す設定・指示・今の状態を置くところ。Dropbox の外（同期を走らせない）。</summary>
        private static readonly string AiFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tsuzuri", "ai");

        /// <summary>この画面だけのパイプの名前。画面が2つあっても、それぞれのフックがそれぞれへ知らせる（2026-10-31。前はアプリに1つ）。</summary>
        private string AiPipeName
        {
            get { return "Tsuzuri-" + WindowKey; }
        }

        /// <summary>今の状態を書き出すファイル。claude を一度でも起動するまでは null（書き出さない）。</summary>
        private string _aiContextPath;

        /// <summary>Claude Code に渡す設定（フック）のファイル。エディタを閉じるときに消す（2026-10-27）。書くまでは null。</summary>
        private string _aiSettingsPath;

        /// <summary>前に落ちたエディタが残した設定・状態のファイルを片付けたか（起動して最初に claude を用意するとき1回だけ）。</summary>
        private bool _aiStaleFilesCleaned;

        /// <summary>
        /// 話しかけられた頼み（保存と状態の書き出し）に応えている最中か（2026-10-27）。保存の確かめのダイアログの最中にも
        /// 次の頼み（ほかのタブから）が届くので、入れ子で保存しないようにする。画面のスレッドだけで触る。
        /// </summary>
        private bool _aiPromptHandling;

        private Thread _aiListener;

        /// <summary>
        /// AI の欄の会話（端末）のタブ1つ（2026-10-26）。タブごとに別の PowerShell（と Claude Code）が動く。
        /// 裏のタブも動き続ける。今の状態を渡す仕組み（フック・パイプ）は、どのタブの claude にも同じに効く。
        /// </summary>
        private sealed class AiTab
        {
            public TerminalView View;
            public Border Header;
            public TextBlock Title;

            /// <summary>タブを見分ける番号。中の PowerShell に環境変数で渡し、claude のフックがどのタブの会話かを知らせてくる（2026-10-27）。</summary>
            public readonly string Id = Guid.NewGuid().ToString("N");

            /// <summary>中で動いている claude の会話の番号と、そのフォルダ。claude を起動していなければ null（次の起動で戻さない）。</summary>
            public string SessionId;
            public string Folder;

            /// <summary>
            /// claude のセッションの名前（#1・#2…。2026-11-01）。claude --name で付け、タブの見出しに出す。
            /// ほかの会話に「#2 にこの会話の要約を渡して」のように頼むときの宛先になる（Claude Code のセッション間のメッセージ）。
            /// # を付けるのは、話の中の数（「2 つ」「2 章」）と取り違えないため。
            /// アプリの中で（全部の画面を通して）重ねない。
            /// </summary>
            public string Name;
        }

        /// <summary>エディタを閉じている最中か。閉じるときに claude が終わっても、開いていたタブの覚えを消さないようにする。</summary>
        private bool _aiStopping;

        /// <summary>AI の欄の会話のタブ。左から。隠しても claude は動いたまま。</summary>
        private readonly List<AiTab> _aiTabs = new List<AiTab>();

        /// <summary>選んでいるタブ（2段のときは上の段のタブ）。無ければ null。</summary>
        private AiTab _aiTab;

        /// <summary>タブの段の「＋」。初めて欄を出すときに作る。</summary>
        private Button _aiAddButton;

        /// <summary>
        /// Claude Code に最初に渡す指示。ファイルの形式は Claude には分からないので、ここで教える。
        /// </summary>
        private const string AiInstructions =
@"あなたは Tsuzuri（Windows の Markdown エディタ）の中の AI の欄で動いている。利用者はエディタで文書を開いたまま、ここで話しかける。

- 利用者が話しかけるたびに、エディタの今の状態（開いているファイル・見ているページ・カーソルの行・選んでいる文字）が付いてくる。
  「ここ」「この節」「選んだところ」「このページ」は、それを指す。
- ファイルは書き換えてよい。エディタは書き換えを知らされて、すぐ読み直す。
  書き換える前には必ずファイルを読み直すこと。利用者がエディタで編集・保存していることがある。
- ファイルの形式:
  - 1つの .md の中に複数のページがある。`<!--page: p3-->` の行の次から、次の印の行の手前までがページ p3。
    ファイルの先頭から最初の印の手前まではルートページ。
  - サブページへのリンクは `[表示名](#p3)`。
  - サブページを作るときは、別のファイルにしない。ファイルの末尾に `<!--page: ID-->` の行と中身を足し、元の場所にリンクを書く。
    ID は、ファイルの中の印とリンクのどれとも重ならない p+数字にする。
  - 印の行は書き換えない。ページを消すときは、印の行とその中身をまとめて消し、そのページへのリンクも消す。
  - 添付（画像など）は、ファイルの隣の「.文書名」フォルダにある。
- 文書の変更の履歴がある。利用者がエディタで保存するたびに git で記録している（.文書名/git。記録しているのは文書の .md だけ）。
  どこをいつ進めたか・前回の保存から何が変わったかを知りたいときに読む
  （例: git -c safe.directory=* --git-dir=""<履歴>"" log --stat -5 、git -c safe.directory=* --git-dir=""<履歴>"" log -p -3）。
  ネットワーク上の場所（\\Mac\...）では、-c safe.directory=* を付けないと git に断られる。
  <履歴> の場所は、話しかけるたびに付いてくるエディタの今の状態に書いてある。前回進めたところを踏まえて助言するのに使う。
  この履歴にコミットしたり、書き換えたりしない（記録はエディタがする）。
- AI の欄には、ほかの会話のタブもある。タブの見出しは、それぞれの claude のセッションの名前（#1・#2…）。
  利用者が「#2 に〜を渡して」「#1 に頼んで」と言ったら、その名前のセッションに SendMessage で送る（相手は ListAgents で探せる）。
- 返事は日本語で、欄は狭いので短めに書く。";

        /// <summary>ツールバーの右端の「AI」。AI の欄を出し入れする。タブが1つも無ければ、claude を起動したタブを1つ開く。</summary>
        private void ToggleAi()
        {
            if (AiPane.Visibility == Visibility.Visible)
            {
                HideAiPane();
                return;
            }

            if (_aiTabs.Count == 0 && !_aiRestorePending) AddAiTab(string.Empty);   // claude を直接起動する（2026-10-31。前は PowerShell だけ）
            ShowAiPane();
        }

        /// <summary>起動したあと、前の会話のタブをまだ戻していないか（窓が出て落ち着いてから戻す。MainWindow のコンストラクタ）。</summary>
        private bool _aiRestorePending;

        /// <summary>
        /// 会話のタブを足して選ぶ。"" なら新しい会話（claude をそのまま起動する。最初の1回だけ。終わったあとの端末は PowerShell）。
        /// null なら PowerShell だけ（今は使わない。2026-10-31 に「＋」のメニューをやめ、claude を直接起動するようにした）。
        /// </summary>
        private void AddAiTab(string start)
        {
            AddAiTab(start, null);
        }

        // ランチャー・ファイラーのスクリプト（.bat・.cmd・.ps1）をこの欄のタブで動かすのは、やめた（2026-10-30。2026-10-27 に入れたが、
        // 使ってみてほぼ意味がなかった）。前のとおり別の窓で動かす（MainWindow.Links.cs の Launch）。

        /// <param name="restore">null でなければ、前の起動で開いていた会話。そのフォルダで、その会話の続きとして claude を起動する。</param>
        private void AddAiTab(string start, AiTabState restore)
        {
            var tab = new AiTab();
            var first = true;

            if (restore != null)
            {
                tab.SessionId = restore.Session;
                tab.Folder = restore.Folder;
                var name = restore.Name != null && !restore.Name.StartsWith("#", StringComparison.Ordinal) ? "#" + restore.Name : restore.Name;   // 前の「1」は「#1」に
                if (!AiTabNameUsed(name)) tab.Name = name;
                start = "--resume " + restore.Session;   // 番号は Guid の形のものだけ（AppSettings）
            }
            if (tab.Name == null) tab.Name = NewAiTabName();

            // ファイルを開いていなければ、利用者のフォルダで起動する。
            // WordPress の記事なら、記事ごとの一時フォルダではなく、その上の決まったフォルダで起動する（2026-10-31）。
            // claude は初めてのフォルダで「信頼するか」を尋ねるので、記事を開くたびに尋ねられていた。記事のファイルはその下にあるので書き換えられる。
            tab.View = new TerminalView(
                () =>
                {
                    var command = AiCommandLine(first ? start : null, tab.Id, tab.Name);
                    first = false;
                    return command;
                },
                () => restore != null && !string.IsNullOrEmpty(restore.Folder) && Directory.Exists(restore.Folder)
                    ? restore.Folder
                    : HasDocument
                        ? (WordPressLinkOf(Active) != null ? WordPressTempRoot : Path.GetDirectoryName(Active.FilePath))
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            tab.View.Visibility = Visibility.Collapsed;
            AiTerminals.Children.Add(tab.View);

            // 見出しはセッションの名前（2026-11-01。前は端末のタイトル。端末のタイトルはツールチップに出す）。
            tab.Title = new TextBlock { Text = tab.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            tab.Header = CreateAiTabHeader(tab);
            tab.View.TitleChanged += title => SetAiTabTitle(tab, title);
            _aiTabs.Add(tab);
            RebuildAiTabStrip();
            SelectAiTab(tab);
        }

        /// <summary>タブの見出し。押すと選ぶ。右の × で閉じる（その会話の PowerShell と claude も終わる）。</summary>
        private Border CreateAiTabHeader(AiTab tab)
        {
            // × と ＋ は、検索欄の ↑↓ と同じアイコンのフォントの記号にする（本文のフォントの × は小さく見えた）。
            var close = new Button
            {
                Content = "\uE711",
                ToolTip = "この会話を閉じる（中の PowerShell と Claude Code も終わる）",
                FontFamily = IconFont,
                FontSize = 9,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
            if (style != null) close.Style = style;
            close.Click += delegate { CloseAiTab(tab); };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(tab.Title);
            panel.Children.Add(close);

            // 高さは検索欄の段のボタン（「AI」「ランチャー」。26px）にそろえる。
            var header = new Border
            {
                Child = panel,
                Height = 26,
                MaxWidth = 220,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 0, 3, 0),
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

        /// <summary>アイコンのフォント（ファイラー・検索欄の ↑↓ と同じ）。</summary>
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>
        /// 端末のタイトルが変わった。タブのツールチップに、セッションの名前と並べて出す。
        /// </summary>
        private static void SetAiTabTitle(AiTab tab, string title)
        {
            // 見出しはセッションの名前のまま。端末のタイトル（Claude Code が会話の中身に合わせて付ける）はツールチップに出す（2026-11-01）。
            var text = (title ?? string.Empty).Trim();
            tab.Header.ToolTip = "セッションの名前: " + tab.Name + (text.Length > 0 ? Environment.NewLine + text : string.Empty);
        }

        /// <summary>アプリの中の（全部の画面の）AI の欄のタブで、その名前を使っているか。</summary>
        private static bool AiTabNameUsed(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return OpenWindows.Any(window => window._aiTabs.Any(tab => tab.Name == name));
        }

        /// <summary>新しいタブの名前。使っていないいちばん小さい番号（#1・#2…）。閉じたタブの番号は、次のタブが使う。</summary>
        private static string NewAiTabName()
        {
            var number = 1;
            while (AiTabNameUsed("#" + number.ToString(CultureInfo.InvariantCulture))) number++;
            return "#" + number.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>タブの段を並べ直す（タブ、その右に「＋」）。</summary>
        private void RebuildAiTabStrip()
        {
            if (_aiAddButton == null)
            {
                _aiAddButton = new Button
                {
                    Content = "\uE710",
                    ToolTip = "新しい会話を足す（claude を起動する）",
                    FontFamily = IconFont,
                    FontSize = 11,
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
                if (style != null) _aiAddButton.Style = style;
                _aiAddButton.Click += delegate { AddAiTab(string.Empty); };   // メニューは出さず、claude を直接起動する（2026-10-31）
            }

            AiTabs.Children.Clear();
            foreach (var tab in _aiTabs) AiTabs.Children.Add(tab.Header);
            AiTabs.Children.Add(_aiAddButton);
        }

        /// <summary>タブを選ぶ。そのタブの端末を出し、ほかは隠す（隠しても動き続ける）。</summary>
        private void SelectAiTab(AiTab tab)
        {
            _aiTab = tab;
            ShowAiTabs();
            if (AiPane.Visibility == Visibility.Visible) FocusAiTerminal(tab);
        }

        /// <summary>
        /// 端末にキーボードを渡す前に、いったん受け皿（見えない文字の欄）に移すためのもの（2026-10-28）。
        /// </summary>
        private TextBox _aiFocusSink;

        /// <summary>
        /// タブの端末にキーボードを渡す（欄を出したとき・タブを選んだとき）。
        /// いったん受け皿の文字の欄に移してから渡す（2026-10-28）。端末（WebView2）にじかに移すと、日本語の変換が窓の左上に出ることがあった
        /// （タブを切り替えたとき・起動したときに戻したタブ）。本文などほかの文字の欄を一度経てから戻ると、正しい所に出たので、それをまねる。
        /// </summary>
        private void FocusAiTerminal(AiTab tab)
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

                // 移している一瞬に打たれた文字は、端末に渡せないので捨てる（受け皿に溜めない）。
                _aiFocusSink.LostKeyboardFocus += delegate { _aiFocusSink.Clear(); };
            }

            Keyboard.Focus(_aiFocusSink);
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ReferenceEquals(_aiTab, tab) && AiPane.Visibility == Visibility.Visible) tab.View.FocusTerminal();
            }), DispatcherPriority.Background);
        }

        /// <summary>タブを閉じる。中の PowerShell と claude も終わらせる。最後の1つを閉じたら欄を畳む。</summary>
        private void CloseAiTab(AiTab tab)
        {
            var index = _aiTabs.IndexOf(tab);
            if (index < 0) return;

            // 中のプログラムを終わらせ、WebView2 も片付ける（2026-10-27。前は終わらせるだけで、WebView2 が残っていた）。
            tab.View.Dispose();
            AiTerminals.Children.Remove(tab.View);
            _aiTabs.RemoveAt(index);
            RebuildAiTabStrip();
            StoreAiTabs();   // 自分で閉じた会話は、次の起動で戻さない

            if (_aiTabs.Count == 0)
            {
                _aiTab = null;
                ShowAiTabs();
                HideAiPane();
                return;
            }

            if (ReferenceEquals(_aiTab, tab))
            {
                SelectAiTab(_aiTabs[Math.Min(index, _aiTabs.Count - 1)]);
            }
        }

        /// <summary>
        /// claude のフックの知らせ（2026-10-27）。会話が始まった（新しく・続きから・/clear で作り直した）ら、その番号をタブに覚える。
        /// 終わった（/exit など）なら、その会話はもう戻さない。始まりと終わりの知らせは前後しうるので、終わりは同じ番号のときだけ消す。
        /// </summary>
        private void OnAiSession(bool started, string tabId, string session, string folder)
        {
            if (_aiStopping) return;

            var tab = _aiTabs.FirstOrDefault(other => other.Id == tabId);
            if (tab == null || !Guid.TryParse(session, out _)) return;

            if (started)
            {
                tab.SessionId = session;
                if (!string.IsNullOrEmpty(folder)) tab.Folder = folder;
            }
            else if (tab.SessionId == session)
            {
                tab.SessionId = null;
            }
            else
            {
                return;
            }

            StoreAiTabs();
        }

        /// <summary>
        /// claude の会話のあるタブを、並びのまま app.json に書く（次の起動で戻す）。PowerShell だけのタブは書かない。
        /// 落ちても残るように、変わるたびに書く。閉じている最中（claude を終わらせている）は書かない。
        /// </summary>
        private void StoreAiTabs()
        {
            if (_aiStopping) return;

            var tabs = _aiTabs.Where(tab => tab.SessionId != null)
                              .Select(tab => new AiTabState { Session = tab.SessionId, Folder = tab.Folder, Name = tab.Name })
                              .ToList();
            SavedAiTabs = tabs.Count > 0 ? tabs : null;   // 画面ごとに覚える（2026-10-31）
            _settings.Save();
        }

        /// <summary>
        /// 起動したとき、前に開いていた会話のタブを、それぞれの会話の続き（claude --resume 番号）として開き直す（2026-10-27）。
        /// 欄はまだ出さない（画面を戻すときに出す）。戻したタブがあれば true。
        /// </summary>
        private bool RestoreAiTabs()
        {
            var saved = SavedAiTabs;
            if (saved == null || saved.Count == 0) return false;

            // 話しかける前に閉じた会話は、claude がディスクに残していないので戻さない（2026-10-31）。
            // claude を直接起動するようにしてから、開いただけのタブも会話の番号を覚え、次の起動で「No conversation found」が出た。
            var restorable = saved.Where(state => ClaudeConversationExists(state.Session)).ToList();
            if (restorable.Count < saved.Count)
            {
                SavedAiTabs = restorable.Count > 0 ? restorable : null;
                _settings.Save();
            }

            if (restorable.Count == 0)
            {
                // 画面を戻すときに欄を出していたら、空のままにせず新しい会話を1つ開く。
                if (AiPane.Visibility == Visibility.Visible) AddAiTab(string.Empty);
                return false;
            }

            foreach (var state in restorable) AddAiTab(null, state);
            SelectAiTab(_aiTabs[0]);
            return true;
        }

        /// <summary>
        /// claude がその会話を残しているか（%USERPROFILE%\.claude\projects\フォルダの名前\番号.jsonl があるか）。
        /// claude は最初に話しかけるまで会話を書かない。調べられなければ true（今までどおり開き直してみる）。
        /// </summary>
        private static bool ClaudeConversationExists(string session)
        {
            try
            {
                var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

                var projects = Path.Combine(home, "projects");
                if (!Directory.Exists(projects)) return true;

                return Directory.EnumerateFiles(projects, session + ".jsonl", SearchOption.AllDirectories).Any();
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 欄の端末で起動するコマンド。中は普通の PowerShell（2026-10-24。まず端末として確実に動かし、動いてから下を付け直した）。
        /// その中で claude と打つと、今の状態を渡すフック（AiSettings）と指示を付けた Claude Code が起動する。
        /// PowerShell の関数 claude で、本物の claude.exe を設定付きで呼び直す。欄の外で起動した claude には付かない。
        /// 設定を書けなければ、関数は置かず、ただの PowerShell にする（claude も設定なしで動く）。
        /// </summary>
        /// <param name="start">PowerShell が起動したら続けて動かす claude の引数（AddAiTab）。null なら何も動かさない。</param>
        /// <param name="tabId">タブの番号（AiTab.Id）。環境変数 MDEDITOR_AI_TAB で claude のフックに渡す。</param>
        private string AiCommandLine(string start, string tabId, string name)
        {
            // Claude Code の入る場所（%USERPROFILE%\.local\bin）を PATH の先頭に足す。中の PowerShell はこのアプリの PATH を受け継ぐが、
            // 入れる前から動いていたアプリ（や、それを起動した Visual Studio）の PATH には入っていず、claude が見つからなかった（2026-10-24）。
            // exe の隣に置いた git（MinGit）も足す。Claude が変更の履歴を読むときは、自分で git を動かすため（2026-10-25）。
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
            foreach (var folder in new[] { DocumentHistory.BundledGitFolder, local })
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (Directory.Exists(folder) && path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0)
                    Environment.SetEnvironmentVariable("PATH", folder + ";" + path);
            }

            var powershell = "\"" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") + "\" -NoLogo";

            var claude = ChatView.FindClaude();
            if (claude == null)
            {
                if (start == null) return powershell;

                // 「＋」で claude の起動を選んだのに見つからなければ、黙って PowerShell だけにせず知らせる（2026-10-27）。
                Status("claude（Claude Code）が見つかりませんでした。PowerShell だけを起動します。");
                var missing = "Write-Host " + Quote("claude（Claude Code）が見つかりませんでした（" + local + " にも PATH にもありません）。"
                                                   + "入れてから、＋ で会話を開き直してください。") + " -ForegroundColor Yellow";
                return powershell + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(missing));
            }

            string settings, instructions;
            try
            {
                Directory.CreateDirectory(AiFolder);

                if (!_aiStaleFilesCleaned)
                {
                    _aiStaleFilesCleaned = true;
                    DeleteStaleAiFiles();
                }

                var id = WindowKey;   // 画面ごとに分ける（プロセスの番号-画面の番号）
                _aiContextPath = Path.Combine(AiFolder, "context-" + id + ".md");
                settings = Path.Combine(AiFolder, "settings-" + id + ".json");
                instructions = Path.Combine(AiFolder, "instructions.md");
                _aiSettingsPath = settings;

                File.WriteAllText(instructions, AiInstructions, new UTF8Encoding(false));
                File.WriteAllText(settings, AiSettings(_aiContextPath, AiPipeName), new UTF8Encoding(false));
                WriteAiContext(null);
            }
            catch (Exception ex)
            {
                Status("AI に渡す設定を書けませんでした: " + ex.Message);
                return powershell;
            }

            StartAiListener();

            // 引数は -EncodedCommand にまとめる（引用符や $ を気にしなくて済む）。
            // 起動したときに出していた claude の使い方の1行は外した（2026-10-30。タブが戻るので「続き」の案内は要らず、履歴は claude --resume）。
            var command = "$env:MDEDITOR_AI_TAB=" + Quote(tabId) + ";"
                         + "function claude { & " + Quote(claude) + " --settings " + Quote(settings)
                         + " --append-system-prompt-file " + Quote(instructions)
                         + (string.IsNullOrEmpty(name) ? string.Empty : " --name " + Quote(name)) + " @args }";
            if (start != null) command += ";claude " + start;
            return powershell + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        }

        private void ShowAiPane()
        {
            AiPane.Visibility = Visibility.Visible;
            AiSplitter.Visibility = Visibility.Visible;
            AiSplitterColumn.Width = new GridLength(4);
            AiColumn.Width = new GridLength(_settings.AiWidth);

            if (_aiTab != null) FocusAiTerminal(_aiTab);
        }

        private void HideAiPane()
        {
            if (AiPane.Visibility == Visibility.Visible) StoreAiWidth();

            AiPane.Visibility = Visibility.Collapsed;
            AiSplitter.Visibility = Visibility.Collapsed;
            AiSplitterColumn.Width = new GridLength(0);
            AiColumn.Width = new GridLength(0);
        }

        /// <summary>AI の欄の幅を覚える。仕切りを動かしたときと、欄を畳むとき。</summary>
        private void StoreAiWidth()
        {
            if (AiColumn.ActualWidth < 200) return;

            _settings.AiWidth = AiColumn.ActualWidth;
            _settings.Save();
        }

        private void AiSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            StoreAiWidth();
        }

        /// <summary>
        /// エディタを閉じるとき。claude も終わらせる（残すと窓の無いまま動き続ける）。
        /// WebView2 も片付け、このエディタの設定・状態のファイル（settings-番号.json・context-番号.md）も消す（2026-10-27。前は溜まっていった）。
        /// </summary>
        private void StopAi()
        {
            _aiStopping = true;
            foreach (var tab in _aiTabs) tab.View.Dispose();

            foreach (var path in new[] { _aiSettingsPath, _aiContextPath })
            {
                if (path == null) continue;

                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    // 消せなくても閉じるのは止めない。次に起動したとき DeleteStaleAiFiles が片付ける。
                }
            }

            _aiContextPath = null;
            _aiSettingsPath = null;
        }

        /// <summary>
        /// 前に落ちたエディタ（閉じる処理を通らなかったもの）が残した settings-番号.json・context-番号.md を消す（2026-10-27）。
        /// 番号のエディタがもう動いていなければ消す。動いているもの（2つ目の起動など）のファイルは残す。できる範囲で。
        /// </summary>
        private static void DeleteStaleAiFiles()
        {
            try
            {
                var self = Process.GetCurrentProcess();

                foreach (var pattern in new[] { "settings-*.json", "context-*.md" })
                {
                    foreach (var path in Directory.GetFiles(AiFolder, pattern))
                    {
                        // 名前は settings-プロセスの番号-画面の番号（2026-10-31。前は画面の番号が無かった）。プロセスの番号で見る。
                        var name = Path.GetFileNameWithoutExtension(path);
                        var number = name.Substring(name.IndexOf('-') + 1);
                        if (number.IndexOf('-') >= 0) number = number.Substring(0, number.IndexOf('-'));
                        int id;
                        if (!int.TryParse(number, out id) || id == self.Id) continue;
                        if (IsEditorRunning(id, self.ProcessName)) continue;

                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 片付けられなくても claude の起動は止めない。
            }
        }

        /// <summary>その番号でこのエディタ（同じ名前のプロセス）が動いているか。番号が別のプログラムに使い回されていれば false。</summary>
        private static bool IsEditorRunning(int id, string name)
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
                return false;   // 動いていない
            }
        }

        /// <summary>PowerShell の文字列（'...'）にする。中の ' は2つ重ねる。</summary>
        private static string Quote(string text)
        {
            return "'" + text.Replace("'", "''") + "'";
        }

        /// <summary>
        /// Claude Code のフック。どちらも PowerShell で動かす（フックは Git Bash で動くこともあるので、
        /// 引用符や $ を含まない -EncodedCommand にして、どちらのシェルでも同じに通るようにする）。
        /// どちらも、このエディタのパイプにつないで1行送る。
        /// ・書き換えたあと（PostToolUse）: 「changed」。エディタが外で変わった文書を読み直す
        /// ・話しかけたとき（UserPromptSubmit）: 「prompt」。エディタが保存して今の状態を書き出し、「ok」を返す。
        ///   そのあとで今の状態のファイルを出力する。出力は Claude への文脈になる。
        ///   エディタが閉じていてつながらなければ、前に書いた状態をそのまま出す
        /// </summary>
        private static string AiSettings(string contextPath, string pipeName)
        {
            var connect =
                "$c=New-Object System.IO.Pipes.NamedPipeClientStream('.','" + pipeName + "',[System.IO.Pipes.PipeDirection]::InOut);";

            // つなぐのを待つのは5秒（2026-10-27。前は1秒で、ほかのタブの頼みに応えている間の「changed」が落ちることがあった）。
            // エディタはつながりごとに次の口を開き直すので、普通はすぐつながる。エディタが閉じると中の claude も終わる（ジョブ）ので、
            // パイプが無くて5秒待つことはまず無い。
            var notify = connect +
                "try{$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);$w.WriteLine('changed');$w.Flush()}catch{};$c.Dispose()";

            var quoted = Quote(contextPath);
            var context = connect +
                "try{$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);$w.WriteLine('prompt');$w.Flush();" +
                "$r=New-Object System.IO.StreamReader($c);[void]$r.ReadLine()}catch{};$c.Dispose();" +
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
                "if(Test-Path -LiteralPath " + quoted + "){[Console]::Out.Write([System.IO.File]::ReadAllText(" + quoted + "))}";

            return "{\"hooks\":{" +
                   "\"PostToolUse\":[{\"matcher\":\"Edit|Write|MultiEdit|NotebookEdit|Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"" + PowerShell(notify) + "\"}]}]," +
                   "\"UserPromptSubmit\":[{\"hooks\":[{\"type\":\"command\",\"timeout\":300,\"command\":\"" + PowerShell(context) + "\"}]}]," +
                   "\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"" + PowerShell(SessionHook(connect, "session")) + "\"}]}]," +
                   "\"SessionEnd\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"" + PowerShell(SessionHook(connect, "ended")) + "\"}]}]" +
                   "}}";
        }

        /// <summary>
        /// 会話が始まった・終わったときのフック（2026-10-27）。次の起動で会話のタブを戻すため、
        /// 「session（ended）→タブの番号→会話の番号→フォルダ」をタブ区切りの1行で送る。
        /// 中身（JSON）は標準入力に来る。フォルダの名前に日本語があっても化けないよう、バイトのまま UTF-8 で読む。
        /// タブの番号（MDEDITOR_AI_TAB）が無ければ（欄の外で起動した claude）、何も送らない。
        /// </summary>
        private static string SessionHook(string connect, string kind)
        {
            return "$t=$env:MDEDITOR_AI_TAB;if($t){try{" +
                   "$m=New-Object System.IO.MemoryStream;[Console]::OpenStandardInput().CopyTo($m);" +
                   "$j=[System.Text.Encoding]::UTF8.GetString($m.ToArray())|ConvertFrom-Json;" +
                   connect +
                   "$c.Connect(5000);$w=New-Object System.IO.StreamWriter($c);" +
                   "$w.WriteLine('" + kind + "'+[char]9+$t+[char]9+$j.session_id+[char]9+$j.cwd);$w.Flush();$c.Dispose()}catch{}}";
        }

        private static string PowerShell(string script)
        {
            return "powershell -NoProfile -NonInteractive -EncodedCommand " +
                   Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        /// <summary>
        /// フックからの頼みを受ける。待つのは別のスレッドで、頼まれたことは画面のスレッドでやる。
        /// 「prompt」は、保存と状態の書き出しが済むまで待ってから「ok」を返す（フックはそのあとで状態を読む）。
        /// つながったら、そのつながりは別のスレッドに任せて、すぐ次の口を開く（2026-10-27）。前は口が1つだけで、
        /// あるタブの「prompt」に応えている間（保存の確かめのダイアログを出している間など）、ほかのタブの知らせがつながらずに落ちた。
        /// </summary>
        private void StartAiListener()
        {
            if (_aiListener != null) return;

            _aiListener = new Thread(delegate ()
            {
                while (true)
                {
                    NamedPipeServerStream server = null;
                    try
                    {
                        server = new NamedPipeServerStream(AiPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, AiPipeSecurity());
                        server.WaitForConnection();
                    }
                    catch (Exception)
                    {
                        // つないですぐ切られた等。少し置いて待ち直す。
                        if (server != null) server.Dispose();
                        Thread.Sleep(100);
                        continue;
                    }

                    var connection = server;
                    var worker = new Thread(delegate () { ServeAiRequest(connection); });
                    worker.IsBackground = true;   // 終了を止めない
                    worker.Start();
                }
            });
            _aiListener.IsBackground = true;   // 終了を止めない
            _aiListener.Start();
        }

        /// <summary>つながった1つの頼みに応える（そのつながり用のスレッドで）。</summary>
        private void ServeAiRequest(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                {
                    var reader = new StreamReader(server, new UTF8Encoding(false), false, 256, true);
                    var request = reader.ReadLine();

                    if (request == "prompt")
                    {
                        // 未保存の編集があれば、保存してから渡す（2026-10-25。話しかけたら保存してよいと決めた）。
                        // 外で書き換わっていて上書きするか尋ねている間はフックが待つ。止まったままにはしないよう、
                        // 4分半で諦めて前の状態を渡させる（フックの持ち時間は5分）。
                        Dispatcher.Invoke(new Action(HandleAiPrompt), TimeSpan.FromSeconds(270));

                        var writer = new StreamWriter(server, new UTF8Encoding(false), 256, true);
                        writer.WriteLine("ok");
                        writer.Flush();
                        server.WaitForPipeDrain();
                    }
                    else if (request != null && (request.StartsWith("session\t", StringComparison.Ordinal)
                                                 || request.StartsWith("ended\t", StringComparison.Ordinal)))
                    {
                        var parts = request.Split('\t');
                        if (parts.Length >= 3)
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                OnAiSession(parts[0] == "session", parts[1], parts[2], parts.Length > 3 ? parts[3] : null);
                            }));
                    }
                    else
                    {
                        Dispatcher.BeginInvoke(new Action(ReloadChangedDocuments), DispatcherPriority.Background);
                    }
                }
            }
            catch (Exception)
            {
                // つないですぐ切られた・エディタを閉じている途中等。フックは前の状態を出す。
            }
        }

        /// <summary>
        /// パイプに入れるのは、このエディタを動かしている利用者だけ（2026-10-27）。ネットワーク越しのつなぎは断る。
        /// 前は既定の権限で、ほかの利用者やネットワーク越しからも「prompt」を送って保存させることができた。
        /// </summary>
        private static PipeSecurity AiPipeSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }

        /// <summary>
        /// 話しかけられた頼み（フックの「prompt」）に応える。保存して、今の状態を書き出す。
        /// ダイアログを出している間（保存するか尋ねている・上書きするか尋ねている等）は保存しない（2026-10-27）。
        /// その最中にも頼みは届き、ここで保存すると、利用者が「保存しない」「キャンセル」を選ぼうとしている文書まで保存してしまった。
        /// そのときは状態だけ書き出し、保存しなかったことを状態に書き添える。
        /// </summary>
        private void HandleAiPrompt()
        {
            string skipped = null;
            if (_aiPromptHandling) skipped = "前の頼みに応えている最中だった";
            else if (IsShowingDialog()) skipped = "ダイアログを出していた";

            if (skipped == null)
            {
                _aiPromptHandling = true;
                try
                {
                    SaveForAi();
                }
                finally
                {
                    _aiPromptHandling = false;
                }
            }
            else if (_stack.Any(view => view.IsDirty))
            {
                Status("AI に渡す前の保存をしませんでした（" + skipped + "ため）。");
            }

            WriteAiContext(skipped == null ? null : "エディタは" + skipped + "ため、今回は保存していない。");
            ShowAiContextSummary();
        }

        /// <summary>
        /// ダイアログを出している最中か。このアプリのダイアログはどれもこの窓が親なので、出ている間は Windows がこの窓を操作できなくする。それで見分ける。
        /// </summary>
        private bool IsShowingDialog()
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return handle != IntPtr.Zero && !IsWindowEnabled(handle);
        }

        /// <summary>
        /// Claude に話しかけられたとき、未保存の編集があれば保存する（尋ねない。2026-10-25）。Claude が読むのはディスクの内容なので、
        /// 今の画面に揃えてから渡す。開いたあとに外（Claude 自身など）で書き換わっていたときだけ、上書きするか尋ねる（SaveDocument）。
        /// 自動保存はやめたが、Claude に話しかけるのは「今の内容を見て」という意思表示なので、保存してよいとした。
        /// </summary>
        private void SaveForAi()
        {
            if (_stack.Any(view => view.IsDirty)) SaveAll();
        }

        /// <summary>
        /// エディタの今の状態を書き出す。話しかけられたときと、claude を起動するとき。
        /// claude をまだ起動していなければ何もしない。書けなくても編集の邪魔はしない。
        /// </summary>
        /// <param name="note">状態に書き添えること（保存しなかった理由など）。無ければ null。</param>
        private void WriteAiContext(string note)
        {
            if (_aiContextPath == null) return;

            try
            {
                File.WriteAllText(_aiContextPath, AiContext(note), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Status("AI に今の状態を渡せませんでした: " + ex.Message);
            }
        }

        /// <summary>
        /// AI の欄の上端の段に、今渡した状態を1行で出す（2026-10-24）。話しかけられて状態を書いたとき。
        /// 例: 渡した状態  TODO.md › p3「Tasks」 12 行目  14:05:31
        /// </summary>
        private void ShowAiContextSummary()
        {
            var view = Active;
            if (view == null || view.Owner == null)
            {
                AiContextText.Text = "渡した状態  ファイルを開いていません  " + DateTime.Now.ToString("HH:mm:ss");
                AiContextText.ToolTip = null;
                return;
            }

            var selected = !string.IsNullOrEmpty(view.SimpleSelectedText.Trim());
            AiContextText.Text = "渡した状態  " + Path.GetFileName(view.FilePath) + " › " + PageLabel(view)
                                 + "  " + view.TextArea.Caret.Line + " 行目" + (selected ? "（選択あり）" : string.Empty)
                                 + "  " + DateTime.Now.ToString("HH:mm:ss");
            AiContextText.ToolTip = view.FilePath;
        }

        private string AiContext(string note)
        {
            var text = new StringBuilder();
            text.Append("[Tsuzuri の今の状態 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("]\n");
            if (!string.IsNullOrEmpty(note)) text.Append(note).Append('\n');

            var view = Active;
            if (view == null || view.Owner == null)
            {
                text.Append("ファイルは開いていない。\n");
                return text.ToString();
            }

            var document = view.Owner;
            var before = document.LinesBefore(view.PageId);
            var line = view.TextArea.Caret.Line;

            text.Append("ファイル: ").Append(document.FilePath).Append('\n');

            var history = DocumentHistory.GitDirOf(document.FilePath);
            if (Directory.Exists(history))
                text.Append("変更の履歴: git -c safe.directory=* --git-dir=\"").Append(history).Append("\" log -p -- \"").Append(Path.GetFileName(document.FilePath)).Append("\"\n");
            text.Append("見ているページ: ").Append(PageLabel(view));
            if (before > 0) text.Append("（ファイルの ").Append(before).Append(" 行目の印から）");
            text.Append('\n');

            text.Append("カーソル: ページの ").Append(line).Append(" 行目");
            if (before >= 0) text.Append("（ファイルの ").Append(before + line).Append(" 行目）");
            text.Append(": ").Append(view.Document.GetText(view.Document.GetLineByNumber(line))).Append('\n');

            var selected = view.SimpleSelectedText;
            if (!string.IsNullOrEmpty(selected.Trim()))
            {
                text.Append("選んでいる文字:\n```\n").Append(selected.Replace("\r\n", "\n")).Append("\n```\n");
            }

            text.Append("開いているページの並び（左から）: ")
                .Append(string.Join(" > ", _stack.Select(PageLabel).ToArray())).Append('\n');

            if (_stack.Any(page => page.IsDirty))
                text.Append("エディタに保存できていない編集がある。ファイルの中身はエディタの画面より古い。\n");

            return text.ToString();
        }

        private static string PageLabel(MarkdownView view)
        {
            var name = string.IsNullOrEmpty(view.PageId) ? "ルート" : view.PageId;
            return name + "「" + view.Title + "」" + (view.IsReadOnly ? "（ファイルから消えたページ）" : string.Empty);
        }

        /// <summary>
        /// 外で書き換わった文書を読み直す。フックの知らせと、エディタに戻ったとき（Activated）。
        /// 未保存の編集がある文書は読み直さない（読み直すとその編集が消える）。そのときは、保存の確かめ（外部変更で止まる）に任せて知らせる。
        /// 読み直して無くなったページを開いていれば、そのページから右を閉じる。
        /// </summary>
        private void ReloadChangedDocuments()
        {
            // ダイアログを出している間は読み直さない（2026-10-24）。ダイアログの最中にも知らせ（フック・Activated）は届くが、
            // そこで画面の並びや中身を変えると、ダイアログを出した操作が古い前提のまま進む
            // （開く位置がずれる・確認した一覧と違うページを消す等）。このアプリのダイアログはどれもこの窓が親なので、
            // 出ている間は Windows がこの窓を操作できなくする。それで見分ける。閉じてこの窓に戻ったとき（Activated）に読み直す。
            if (IsShowingDialog()) return;

            var documents = _stack.Select(view => view.Owner).Where(owner => owner != null).Distinct().ToList();
            var reloaded = new List<PageDocument>();

            foreach (var document in documents)
            {
                bool changed;
                try
                {
                    changed = document.IsChangedOnDisk && File.Exists(document.FilePath);
                }
                catch (Exception)
                {
                    continue;   // 共有が一瞬見えない等。次のきっかけで確かめ直す
                }

                if (!changed) continue;

                if (document.IsDirty)
                {
                    Status("外で書き換えられましたが、未保存の編集があるため読み直していません: " + Path.GetFileName(document.FilePath));
                    continue;
                }

                try
                {
                    document.Reload();
                    reloaded.Add(document);
                }
                catch (Exception ex)
                {
                    // 書いている途中を読んだ等。今の中身のまま、次のきっかけで読み直す。
                    Status("外での書き換えを読み直せませんでした: " + ex.Message);
                }
            }

            if (reloaded.Count == 0) return;

            // 無くなったページから右は閉じる。ルートはいつもあるので、先頭は残る。
            // 右には別のファイルのページもありうる。閉じるとそのファイルは保存の対象から外れるので、閉じる前に保存する
            // （2026-10-24。保存せずに閉じて、別のファイルの未保存の編集が黙って消えた）。保存も別名での書き出しもできなければ閉じない。
            var lost = _stack.FindIndex(view => reloaded.Contains(view.Owner) && !view.Owner.Contains(view.PageId));
            if (lost > 0 && KeepClosingDocuments(lost))
            {
                while (_stack.Count > lost) _stack.RemoveAt(_stack.Count - 1);
                if (!_stack.Contains(_focused)) _focused = _stack[_stack.Count - 1];
            }
            else
            {
                lost = -1;   // 閉じない。無くなったページは、打てば行き先の無いページの扱いになる（別名で書き出す）
            }

            foreach (var view in _stack)
            {
                if (reloaded.Contains(view.Owner)) view.ReloadFromOwner();
            }

            if (lost > 0) RebuildStack(false);

            _outlineSignature = null;
            RefreshOutline();
            RefreshPageTitles();
            UpdateChrome();

            Status("外での書き換えを読み直しました: " + string.Join("、", reloaded.Select(d => Path.GetFileName(d.FilePath)).ToArray()));
        }

        /// <summary>
        /// スタックの from から右を閉じてよいか。そこにしか無い文書に未保存の編集があれば閉じない（false）。
        /// 外からの知らせで動くので、尋ねもせず、勝手に保存もしない（2026-10-25。自動保存をやめた）。
        /// 閉じなかった無くなったページは、打てば行き先の無いページの扱いになる（別名で書き出す）。
        /// </summary>
        private bool KeepClosingDocuments(int from)
        {
            var staying = _stack.Take(from).Select(view => view.Owner).ToList();
            var unsaved = _stack.Skip(from).Select(view => view.Owner)
                .FirstOrDefault(owner => owner != null && owner.IsDirty && !staying.Contains(owner));
            if (unsaved == null) return true;

            Status("無くなったページを閉じると、保存していない「" + Path.GetFileName(unsaved.FilePath) + "」の編集も閉じるので、閉じずに残しました。");
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr window);
    }
}
