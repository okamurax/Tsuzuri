using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: 画面を増やす（2026-10-31）。
    //
    // 1つのアプリの中で、この画面（MainWindow）を丸ごともう1つ作る。中の作りは変えず、画面ごとに今までと同じに動く。
    // 別の exe を起動しないのは、app.json を2つのアプリが別々に書いて、ランチャーの登録などを消し合わないため（設定はメモリの上で1つを共有する）。
    // ・1つの文書は1つの画面だけで開く（リンクで右に開いた .md・WordPress の記事も。休止している画面の分も）。.文書名/state.json と変更の記録を、2つの画面から書かないため
    // ・終了したときに開いていた画面は、次の起動で全部戻す（ページの並び・AI の会話のタブ。WordPress の記事は読み込み直す）。
    //   最初の画面の分は app.json の Screen・Collapsed・AiTabs、増やした画面の分は Windows に覚える。タブの × で閉じたら、その分は消す（「終了」・ログオフでは残す）
    // ・タブの × は、その画面だけを閉じる。ツールバーの「終了」は、アプリごと（全部の画面を）閉じる（2026-10-31）
    //   最初の画面を × で閉じたら、次の画面が最初の画面を引き継ぐ（前回のファイル・app.json の上のほうの項目）
    // ・画面は全部同じ位置・大きさに重ね、ツールバーの一段下のタブで前に出す
    // ・前に出ている画面のほかは隠す（2026-11-01）。タスクバーのボタンも Alt+Tab も1つで、切り替えはタブだけ
    // ・タブを掴んで少し動かすか、右クリック →「休止する」で、画面を閉じて覚えだけを残す（2026-11-01）。起動しても作らず、AI の会話も始めない。
    //   タブは薄く残り、押すとその覚えから画面を作り直す（ページの並び・AI の会話も戻す。WordPress の記事は読み込み直す）
    public partial class MainWindow
    {
        /// <summary>開いている画面。作った順。先頭が最初の画面。</summary>
        private static readonly List<MainWindow> OpenWindows = new List<MainWindow>();

        private static int _windowsCreated;

        /// <summary>何番目に作った画面か（1 始まり）。1 が最初の画面。</summary>
        private int _windowNumber;

        /// <summary>増やした画面の、次の起動で戻すもの（app.json の Windows の1つ）。最初の画面は null（app.json の上のほうの項目を使う）。</summary>
        private WindowSession _session;

        /// <summary>「終了」で全部の画面を閉じている最中か。このときは、閉じた画面の覚え（Windows）を消さない（次の起動で戻す）。</summary>
        private static bool _closingAll;

        /// <summary>休止するために閉じている最中か。閉じ終えたら、覚えを消さずに休止の印を付ける。</summary>
        private bool _suspending;

        /// <summary>休止したときのタブの名前。</summary>
        private string _suspendTitle;

        /// <summary>前に出ている画面（ほかの画面は隠してある）。最後に前に出た画面。</summary>
        private static MainWindow _frontWindow;

        /// <summary>画面のタブの段を最後に組んだときの中身。変わったときだけ組み直す。</summary>
        private string _windowListSignature;

        /// <summary>
        /// 最初の画面か（覚えを app.json の上のほうの項目に置く画面）。前回のファイル（LastFile）を覚えるのと、起動のときにほかの画面を作るのは、この画面だけ。
        /// 最初の画面を × で閉じると、次の画面が引き継ぐ（PromoteToPrimary）。
        /// </summary>
        private bool IsPrimary
        {
            get { return _session == null; }
        }

        /// <summary>画面を見分ける名前。AI の欄のパイプ・設定のファイル、WordPress の一時フォルダに使う（同じアプリの中の画面どうしで重ねない）。</summary>
        private string WindowKey
        {
            get { return Process.GetCurrentProcess().Id + "-" + _windowNumber; }
        }

        /// <summary>
        /// コンストラクタから。一覧に載せ、閉じたら外す。増やした画面は、次の起動で戻すものを app.json の Windows に置く
        /// （session は、起動のときに戻す画面ならその覚え。新しく増やした画面なら null で、空の覚えを作る）。
        /// </summary>
        private void RegisterWindow(WindowSession session)
        {
            _windowNumber = ++_windowsCreated;
            OpenWindows.Add(this);

            if (OpenWindows.Count > 1)
            {
                _session = session ?? new WindowSession();
                if (!_settings.Windows.Contains(_session))
                {
                    // 「＋」で増やした空の画面も、すぐ覚える（2026-10-31）。落ちても、次の起動で空の画面として戻る。
                    _settings.Windows.Add(_session);
                    _settings.Save();
                }
            }

            Activated += delegate { _frontWindow = this; };

            Closed += delegate
            {
                var wasPrimary = IsPrimary;
                var index = OpenWindows.IndexOf(this);
                OpenWindows.Remove(this);

                // 前に出ていた画面をタブの × で閉じたら、隣の画面（右、無ければ左）を出す。ほかは隠してあるので、出さないと何も見えなくなる。
                if (ReferenceEquals(_frontWindow, this))
                {
                    _frontWindow = null;
                    if (!_closingAll && OpenWindows.Count > 0)
                        OpenWindows[Math.Min(index, OpenWindows.Count - 1)].BringForward(this);
                }

                if (_suspending)
                {
                    // 休止: 覚えは消さずに印を付ける。最初の画面なら、覚えを app.json の上のほうの項目から移し、次の画面が最初の画面を引き継ぐ。
                    var kept = _session ?? new WindowSession { Screen = _settings.Screen, Collapsed = _settings.Collapsed, AiTabs = _settings.AiTabs };
                    kept.Suspended = true;
                    kept.Title = _suspendTitle;
                    if (wasPrimary && OpenWindows.Count > 0) OpenWindows[0].PromoteToPrimary();
                    if (!_settings.Windows.Contains(kept)) _settings.Windows.Add(kept);
                    _settings.Save();
                }
                // タブの × でこの画面だけを閉じたなら、次の起動では戻さない。「終了」で全部を閉じている最中なら残す。
                else if (!_closingAll)
                {
                    if (_session != null) _settings.Windows.Remove(_session);
                    else if (wasPrimary && OpenWindows.Count > 0) OpenWindows[0].PromoteToPrimary();
                    _settings.Save();
                }

                RefreshWindowLists();
            };
        }

        /// <summary>
        /// 最初の画面が × で閉じられたので、この画面が引き継ぐ。この画面の覚えを app.json の上のほうの項目へ移す
        /// （閉じた画面の覚えは捨てる）。アプリの主の窓もこの画面にする。
        /// </summary>
        private void PromoteToPrimary()
        {
            _settings.Screen = _session.Screen;
            _settings.Collapsed = _session.Collapsed;
            _settings.AiTabs = _session.AiTabs;
            _settings.Windows.Remove(_session);
            _session = null;

            _settings.LastFile = _stack.Count > 0 ? _stack[0].FilePath : null;
            if (Application.Current != null) Application.Current.MainWindow = this;
        }

        /// <summary>ツールバーの「終了」。アプリごと（全部の画面を）閉じる。最初の画面を閉じれば、ほかの画面を先に閉じる（OnClosing）。</summary>
        private void CloseAllWindows()
        {
            _closingAll = true;
            var primary = OpenWindows.FirstOrDefault(window => window.IsPrimary) ?? this;

            // 次の起動の位置・大きさは最初の画面のものを覚える。隠れていた間に前の画面を動かしたかもしれないので、前の画面に合わせておく。
            var front = FrontWindow();
            if (front != null && !ReferenceEquals(front, primary)) primary.PlaceLike(front);

            primary.CloseShowingFirst();
        }

        /// <summary>
        /// この画面を閉じる。隠れていて未保存の文書があれば、先に前に出す（2026-11-04）。
        /// 閉じている最中（Closing）の窓は Show できず、保存するかを尋ねる前に前に出そうとして落ちていた。
        /// </summary>
        private void CloseShowingFirst()
        {
            if (!IsVisible && _stack.Any(view => view.IsDirty)) BringForward(FrontWindow());
            Close();
        }

        /// <summary>次の起動で戻す、終了したときの画面（最初の画面は app.json の Screen、増やした画面は Windows の1つ）。</summary>
        private ScreenState SavedScreen
        {
            get { return _session != null ? _session.Screen : _settings.Screen; }
            set
            {
                if (_session != null) _session.Screen = value;
                else _settings.Screen = value;
            }
        }

        private List<string> SavedCollapsed
        {
            get { return _session != null ? _session.Collapsed : _settings.Collapsed; }
            set
            {
                if (_session != null) _session.Collapsed = value;
                else _settings.Collapsed = value;
            }
        }

        /// <summary>次の起動で戻す、AI の会話のタブ。</summary>
        private List<AiTabState> SavedAiTabs
        {
            get { return _session != null ? _session.AiTabs : _settings.AiTabs; }
            set
            {
                if (_session != null) _session.AiTabs = value;
                else _settings.AiTabs = value;
            }
        }

        /// <summary>最初の画面が起動したとき、前回終了したときに開いていたほかの画面を作り直す（それぞれ自分の覚えで戻す）。最後に最初の画面を前に出す。</summary>
        private void RestoreOtherWindows()
        {
            var sessions = _settings.Windows.Where(session => !session.Suspended).ToList();   // 休止している画面は作らない（タブだけ出す）
            if (sessions.Count == 0) return;

            foreach (var session in sessions)
            {
                var window = new MainWindow(_settings, this, session);
                window.Show();
            }

            // 中身を戻すのに一度出してから（Loaded）、最初の画面を前に出してほかを隠す。
            Dispatcher.BeginInvoke(new Action(delegate { BringForward(null); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>増やした画面を、元の画面と同じ位置・大きさで出す（前回の位置は使わない）。</summary>
        private void PlaceLike(Window other)
        {
            var bounds = other.WindowState == WindowState.Normal ? new Rect(other.Left, other.Top, other.Width, other.Height) : other.RestoreBounds;

            WindowStartupLocation = WindowStartupLocation.Manual;
            if (!bounds.IsEmpty)
            {
                Left = bounds.Left;
                Top = bounds.Top;
                Width = bounds.Width;
                Height = bounds.Height;
            }

            WindowState = other.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        /// <summary>画面のタブの段の「＋」。空の画面を1つ増やして前に出す。開くのは、その画面のランチャー・WordPress・新規作成から。</summary>
        private void NewWindow_Click(object sender, RoutedEventArgs e)
        {
            var window = new MainWindow(_settings, this, null);
            window.BringForward(this);
        }

        /// <summary>この画面を、今の画面と同じ位置・大きさにして前に出し、ほかの画面を隠す（先に出してから隠すので、間に何も見えない時が無い）。</summary>
        private void BringForward(MainWindow from)
        {
            // タブで隠れている画面へ切り替えるときは、今の画面の後ろで先に描かせてから入れ替える（2026-11-05）。
            if (from != null && !ReferenceEquals(from, this) && from.IsVisible && !IsVisible
                && from.WindowState != WindowState.Minimized && _stack.Count > 0)
            {
                BringForwardBehind(from);
                return;
            }

            // 切り替えの時間を perf.log に書く（2026-11-05。app.json の perfLog が true のときだけ）。タブの切り替えがもっさりする原因を探すため。
            var measuring = PerfLog.Enabled && !IsVisible;
            if (measuring) PerfLog.BeginSteps("タブ " + _windowNumber + (_stack.Count > 0 ? " " + System.IO.Path.GetFileName(_stack[0].FilePath) : string.Empty));

            var started = PerfLog.Start();
            if (from != null && !ReferenceEquals(from, this)) PlaceLike(from);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            PerfLog.Step("位置合わせ", started);

            started = PerfLog.Start();
            Show();
            PerfLog.Step("Show", started);

            started = PerfLog.Start();
            Activate();
            PerfLog.Step("Activate", started);
            _frontWindow = this;

            // ダイアログを出している画面は隠さない（起動のとき、戻している画面が「フォルダを作りますか」などを尋ねている最中に、
            // 最初の画面を前に出すことがある。隠すと、尋ねている窓が見えなくなる。2026-11-02）。後ろに重なって残るだけ。
            started = PerfLog.Start();
            foreach (var other in OpenWindows.Where(window => !ReferenceEquals(window, this) && window.IsVisible && !window.IsShowingDialog()).ToList())
                other.Hide();
            PerfLog.Step("ほかを隠す", started);

            if (measuring)
            {
                started = PerfLog.Start();
                UpdateLayout();
                PerfLog.Step("配置", started);
                FinishStepsLater();
            }
        }

        /// <summary>入れ替えを待っている切り替えの番号。新しい切り替えが来たら、待っていた分はやめる。</summary>
        private static int _switchToken;

        /// <summary>
        /// 隠れている画面を、今の画面（from）のすぐ後ろに出して描かせ、描き終わってから前に出してほかを隠す（2026-11-05）。
        /// 前は前に出してから描いたので、描いている途中（空の窓・AI の欄が遅れて出る）が見え、ちらついてもっさりして見えた
        /// （perf.log では綴の処理は 0.05〜0.3 秒で終わっていて、残りは隠していた窓を出し直す Windows の側の時間だった）。
        /// 入れ替えは、配置が済んで（Loaded の優先度）から描画を2回待ったあと。待つ間も今の画面はそのまま見えていて、操作もできる。
        /// </summary>
        private void BringForwardBehind(MainWindow from)
        {
            var token = ++_switchToken;
            var measuring = PerfLog.Enabled;
            if (measuring) PerfLog.BeginSteps("タブ(後ろで描く) " + _windowNumber + " " + System.IO.Path.GetFileName(_stack[0].FilePath));

            var started = PerfLog.Start();
            PlaceLike(from);

            var window = new WindowInteropHelper(this).EnsureHandle();
            var under = new WindowInteropHelper(from).Handle;
            PlaceBelow(window, under);

            var activate = ShowActivated;
            ShowActivated = false;   // 出すだけで前に出さない（SW_SHOWNA。重なりの順は上で決めた所のまま）
            Show();
            ShowActivated = activate;
            PlaceBelow(window, under);
            PerfLog.Step("後ろに出す", started);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                PerfLog.StepMark("配置後");
                var frames = 0;
                EventHandler rendering = null;
                rendering = delegate
                {
                    if (++frames < 2 && token == _switchToken) return;
                    System.Windows.Media.CompositionTarget.Rendering -= rendering;
                    PerfLog.StepMark("描画");

                    // 待っている間にほかの切り替えが来た・閉じた等なら、入れ替えない（隠すのは次の切り替えがやる）。
                    if (token != _switchToken || !IsVisible || !OpenWindows.Contains(this)) return;

                    var swap = PerfLog.Start();
                    Activate();
                    _frontWindow = this;
                    foreach (var other in OpenWindows.Where(each => !ReferenceEquals(each, this) && each.IsVisible && !each.IsShowingDialog()).ToList())
                        other.Hide();
                    PerfLog.Step("入れ替え", swap);

                    if (measuring)
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            PerfLog.StepMark("落ち着き");
                            PerfLog.EndSteps();
                        }), DispatcherPriority.ApplicationIdle);
                };
                System.Windows.Media.CompositionTarget.Rendering += rendering;
            }), DispatcherPriority.Loaded);
        }

        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        /// <summary>window を under のすぐ後ろに置く（前に出さない）。</summary>
        private static void PlaceBelow(IntPtr window, IntPtr under)
        {
            if (window == IntPtr.Zero || under == IntPtr.Zero) return;
            SetWindowPos(window, under, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        /// <summary>前に出ている画面。無ければ、見えている画面のどれか。</summary>
        private static MainWindow FrontWindow()
        {
            if (_frontWindow != null && OpenWindows.Contains(_frontWindow) && _frontWindow.IsVisible) return _frontWindow;
            return OpenWindows.FirstOrDefault(window => window.IsVisible);
        }

        /// <summary>
        /// 尋ねる前に呼ぶ。隠れている画面（閉じるとき・「終了」のとき）なら前に出す。どの画面の文書について尋ねているのかが見えるように、
        /// また、隠れた窓を親にしたダイアログが見えなくならないように。
        /// </summary>
        private void ShowBeforeAsking()
        {
            // 閉じている最中の窓は Show できない（落ちる）。閉じる前に前に出しておく（CloseShowingFirst）。そうでない閉じ方（タスクバーなど）では、出さずに尋ねる。
            if (!IsVisible && !_closing) BringForward(FrontWindow());
        }

        /// <summary>この画面の Closing を処理している最中。</summary>
        private bool _closing;

        /// <summary>タブの並びで、隣の画面に切り替える（-1 で前、1 で次。端で反対の端へ回る）。休止している画面は飛ばす。</summary>
        private void SwitchWindow(int step)
        {
            if (OpenWindows.Count < 2) return;

            var index = OpenWindows.IndexOf(this);
            var next = OpenWindows[((index + step) % OpenWindows.Count + OpenWindows.Count) % OpenWindows.Count];
            next.BringForward(this);
        }

        /// <summary>
        /// その .md を開いている、ほかの画面。無ければ null。
        /// 1つの文書を2つの画面で開くと、.文書名/state.json（ブックマーク・カーソル）を互いに上書きし、別々に直して食い違うため、開かずにその画面を前に出す。
        /// </summary>
        private MainWindow WindowHolding(string path)
        {
            return OpenWindows.FirstOrDefault(window => !ReferenceEquals(window, this) && window.Holds(path));
        }

        /// <summary>その WordPress の記事を開いている、ほかの画面。無ければ null。記事は一時ファイルの場所が画面ごとに違うので、サイトと番号で見る。</summary>
        private MainWindow WindowHoldingPost(string siteUrl, int id)
        {
            return OpenWindows.FirstOrDefault(window => !ReferenceEquals(window, this) && window.HoldsPost(siteUrl, id));
        }

        private bool Holds(string path)
        {
            return _stack.Any(view => SamePath(view.FilePath, path));
        }

        private bool HoldsPost(string siteUrl, int id)
        {
            return _stack.Any(view =>
            {
                var link = WordPressLinkOf(view);
                return link != null && link.Id == id && string.Equals(link.Site.Url, siteUrl, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// その .md を開いていた、休止している画面。無ければ null（2026-11-02）。休止している画面も「1つの文書は1つの画面だけ」に数える。
        /// 数えないと、休止中にほかの画面で開いたあと再開したとき、再開した画面が文書を開けず、休止の覚えが空の画面で消えていた。
        /// </summary>
        private WindowSession SuspendedHolding(string path)
        {
            return _settings.Windows.FirstOrDefault(session => session.Suspended && session.Screen != null && session.Screen.Pages != null
                && session.Screen.Pages.Any(page => page != null && !string.IsNullOrEmpty(page.File)
                                                    && SamePath(ShellActions.CanonicalPath(page.File), path)));
        }

        private WindowSession SuspendedHoldingPost(string siteUrl, int id)
        {
            return _settings.Windows.FirstOrDefault(session => session.Suspended && session.Screen != null
                && session.Screen.WordPressPost == id
                && string.Equals(session.Screen.WordPressSite, siteUrl, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// その .md をほかの画面が開いていれば、その画面を前に出して true（休止している画面なら再開する）。開く前に呼ぶ（2026-11-02。前は3か所に同じ処理があった）。
        /// </summary>
        private bool ShowHolderOf(string path)
        {
            var holder = WindowHolding(path);
            if (holder != null)
            {
                holder.BringForward(this);
                holder.Status("この文書はこの画面で開いています: " + path);
                return true;
            }

            var suspended = SuspendedHolding(path);
            if (suspended == null) return false;

            Resume(suspended);
            return true;
        }

        /// <summary>その WordPress の記事をほかの画面が開いていれば、その画面を前に出して true（休止している画面なら再開する）。</summary>
        private bool ShowHolderOfPost(string siteUrl, int id)
        {
            var holder = WindowHoldingPost(siteUrl, id);
            if (holder != null)
            {
                holder.BringForward(this);
                holder.Status("この記事はこの画面で開いています。#" + id);
                return true;
            }

            var suspended = SuspendedHoldingPost(siteUrl, id);
            if (suspended == null) return false;

            Resume(suspended);
            return true;
        }

        /// <summary>全部の画面の一覧（ツールバーの一段下）を、今の中身に合わせる（題・未保存の印が変わったとき、画面が増えた・減ったとき）。</summary>
        private static void RefreshWindowLists()
        {
            foreach (var window in OpenWindows.ToList()) window.BuildWindowList();
        }

        /// <summary>
        /// ツールバーの一段下の、画面のタブ。画面ごとのタブ（この画面は色を付ける）と、右に「＋」（新しい画面）。1つだけのときも出す（ブラウザのタブと同じ）。
        /// タブを押すとその画面を前に出し、タブの × でその画面だけを閉じる。画面が1つのときは × を出さない（アプリを閉じるのは「終了」）。
        /// 見た目は AI の欄の会話のタブにそろえる。
        /// </summary>
        private void BuildWindowList()
        {
            var labels = OpenWindows.Select(window => window.WindowLabel()).ToList();
            var suspended = _settings.Windows.Where(session => session.Suspended).ToList();
            var signature = string.Join("\n", labels) + "\n" + OpenWindows.IndexOf(this) + "\n"
                            + string.Join("\n", suspended.Select(session => session.GetHashCode() + ":" + session.Title));
            if (signature == _windowListSignature) return;

            _windowListSignature = signature;
            WindowList.Children.Clear();
            SuspendedList.Children.Clear();

            for (var i = 0; i < OpenWindows.Count; i++)
                WindowList.Children.Add(CreateWindowTab(OpenWindows[i], labels[i], OpenWindows.Count > 1));

            // 休止している画面は、段の右端に薄く並べる。
            foreach (var session in suspended) SuspendedList.Children.Add(CreateSuspendedTab(session));

            var add = new Button
            {
                Content = "＋",
                Style = (Style)FindResource("ToolButton"),
                ToolTip = "新しい画面を開く。同じ機能をもう1組使える（別の文書・別の WordPress の記事を並べて直すとき）。ここに並んだ名前を押すと切り替わる",
            };
            add.Click += NewWindow_Click;
            WindowList.Children.Add(add);
        }

        private const double WindowTabWidth = 180;

        /// <summary>画面のタブを掴んでこれだけ動かしたら、離したときに休止する（ピクセル）。</summary>
        private const double SuspendDragDistance = 16;

        private Border CreateWindowTab(MainWindow window, string label, bool closable)
        {
            var self = ReferenceEquals(window, this);

            // タブの幅は名前の長さによらず同じ。短い名前は右が空き、長い名前は「…」で切る（全部の名前はツールチップに出る）。
            var panel = new DockPanel { LastChildFill = true };
            var text = new TextBlock
            {
                Text = label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(self ? "AccentText" : "TextMuted"),
            };

            if (closable)
            {
                var close = new Button
                {
                    Content = "\uE711",
                    ToolTip = "この画面を閉じる（ほかの画面はそのまま。未保存なら保存するか尋ねる）",
                    FontFamily = IconFont,
                    FontSize = 9,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)FindResource("ToolButton"),
                };
                close.Click += delegate { window.CloseShowingFirst(); };
                DockPanel.SetDock(close, Dock.Right);
                panel.Children.Add(close);
            }
            panel.Children.Add(text);

            var tab = new Border
            {
                Child = panel,
                Width = WindowTabWidth,
                Height = 26,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 0, closable ? 3 : 10, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Background = self ? (Brush)FindResource("AccentFill") : Brushes.Transparent,
                ToolTip = window.WindowTip(),
            };

            // 押して離したら切り替える。掴んで少し動かしたら休止する（2026-11-01）。
            // 段の空いているところはウィンドウを動かすので、ここで受け止めて段に回さない（× のボタンは自分で受け取る）。
            var shift = new TranslateTransform();
            tab.RenderTransform = shift;
            Point? grabbed = null;
            var dragging = false;
            tab.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                grabbed = e.GetPosition(WindowList);
                dragging = false;
                tab.CaptureMouse();
            };
            tab.MouseMove += (sender, e) =>
            {
                if (grabbed == null || !tab.IsMouseCaptured) return;

                var moved = e.GetPosition(WindowList) - grabbed.Value;
                if (!dragging && OpenWindows.Count > 1
                    && (Math.Abs(moved.X) >= SuspendDragDistance || Math.Abs(moved.Y) >= SuspendDragDistance))
                    dragging = true;
                if (!dragging) return;

                // 掴んだタブを手に付いてこさせ、薄くする（離せば休止する印）。
                shift.X = moved.X;
                shift.Y = moved.Y;
                tab.Opacity = 0.5;
            };
            tab.MouseLeftButtonUp += (sender, e) =>
            {
                if (grabbed == null) return;
                e.Handled = true;
                grabbed = null;
                tab.ReleaseMouseCapture();

                if (!dragging)
                {
                    window.BringForward(this);
                    return;
                }

                dragging = false;
                window.Suspend();

                // 保存するかで取りやめたら、タブを元の場所に戻す。
                shift.X = shift.Y = 0;
                tab.Opacity = 1;
            };
            tab.LostMouseCapture += delegate
            {
                if (grabbed == null) return;
                grabbed = null;
                dragging = false;
                shift.X = shift.Y = 0;
                tab.Opacity = 1;
            };
            tab.MouseEnter += delegate { if (!self) tab.Background = (Brush)FindResource("HoverFill"); };
            tab.MouseLeave += delegate { if (!self) tab.Background = Brushes.Transparent; };

            // 右クリックで「休止する」。画面が1つだけなら休止できない（何も見えなくなる）。
            tab.MouseRightButtonUp += (sender, e) =>
            {
                e.Handled = true;
                var suspend = new MenuItem
                {
                    Header = "休止する",
                    ToolTip = "この画面を閉じて、状態だけを残す（AI の会話も止める）。タブは右端に薄く残り、押すと元の状態で開き直す。タブを掴んで少し動かしても休止する",
                    IsEnabled = OpenWindows.Count > 1,
                };
                suspend.Click += delegate { window.Suspend(); };
                var menu = new ContextMenu { PlacementTarget = tab, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                menu.Items.Add(suspend);
                menu.IsOpen = true;
            };
            return tab;
        }

        /// <summary>休止している画面のタブ。薄く出す。押すとその覚えから画面を作り直し、× で覚えごと捨てる。</summary>
        private Border CreateSuspendedTab(WindowSession session)
        {
            var panel = new DockPanel { LastChildFill = true };

            var close = new Button
            {
                Content = "\uE711",
                ToolTip = "休止している状態を捨てる（ファイルはそのまま。AI の会話は claude --resume で開ける）",
                FontFamily = IconFont,
                FontSize = 9,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("ToolButton"),
            };
            close.Click += delegate
            {
                _settings.Windows.Remove(session);
                _settings.Save();
                RefreshWindowLists();
            };
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(close);

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(session.Title) ? "（空）" : session.Title,
                FontStyle = FontStyles.Italic,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextMuted"),
            });

            var tab = new Border
            {
                Child = panel,
                Width = WindowTabWidth,
                Height = 26,
                Opacity = 0.6,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                ToolTip = "休止中: " + session.Title + "。押すと、休止したときの状態で開き直す（AI の会話も戻す）",
            };
            tab.MouseLeftButtonDown += (sender, e) =>
            {
                e.Handled = true;
                Resume(session);
            };
            tab.MouseEnter += delegate { tab.Background = (Brush)FindResource("HoverFill"); };
            tab.MouseLeave += delegate { tab.Background = Brushes.Transparent; };
            return tab;
        }

        /// <summary>この画面を休止する（閉じて覚えだけを残す）。未保存なら、閉じるときと同じく尋ねる。取りやめたら休止しない。</summary>
        private void Suspend()
        {
            if (OpenWindows.Count < 2) return;

            _suspendTitle = WindowLabel(false);
            _suspending = true;
            CloseShowingFirst();
            if (OpenWindows.Contains(this)) _suspending = false;   // 保存するかで取りやめた
        }

        /// <summary>休止している画面を、その覚えから作り直して前に出す。</summary>
        private void Resume(WindowSession session)
        {
            // 休止している間に、その文書・記事をほかの画面で開いていたら（2026-11-02 より前の版で開いたものなど）、再開せずにその画面を出す。
            // 再開すると文書を開けず、休止の覚えが空の画面で上書きされる。休止のタブはそのまま残す。
            var holder = OpenHolderOf(session);
            if (holder != null)
            {
                holder.BringForward(this);
                holder.Status("休止している画面の文書を、この画面で開いています。こちらを閉じてから、休止のタブを押してください。");
                return;
            }

            session.Suspended = false;
            session.Title = null;
            _settings.Save();

            var window = new MainWindow(_settings, this, session);
            window.BringForward(this);
            RefreshWindowLists();
        }

        /// <summary>休止している画面の文書・記事を開いている、開いている画面。無ければ null。</summary>
        private static MainWindow OpenHolderOf(WindowSession session)
        {
            var screen = session.Screen;
            if (screen == null) return null;

            if (screen.WordPressPost > 0)
                return OpenWindows.FirstOrDefault(window => window.HoldsPost(screen.WordPressSite, screen.WordPressPost));

            if (screen.Pages == null) return null;
            var files = screen.Pages.Where(page => page != null && !string.IsNullOrEmpty(page.File))
                                    .Select(page => ShellActions.CanonicalPath(page.File)).ToList();
            return OpenWindows.FirstOrDefault(window => files.Any(window.Holds));
        }

        /// <summary>一覧に出す名前。開いているファイル（いちばん左のページ）の題。WordPress の記事なら頭に「WP:」、未保存なら後ろに「*」。</summary>
        private string WindowLabel(bool dirtyMark = true)
        {
            if (_stack.Count == 0) return "（空）";

            var root = _stack[0];
            var label = root.Title;
            if (string.IsNullOrWhiteSpace(label)) label = System.IO.Path.GetFileNameWithoutExtension(root.FilePath);
            if (WordPressLinkOf(root) != null) label = "WP: " + label;
            if (dirtyMark && _stack.Any(view => view.IsDirty)) label += " *";
            return label;
        }

        private string WindowTip()
        {
            if (_stack.Count == 0) return "何も開いていない画面";

            var link = WordPressLinkOf(_stack[0]);
            return link != null ? "WordPress " + link.Site.DisplayName + " の記事" : _stack[0].FilePath;
        }

        /// <summary>
        /// 「終了」で最初の画面を閉じるとき、先にほかの画面を閉じる（それぞれ未保存の確かめをする）。どれかが取りやめたら false（最初の画面も閉じない）。
        /// </summary>
        private bool CloseOtherWindows()
        {
            foreach (var window in OpenWindows.Where(other => !ReferenceEquals(other, this)).ToList())
            {
                window.CloseShowingFirst();
                if (OpenWindows.Contains(window))
                {
                    CancelClosingAll();
                    window.BringForward(null);
                    return false;
                }
            }

            return true;
        }

        /// <summary>全部を閉じるのを途中でやめた。もう閉じた画面の覚えは消す（開いていない画面を次の起動で戻さない）。</summary>
        private void CancelClosingAll()
        {
            _closingAll = false;
            _settings.Windows.RemoveAll(session => !session.Suspended && !OpenWindows.Any(window => ReferenceEquals(window._session, session)));
            _settings.Save();
        }
    }
}
