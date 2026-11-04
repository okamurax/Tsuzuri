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
    public partial class MainWindow : Window
    {
        /// <summary>アウトラインの1項目に紐づける情報。Tag は1つしか無いためまとめて持つ。</summary>
        private sealed class OutlineNode
        {
            public int Line;

            /// <summary>たたみ状態の鍵。ファイル・ページと、親から辿った見出しの名前（RebuildOutline）。</summary>
            public string Path;

            /// <summary>見出しの文字。ブックマークの照合に使う。</summary>
            public string Text;

            /// <summary>ブックマークしている印の •（押せない。付け外しは本文の行番号の右ボタンのダブルクリック）。</summary>
            public TextBlock Mark;

            /// <summary>見出しの名前の表示と、それを入れている段。名前を変えている間は、Label の代わりに入力欄を入れる。</summary>
            public TextBlock Label;
            public DockPanel Panel;

            /// <summary>見出しの段の枠。ドラッグで落とす位置の青い線を、この上端か下端に出す。</summary>
            public Border Frame;

            /// <summary>見出しのレベル（1〜6）。右クリックの「レベルを上げる・下げる」を押せるかの判断に使う。</summary>
            public int Level;
        }

        /// <summary>
        /// 見出しのブックマーク（SPEC 7章）。行番号ではなく見出しの文字で覚える。
        /// Line は同じ文字の見出しが複数あるときの手がかりにだけ使う。
        /// </summary>
        private sealed class HeadingBookmark
        {
            public string File;

            /// <summary>ファイルの中のページ。空ならルート。</summary>
            public string Page;

            public string Text;

            /// <summary>最後に見た行（1 始まり）。</summary>
            public int Line;

            /// <summary>
            /// 登録したときの画面の状態（ページの並び・カーソル・ランチャー・AI の欄。2026-10-25）。開くとそのとおりに戻す。
            /// null なら覚えていない（前の版で登録したもの）。見出しのページをそのまま開く。
            /// </summary>
            public ScreenState Screen;
        }

        /// <summary>
        /// 見出しのブックマーク。新しいものが先頭。
        /// 手元に持つのはスタックにあるファイルの分だけ。閉じたファイルの分は .文書名/state.json にある。
        /// </summary>
        private readonly List<HeadingBookmark> _headingBookmarks = new List<HeadingBookmark>();

        /// <summary>
        /// スタックにあるファイルの状態（.文書名/state.json）。開くとき（OpenFile）に読み、ファイルが外れたら手放す。
        /// 鍵は開く入口で揃えた本当のパス。スタックにあるファイルの分は必ずある。
        /// </summary>
        private readonly Dictionary<string, DocumentState> _states =
            new Dictionary<string, DocumentState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>開いているページの連鎖。末尾がいちばん右（SPEC 6章）。</summary>
        private readonly List<MarkdownView> _stack = new List<MarkdownView>();

        /// <summary>いま画面に出ているページ。いつも連鎖の末尾の1つだけ（2026-10-27。前は最大2つ）。</summary>
        private readonly List<MarkdownView> _visible = new List<MarkdownView>();

        /// <summary>各ページの帯に出しているタイトル。_visible と同じ並び。</summary>
        private readonly List<TextBlock> _pageTitles = new List<TextBlock>();

        /// <summary>フォーカスのあるページ。アウトラインはこれに追従する。</summary>
        private MarkdownView _focused;

        /// <summary>
        /// ランチャー（SPEC 4章）。本文の右半分に重ねて出す。下のエディタ（スタック）には触らないので、
        /// 開け閉めしても、開いているファイルや画面の並びは変わらない。
        /// </summary>
        private LauncherView _launcher;

        /// <summary>右のファイラー（SPEC 9章）。初めて開いたときに作り、終了まで最後のフォルダを覚えている。</summary>
        private FilerView _filer;

        private readonly DispatcherTimer _outlineRefresh = new DispatcherTimer();
        private readonly DispatcherTimer _statusReset = new DispatcherTimer();

        private readonly List<TreeViewItem> _outlineOrder = new List<TreeViewItem>();
        private readonly Dictionary<TreeViewItem, TreeViewItem> _outlineParents =
            new Dictionary<TreeViewItem, TreeViewItem>();
        private readonly HashSet<string> _collapsedPaths = new HashSet<string>();

        private object _handledDrop;
        private string _outlineSignature;

        /// <summary>見出しのブックマークを追いかけるための、直前の見出しの一覧（SPEC 7章）。</summary>
        private string _trackedPage;
        private List<OutlineHeading> _trackedEntries;

        /// <summary>見出しのブックマーク欄を最後に組んだときのファイル。開いているファイルの分だけを出すため。</summary>
        private string _headingContext;

        /// <summary>exe と同階層の app.json。今は最近使ったファイルだけ（SPEC 4章）。</summary>
        /// <summary>起動の入口（App）で読めたものだけを受け取る。</summary>
        private readonly AppSettings _settings;

        public MainWindow(AppSettings settings) : this(settings, null, null)
        {
        }

        /// <param name="opener">増やした画面なら、元の画面（同じ位置・大きさで出す）。最初の画面は null。</param>
        /// <param name="session">起動のときに戻す画面なら、その覚え。「＋」で新しく増やした画面は null。</param>
        private MainWindow(AppSettings settings, MainWindow opener, WindowSession session)
        {
            _settings = settings;
            InitializeComponent();
            RegisterWindow(session);
            DirtyText.Foreground = Theme.DirtyMark;

            // アウトラインの更新はまとめて行う。見出しの一覧はエディタ側で
            // 使い回されるので、この間隔は短くてよい。
            _outlineRefresh.Interval = TimeSpan.FromMilliseconds(60);
            _outlineRefresh.Tick += delegate
            {
                _outlineRefresh.Stop();
                RefreshOutline();
                RefreshPageTitles();
                UpdateChrome();
            };

            // 知らせを出しっぱなしにせず、しばらくしたら元の表示へ戻す。
            _statusReset.Interval = TimeSpan.FromSeconds(6);
            _statusReset.Tick += delegate
            {
                _statusReset.Stop();
                ShowRest();
            };

            SourceInitialized += delegate
            {
                UseDarkTitleBar();
                DisableTransitions();
            };

            // 戻ってきたら、外で変わった文書を読み直す（SPEC 19章）。離れるときに保存するのはやめた（2026-10-25。自動保存をやめた）。
            Activated += delegate { ReloadChangedDocuments(); };

            // ほかのアプリに切り替えたら、全部の画面の状態を覚える（2026-10-31。VM ごと止める前は、たいてい Mac 側に移るため）。
            Deactivated += delegate { ScheduleRememberScreens(); };
            Closing += OnClosing;

            // Ctrl+S で保存（2026-10-25）。ショートカットは既定では付けない方針（SPEC 11章）だが、自動保存をやめたので、これだけは付ける。
            // AI の欄の端末（WebView2）にキーボードがあるときは保存せず、端末のキーとして向こうに渡す（2026-10-27）。
            // WebView2 は Ctrl+文字のキーをいったんこの窓に回してくる（ここで処理済みにすると端末に届かない）ので、
            // 前は端末で打った Ctrl+S でもエディタが保存していた。回ってきたキーの出どころ（WebView2）が欄の中かで見分ける。
            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;

                var source = e.OriginalSource as Visual;
                if (AiPane.IsKeyboardFocusWithin || (source != null && AiPane.IsAncestorOf(source))) return;

                e.Handled = true;
                Save_Click(this, null);
            };

            // Ctrl+PageUp で前の画面、Ctrl+PageDown で次の画面（タブの並び。端で反対の端へ回る。2026-11-01）。
            // AI の欄の端末にキーボードがあるときも切り替える（端末ではまず使わないキー）。
            PreviewKeyDown += (sender, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control || (e.Key != Key.PageUp && e.Key != Key.PageDown)) return;

                e.Handled = true;
                SwitchWindow(e.Key == Key.PageUp ? -1 : 1);
            };

            // 変更の履歴（git）を記録できなかったら知らせる。記録は裏のスレッドで動く。
            // アプリ全体の知らせなので、画面を閉じたら外す（閉じた画面に知らせない・未保存のまま閉じた文書を書き出さない）。
            Action<string> historyFailed = message => Dispatcher.BeginInvoke(new Action(delegate { Status(message); }));
            DocumentHistory.Failed += historyFailed;

            // 落ちる前・ログオフのときは、未保存の編集を別名で残す。尋ねられないので、元のファイルは書き換えない。
            System.Windows.Threading.DispatcherUnhandledExceptionEventHandler crashed = delegate { KeepUnsavedQuietly(); };
            // ログオフ・シャットダウンでは Closing が来ないまま全部の画面が閉じる（Closed は来る）。「終了」と同じに扱い、
            // 閉じた画面の覚えを消さない・最初の画面を引き継がせない（2026-11-02。前は × で閉じた扱いになり、最後の1画面しか戻らなかった）。
            SessionEndingCancelEventHandler ending = delegate
            {
                _closingAll = true;
                KeepUnsavedQuietly();
                RememberLastScreen();
                _settings.Save();
            };
            if (Application.Current != null)
            {
                Application.Current.DispatcherUnhandledException += crashed;
                Application.Current.SessionEnding += ending;
            }

            Closed += delegate
            {
                DocumentHistory.Failed -= historyFailed;
                if (Application.Current == null) return;

                Application.Current.DispatcherUnhandledException -= crashed;
                Application.Current.SessionEnding -= ending;
            };

            SidebarColumn.Width = new GridLength(_settings.SidebarWidth);

            // ランチャーを出している間に、その外（アウトライン・ツールバー等）を押したら閉じる。Esc でも閉じる（SPEC 4章）。
            PreviewMouseDown += CloseLauncherOnOutsideClick;
            LauncherHost.KeyDown += LauncherHost_KeyDown;

            // アウトラインのドラッグで節を動かす（SPEC 7章）。
            OutlineTree.PreviewMouseLeftButtonDown += OutlineTree_PreviewMouseLeftButtonDown;
            SetUpHeadingBookmarkDrag();

            OutlineTree.PreviewMouseMove += OutlineTree_PreviewMouseMove;
            _outlineAutoScroll.Tick += delegate { OutlineAutoScroll(); };
            OutlineTree.PreviewMouseLeftButtonUp += OutlineTree_PreviewMouseLeftButtonUp;
            OutlineTree.LostMouseCapture += delegate { EndOutlineDrag(); };
            if (opener != null) PlaceLike(opener);
            else RestoreWindowBounds();
            ApplyHistorySplit();

            RebuildStack(false);

            CreateFiler();

            // 開けないときのダイアログをこのウィンドウに付けたいので、出てから開く。
            // AI の会話のタブは、窓が出て落ち着いてから戻す（2026-10-28）。窓が出る前に作った端末（WebView2）は、
            // ほかのアプリに切り替えて戻るまで、日本語の変換が窓の左上に出た（＋で後から作ったタブは正しい所に出る）。
            // 戻すまでは、画面を戻すときに欄を出しても新しい会話のタブを足さない（_aiRestorePending）。隠れている画面は、前に出たときに戻す。
            // 終了したときの画面・AI の会話のタブを戻す。最初の画面は、そのあとでほかの画面を作り直す（2026-10-31）。
            // 「＋」で新しく増やした画面は、覚えが空なので何も開かない（ランチャー・WordPress・新規作成から開く）。
            Loaded += delegate
            {
                RefreshWindowLists();   // ほかの画面の一覧にも、この画面を足す
                _aiRestorePending = SavedAiTabs != null && SavedAiTabs.Count > 0;

                if (!IsPrimary)
                {
                    if (!RestoreLastScreen()) ShowRest();
                    MarkScreenReady();
                    return;
                }

                CleanWordPressTemp();   // 前の起動で読み込んだ WordPress の記事は、閉じたらそれで終わり（SPEC 20章）。開き直す前に消す
                OpenLastFile();
                MarkScreenReady();
                RestoreOtherWindows();
            };
            ContentRendered += delegate
            {
                if (!_aiRestorePending) return;

                Dispatcher.BeginInvoke(new Action(RestoreAiTabsWhenShown), DispatcherPriority.ApplicationIdle);
            };
        }

        /// <summary>
        /// 前の会話のタブを戻す。画面が隠れていれば、初めて前に出たときに戻す（2026-11-02）。
        /// 起動のときに戻したほかの画面は隠れるので、隠れた窓の中で端末（WebView2）を作ると、日本語の変換が窓の左上に出るおそれがある。
        /// </summary>
        private void RestoreAiTabsWhenShown()
        {
            if (!_aiRestorePending || !OpenWindows.Contains(this)) return;

            if (!IsVisible)
            {
                DependencyPropertyChangedEventHandler shown = null;
                shown = delegate
                {
                    if (!IsVisible) return;
                    IsVisibleChanged -= shown;
                    Dispatcher.BeginInvoke(new Action(RestoreAiTabsWhenShown), DispatcherPriority.ApplicationIdle);
                };
                IsVisibleChanged += shown;
                return;
            }

            _aiRestorePending = false;
            if (RestoreAiTabs() && AiPane.Visibility != Visibility.Visible) ShowAiPane();
        }

        /// <summary>
        /// 前回の位置と大きさで出す（SPEC 2章）。
        /// モニターを外したなどで画面の外に出てしまう位置なら、位置は使わず既定の場所に出す。
        /// </summary>
        private void RestoreWindowBounds()
        {
            if (_settings.WindowWidth <= 0) return;

            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;

            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var window = new Rect(_settings.WindowLeft, _settings.WindowTop, Width, Height);

            // 上端（つかんで動かすツールバー）の辺りが画面に 100px 以上かかっていれば、つかんで動かせる。
            var grip = new Rect(window.Left, window.Top, window.Width, 40);
            grip.Intersect(screen);

            if (!grip.IsEmpty && grip.Width >= 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }

            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        }

        /// <summary>最大化しているときは、最大化する前の位置と大きさを覚える。最小化は覚えない。</summary>
        private void StoreWindowBounds()
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty || bounds.Width < 640 || bounds.Height < 400) return;

            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
        }

        /// <summary>フォーカスのあるページ。無ければいちばん右（SPEC 7章）。</summary>
        private MarkdownView Active
        {
            get
            {
                if (_focused != null && _stack.Contains(_focused)) return _focused;
                return _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>
        /// タイトルバーもダークにする。WPF からは触れないので Windows に直接頼む。
        /// 属性番号は Windows 10 2004 で 20 に変わった。古いビルド向けに 19 も試す。
        /// 効かない環境ではタイトルバーが明るいままになるだけ。
        /// </summary>
        private void UseDarkTitleBar()
        {
            UseDarkTitleBar(this);
        }

        /// <summary>ほかのウィンドウ（ランチャー）のタイトルバーも同じにする。</summary>
        private static void UseDarkTitleBar(Window target)
        {
            try
            {
                var window = new WindowInteropHelper(target).Handle;
                if (window == IntPtr.Zero) return;

                var on = 1;

                if (DwmSetWindowAttribute(window, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(window, 19, ref on, sizeof(int));

                // Windows 11 では、タイトルバーをツールバーと同じ紺（#192339）にして一続きに見せる。
                // 色は 0x00BBGGRR。Windows 10 では効かず、黒いタイトルバーのままになる。
                var caption = 0x00392319;
                DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
                var captionText = 0x00E4DFDD;
                DwmSetWindowAttribute(window, 36, ref captionText, sizeof(int));
            }
            catch (Exception)
            {
                // 古い Windows では用意されていない。見た目だけの話なので黙って諦める。
            }
        }

        /// <summary>
        /// 窓を出す・隠すときの Windows のアニメーション（ふわっと出る・消える）を止める（2026-11-03）。
        /// 画面のタブの切り替えは、前に出す画面を Show して、ほかを Hide する（MainWindow.Windows.cs）。そのたびに両方の窓がアニメーションし、ちらついて見えたため。
        /// 効かない環境では今までどおりアニメーションするだけ。
        /// </summary>
        private void DisableTransitions()
        {
            try
            {
                var window = new WindowInteropHelper(this).Handle;
                if (window == IntPtr.Zero) return;

                var on = 1;
                DwmSetWindowAttribute(window, 3, ref on, sizeof(int));   // DWMWA_TRANSITIONS_FORCEDISABLED
            }
            catch (Exception)
            {
            }
        }

        // ---------------------------------------------------------------
        // ステータスバー
        //
        // 普段は開いているファイルの場所を出しておき、
        // 知らせは一時的に重ねて、しばらくすると元に戻す。
        // 出しっぱなしにすると、ずっと前のエラーが残り続けて紛らわしい。
        // ---------------------------------------------------------------

        private void Status(string message)
        {
            StatusText.Text = message;

            _statusReset.Stop();
            _statusReset.Start();
        }

        /// <summary>普段の表示に戻す。知らせが出ている間は触らない。</summary>
        private void ShowRest()
        {
            if (_statusReset.IsEnabled) return;

            StatusText.Text = HasDocument
                ? Active.FilePath
                : "ツールバーの「ランチャー」からファイルを選ぶか、「新規作成」で作ってください。";
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _closing = true;
            try
            {
                OnClosingCore(e);
            }
            finally
            {
                _closing = false;
            }
        }

        private void OnClosingCore(CancelEventArgs e)
        {
            // 「終了」で最初の画面を閉じるときは、ほかの画面を先に閉じる。どれかで取りやめたら、こちらも閉じない（2026-10-31）。
            // タブの × で閉じるときは、この画面だけ（最初の画面なら、次の画面が引き継ぐ）。
            // 最初の画面の保存を先に尋ねる（2026-11-02）。前はほかの画面を閉じてから尋ね、ここで取りやめると、もう閉じた画面の覚えが消えていた。
            // 編集していなければ何も尋ねずに閉じる。編集していれば、保存するか尋ねる（2026-10-25）。
            if (!ConfirmLeaving(new MarkdownView[0], null))
            {
                if (_closingAll) CancelClosingAll();
                e.Cancel = true;
                return;
            }

            if (IsPrimary && _closingAll && !CloseOtherWindows())
            {
                e.Cancel = true;
                return;
            }

            StoreHeadingBookmarks();
            RememberFileStates();
            RememberLastScreen();   // StoreLayout が app.json に書く。増やした画面を「終了」で閉じたときは、閉じたあとで覚えごと消す
            StoreLayout();
            _filer.Stop();
            StopAi();
            DeleteWordPressSession();   // 読み込んだ WordPress の記事の一時ファイルは、閉じたら消す（SPEC 20章）
        }

        /// <summary>
        /// 落ちる直前とログオフ・シャットダウンのとき。尋ねられないので、未保存の文書は元のファイルを書き換えず、
        /// 隣に別名（名前.recovered-日時.md）で書き出す（2026-10-25。前は元のファイルに保存していた）。
        /// 1つの失敗で残りを諦めないよう、文書ごとに受け止める。
        /// </summary>
        private void KeepUnsavedQuietly()
        {
            var done = new List<PageDocument>();

            foreach (var view in _stack.ToList())
            {
                var document = view.Owner;
                if (document == null || !document.IsDirty || done.Contains(document)) continue;

                done.Add(document);

                try
                {
                    document.SaveRecoveryCopy();
                }
                catch (Exception)
                {
                    // ここで投げると元の異常を覆い隠すだけ。
                }
            }
        }

        private bool HasDocument
        {
            get { return Active != null && !string.IsNullOrEmpty(Active.FilePath); }
        }
    }
}
