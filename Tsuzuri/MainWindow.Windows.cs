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
    // ・1つの文書は1つの画面だけで開く（リンクで右に開いた .md・WordPress の記事も）。.ファイル名/state.json と変更の記録を、2つの画面から書かないため
    // ・終了したときに開いていた画面は、次の起動で全部戻す（ページの並び・AI の会話のタブ。WordPress の記事は読み込み直す）。
    //   最初の画面の分は app.json の Screen・Collapsed・AiTabs、増やした画面の分は Windows に覚える。タブの × で閉じたら、その分は消す（「終了」・ログオフでは残す）
    // ・タブの × は、その画面だけを閉じる。ツールバーの「終了」は、アプリごと（全部の画面を）閉じる（2026-10-31）
    //   最初の画面を × で閉じたら、次の画面が最初の画面を引き継ぐ（前回のファイル・app.json の上のほうの項目）
    // ・画面は全部同じ位置・大きさに重ね、ツールバーの一段下のタブで前に出す
    // ・前に出ている画面のほかは見えなくする（2026-11-01。2026-11-11 から Hide でなく DWM の cloak）。タスクバーのボタンも Alt+Tab も1つで、切り替えはタブだけ
    // ・タブを掴んで横に動かすと並べ替える（2026-11-10）。休止（2026-11-01〜11-20）はやめた
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

            // 見えなくしている画面は前の画面にしない（前の画面を閉じたとき、Windows が重なりの次の窓として、見えない画面を選ぶことがある）。
            Activated += delegate { if (!_cloaked) _frontWindow = this; };

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

                // タブの × でこの画面だけを閉じたなら、次の起動では戻さない。「終了」で全部を閉じている最中なら残す。
                if (!_closingAll)
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

            _settings.LastFile = RememberableFile();
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
            if (!IsOnScreen && _stack.Any(view => view.IsDirty)) BringForward(FrontWindow(), true);
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
            // 前の版で休止していた画面（app.json の suspended。2026-11-20 に休止をやめた）も、ふつうの画面として戻す
            var sessions = _settings.Windows.ToList();
            if (sessions.Count == 0) return;

            foreach (var session in sessions)
            {
                var window = new MainWindow(_settings, this, session);
                window.ShowCloaked();
            }

            // 中身を戻すのに見えないまま出してから（Loaded）、最初の画面を前に出す。
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

        /// <summary>
        /// この画面を、今の画面と同じ位置・大きさにして前に出し、ほかの画面を見えなくする（2026-11-11）。
        /// ほかの画面は Hide せず、出したまま DWM の cloak で見えなくしておく（SetCloaked）。描いた絵が残るので、切り替えは見せる窓を替えるだけになる。
        /// 前は Hide していて、出し直すたびに Windows が窓の描き先を作り直し、最初の絵が届くまでの白い下地が一瞬見えた
        /// （後ろで描かせてから入れ替えても、WPF の描く合図は実際に窓に届く前に来るので防げなかった）。
        /// まだ出していない画面（増やした・再開した）と、位置・大きさが変わった画面は、見えないまま描かせ、描き終わってから見せる。
        /// 尋ねる前（immediate）は待たずにすぐ見せる（2026-11-15）。待つと、問いが先に別の画面の上に出ていた。白い下地が一瞬見えることがあるのは受け入れる。
        /// </summary>
        private void BringForward(MainWindow from, bool immediate = false)
        {
            var token = ++_switchToken;

            // 切り替えの時間を perf.log に書く（2026-11-05。app.json の perfLog が true のときだけ）。
            var measuring = PerfLog.Enabled && !IsOnScreen;
            if (measuring) PerfLog.BeginSteps("タブ " + _windowNumber + (_stack.Count > 0 ? " " + System.IO.Path.GetFileName(_stack[0].FilePath) : string.Empty));

            var started = PerfLog.Start();
            var before = new Rect(Left, Top, Width, Height);
            var state = WindowState;
            if (from != null && !ReferenceEquals(from, this)) PlaceLike(from);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            var moved = before != new Rect(Left, Top, Width, Height) || state != WindowState;
            PerfLog.Step("位置合わせ", started);

            var fresh = !IsVisible;
            if (fresh)
            {
                started = PerfLog.Start();
                ShowCloaked();
                PerfLog.Step("見えないまま出す", started);
                moved = true;
            }

            if (!_cloaked || !moved || immediate)
            {
                SwapIn(token, measuring);
                return;
            }

            // 描画の合図が来ない（画面のロック・全部を最小化など）と見せられないので、1 秒で諦めて見せる（2026-11-15）
            var done = false;
            var fallback = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(fresh ? 3 : 1) };
            fallback.Tick += delegate
            {
                fallback.Stop();
                if (done) return;
                done = true;
                SwapIn(token, measuring);
            };
            fallback.Start();

            // 配置が済んで（Loaded の優先度）から描画を2回待ち、DWM が絵を受け取るのを待ってから見せる。待つ間も今の画面はそのまま見えていて、操作もできる。
            // 初めて出す画面（増やした・再開した）は、支度の残り（遅らせた処理）が全部済む（ApplicationIdle）まで待ち、描画も4回待つ（2026-11-11）。
            // 2回で見せていたときは、見せたあとも 0.2〜1.1 秒ほど処理と描き直しが続き、窓全体がカクついて見えた（perf.log の「落ち着き」）。
            Dispatcher.BeginInvoke(new Action(delegate
            {
                PerfLog.StepMark(fresh ? "支度後" : "配置後");
                var frames = 0;
                var needed = fresh ? 4 : 2;
                EventHandler rendering = null;
                rendering = delegate
                {
                    if (done || token != _switchToken)
                    {
                        CompositionTarget.Rendering -= rendering;
                        return;
                    }
                    if (++frames < needed) return;
                    CompositionTarget.Rendering -= rendering;
                    PerfLog.StepMark("描画");

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (done) return;
                        try { DwmFlush(); } catch (Exception) { }
                        PerfLog.StepMark("DWM");
                        done = true;
                        fallback.Stop();
                        SwapIn(token, measuring);
                    }), DispatcherPriority.Background);
                };
                if (done) return;
                CompositionTarget.Rendering += rendering;
            }), fresh ? DispatcherPriority.ApplicationIdle : DispatcherPriority.Loaded);
        }

        /// <summary>この画面を見せて前に出し、ほかの画面を見えなくする。待っている間にほかの切り替えが来た・閉じた等なら、何もしない（次の切り替えがやる）。</summary>
        private void SwapIn(int token, bool measuring)
        {
            if (token != _switchToken || !IsVisible || !OpenWindows.Contains(this))
            {
                if (measuring) PerfLog.EndSteps();
                return;
            }

            var started = PerfLog.Start();
            SetCloaked(false);
            Activate();
            _frontWindow = this;

            // ダイアログを出している画面は見えなくしない（起動のとき、戻している画面が「フォルダを作りますか」などを尋ねている最中に、
            // 最初の画面を前に出すことがある。見えなくすると、尋ねている窓が見えなくなる。2026-11-02）。後ろに重なって残るだけ。
            // 隠しているつもりの画面も隠し直す（2026-11-17。Windows の側で隠れが外れ、全部の画面が重なって見えていたことがある）
            foreach (var other in OpenWindows.Where(each => !ReferenceEquals(each, this) && each.IsVisible && !each.IsShowingDialog()).ToList())
                other.SetCloaked(true);
            WatchCloaks();
            PerfLog.Step("入れ替え", started);

            if (measuring)
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    PerfLog.StepMark("落ち着き");
                    PerfLog.EndSteps();
                }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>入れ替えを待っている切り替えの番号。新しい切り替えが来たら、待っていた分はやめる。</summary>
        private static int _switchToken;

        /// <summary>DWM の cloak で見えなくしているか（窓は出したまま。描き続け、絵も残る）。</summary>
        private bool _cloaked;

        /// <summary>画面に見えているか（出していて、見えなくしていない）。窓の IsVisible は、見えなくしている画面でも true。</summary>
        private bool IsOnScreen
        {
            get { return IsVisible && !_cloaked; }
        }

        /// <summary>
        /// 窓を出したまま、見えなくする・見せる（DWM の cloak。Windows 8 から。仮想デスクトップと同じ仕組み）。
        /// 見えなくした画面はタスクバーのボタンも外す（ボタンと Alt+Tab は、画面がいくつあっても1つ）。効かない環境では何もしない。
        /// </summary>
        private void SetCloaked(bool cloaked)
        {
            var window = new WindowInteropHelper(this).EnsureHandle();
            var changed = _cloaked != cloaked;
            _cloaked = cloaked;

            try
            {
                var value = cloaked ? 1 : 0;
                DwmSetWindowAttribute(window, DwmaCloak, ref value, sizeof(int));
            }
            catch (Exception)
            {
            }

            if (IsVisible)
            {
                try
                {
                    var taskbar = Taskbar();
                    if (cloaked) taskbar.DeleteTab(window);
                    else taskbar.AddTab(window);
                }
                catch (Exception)
                {
                    _taskbar = null;   // Explorer が作り直されたなど。次は作り直す（2026-11-15）
                }
            }

            // 前は初めて前に出たときに戻していた AI の会話のタブ（RestoreAiTabsWhenShown）を、見せたときに戻す。
            if (changed && !cloaked && _aiRestorePending)
                Dispatcher.BeginInvoke(new Action(RestoreAiTabsWhenShown), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 見えなくしている画面がダイアログを出したら、その画面を見せる（2026-11-15）。起動のときに戻している画面が Loaded で
        /// 「フォルダを作りますか」などを尋ねると、持ち主の窓が見えないまま問いだけが浮き、閉じると見えない窓にキーボードが移った。
        /// ダイアログは持ち主の窓を使えなくする（WM_ENABLE）ので、それを合図に、この窓が持ち主の見えている窓があるかを少しの間確かめる。
        /// </summary>
        private IntPtr WatchDialogs(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmEnable || wParam != IntPtr.Zero || !_cloaked) return IntPtr.Zero;

            var tries = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += delegate
            {
                if (++tries > 40 || !_cloaked || !IsShowingDialog() || !OpenWindows.Contains(this))
                {
                    timer.Stop();
                    return;
                }
                if (!HasVisibleOwned(window)) return;

                timer.Stop();
                var front = FrontWindow();
                if (front != null && !ReferenceEquals(front, this)) PlaceLike(front);
                ++_switchToken;   // 待っている切り替えはやめる
                SetCloaked(false);
                _frontWindow = this;
                foreach (var other in OpenWindows.Where(each => !ReferenceEquals(each, this) && each.IsVisible && !each.IsShowingDialog()).ToList())
                    other.SetCloaked(true);
            };
            timer.Start();
            return IntPtr.Zero;
        }

        /// <summary>この窓が持ち主の、見えている窓（ダイアログ）があるか。</summary>
        private static bool HasVisibleOwned(IntPtr owner)
        {
            var found = false;
            EnumThreadWindows(GetCurrentThreadId(), (each, unused) =>
            {
                if (GetWindow(each, GwOwner) == owner && IsWindowVisible(each)) found = true;
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 隠しているつもりの画面が、本当に隠れているかを 2 秒ごとに確かめ、外れていたら隠し直す（2026-11-17）。
        /// Visual Studio で動かしている最中に、5 つの画面の隠れ（DWM の cloak）がどれも外れ、同じ位置に重なって全部見えていた
        /// （Parallels のコヒーレンスでは、Mac のタスクバーにも全部が並んだ）。Tsuzuri は隠したつもりのままなので、切り替えても直らなかった。
        /// 外れた原因は分かっていない（止めたときの「応答なし」の差し替え・画面の構成の変化などを疑っている）。
        /// 確かめるのは窓の数だけの軽い問い合わせ。外れていたら下端の知らせに出す（いつ起きるかを突き止めるため）。
        /// </summary>
        private static void WatchCloaks()
        {
            if (_cloakWatch != null) return;

            _cloakWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _cloakWatch.Tick += delegate
            {
                if (OpenWindows.Count == 0)
                {
                    _cloakWatch.Stop();
                    _cloakWatch = null;
                    return;
                }

                var fixedCount = 0;
                foreach (var window in OpenWindows.ToList())
                {
                    if (!window._cloaked || !window.IsVisible) continue;

                    var handle = new WindowInteropHelper(window).Handle;
                    int cloaked;
                    if (handle == IntPtr.Zero || DwmGetWindowAttribute(handle, DwmaCloaked, out cloaked, sizeof(int)) != 0) continue;
                    if (cloaked != 0) continue;

                    window.SetCloaked(true);
                    fixedCount++;
                }

                if (fixedCount == 0) return;
                var front = FrontWindow();
                if (front != null) front.Status("隠していた画面が " + fixedCount + " つ見えていたので、隠し直しました");
            };
            _cloakWatch.Start();
        }

        private static DispatcherTimer _cloakWatch;

        private const int DwmaCloaked = 14;   // DWMWA_CLOAKED（隠れているか。0 なら見えている）

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        /// <summary>窓を作ったら（SourceInitialized）呼ぶ。ダイアログの見張り（WatchDialogs）を付ける。</summary>
        private void HookWindowMessages()
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source != null) source.AddHook(WatchDialogs);
        }

        private const int WmEnable = 0x000A;
        private const uint GwOwner = 4;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>見えないまま窓を出す（前に出さない）。タスクバーのボタンは、出したあと Windows が付けるので、落ち着いてからもう一度外す。</summary>
        private void ShowCloaked()
        {
            SetCloaked(true);
            var activate = ShowActivated;
            ShowActivated = false;
            Show();
            ShowActivated = activate;
            SetCloaked(true);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_cloaked && IsVisible) SetCloaked(true);
            }), DispatcherPriority.ApplicationIdle);
        }

        private const int DwmaCloak = 13;   // DWMWA_CLOAK

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private static ITaskbarList _taskbar;

        private static ITaskbarList Taskbar()
        {
            if (_taskbar == null)
            {
                var list = (ITaskbarList)new TaskbarListObject();
                list.HrInit();
                _taskbar = list;
            }
            return _taskbar;
        }

        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr window);
            void DeleteTab(IntPtr window);
            void ActivateTab(IntPtr window);
            void SetActiveAlt(IntPtr window);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarListObject
        {
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
            if (_frontWindow != null && OpenWindows.Contains(_frontWindow) && _frontWindow.IsOnScreen) return _frontWindow;
            return OpenWindows.FirstOrDefault(window => window.IsOnScreen);
        }

        /// <summary>
        /// 尋ねる前に呼ぶ。隠れている画面（閉じるとき・「終了」のとき）なら前に出す。どの画面の文書について尋ねているのかが見えるように、
        /// また、隠れた窓を親にしたダイアログが見えなくならないように。
        /// </summary>
        private void ShowBeforeAsking()
        {
            // 閉じている最中の窓は Show できない（落ちる）。閉じる前に前に出しておく（CloseShowingFirst）。そうでない閉じ方（タスクバーなど）では、出さずに尋ねる。
            if (!IsOnScreen && !_closing) BringForward(FrontWindow(), true);
        }

        /// <summary>この画面の Closing を処理している最中。</summary>
        private bool _closing;

        /// <summary>タブの並びで、隣の画面に切り替える（-1 で前、1 で次。端で反対の端へ回る）。</summary>
        private void SwitchWindow(int step)
        {
            if (OpenWindows.Count < 2) return;

            var index = OpenWindows.IndexOf(this);
            var next = OpenWindows[((index + step) % OpenWindows.Count + OpenWindows.Count) % OpenWindows.Count];
            next.BringForward(this);
        }

        /// <summary>
        /// その .md を開いている、ほかの画面。無ければ null。
        /// 1つの文書を2つの画面で開くと、.ファイル名/state.json（ブックマーク・カーソル）を互いに上書きし、別々に直して食い違うため、開かずにその画面を前に出す。
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
        /// その .md をほかの画面が開いていれば、その画面を前に出して true。開く前に呼ぶ（2026-11-02。前は3か所に同じ処理があった）。
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
            return false;
        }

        /// <summary>その WordPress の記事をほかの画面が開いていれば、その画面を前に出して true。</summary>
        private bool ShowHolderOfPost(string siteUrl, int id)
        {
            var holder = WindowHoldingPost(siteUrl, id);
            if (holder != null)
            {
                holder.BringForward(this);
                holder.Status("この記事はこの画面で開いています。#" + id);
                return true;
            }
            return false;
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
            var signature = string.Join("\n", labels) + "\n" + OpenWindows.IndexOf(this);
            if (signature == _windowListSignature) return;

            _windowListSignature = signature;
            WindowList.Children.Clear();

            // タブの右に区切りの縦線（2026-11-10）。左右に付けると隣どうしで重なるので、右だけ。
            for (var i = 0; i < OpenWindows.Count; i++)
            {
                WindowList.Children.Add(CreateWindowTab(OpenWindows[i], labels[i], OpenWindows.Count > 1));
                WindowList.Children.Add(new Border
                {
                    Width = WindowTabSeparatorWidth,
                    Height = 16,
                    Margin = new Thickness(0, 0, WindowTabSeparatorMargin, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)FindResource("BorderBrushSoft"),
                });
            }

            WindowList.Children.Add(LauncherButton);   // タブの並びの一番右（「＋」の左。2026-11-20）
            WindowList.Children.Add(CalendarButton);   // その右にカレンダー（SPEC 22章）

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

        private Button _launcherButton;

        /// <summary>
        /// 「ランチャー」のタブ（2026-11-20。前はツールバーのボタン）。いつも画面のタブの並びの一番右（「＋」の左）に置く（BuildWindowList）。
        /// 押すとこの画面にランチャーを出し入れし、出している間は今の画面のタブと同じ色にする（MainWindow.Overlays.cs）。Alt+L でも。
        /// ランチャーの外を押すと閉じるが、このボタンは除く（CloseLauncherOnOutsideClick）。
        /// </summary>
        private Button LauncherButton
        {
            get
            {
                if (_launcherButton != null) return _launcherButton;
                // 字は出さずアイコン（4つの四角）だけで幅を狭く（2026-11-20。見た目は画面のタブにそろえる。App.xaml の LauncherTabButton）
                _launcherButton = new Button
                {
                    Content = new TextBlock { Text = "\uE8A9", FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    Style = (Style)FindResource("LauncherTabButton"),
                    Width = 34,
                    Height = 26,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "ランチャー（Alt+L）。ランチャーとファイラーを、本文の場所いっぱいに重ねて出す。もう一度押すか Esc で閉じる",
                };
                _launcherButton.Click += Launcher_Click;
                System.Windows.Input.AccessKeyManager.Register("L", _launcherButton);   // 字を出さなくなったので、Alt+L は自分で付ける
                return _launcherButton;
            }
        }

        /// <summary>画面のタブを掴んで横にこれだけ動かしたら、並べ替える（ピクセル。2026-11-10）。</summary>
        private const double ReorderDragDistance = 8;

        /// <summary>タブの右の区切り線の幅と、その右の余白（BuildWindowList）。並べ替えで、タブ1つ分の幅を出すのに使う。</summary>
        private const double WindowTabSeparatorWidth = 1;
        private const double WindowTabSeparatorMargin = 4;

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

            // 押して離したら切り替える。掴んで横に動かしたら並べ替え（2026-11-10）。
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
                if (!dragging && OpenWindows.Count > 1 && Math.Abs(moved.X) >= ReorderDragDistance)
                {
                    dragging = true;
                    Panel.SetZIndex(tab, 1);   // ほかのタブの上を通る
                }
                if (!dragging) return;

                // 掴んだタブを手に付いてこさせる（段の中で横にだけ）。
                shift.X = moved.X;
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
                Panel.SetZIndex(tab, 0);
                var steps = (int)Math.Round(shift.X / (tab.ActualWidth + tab.Margin.Right + WindowTabSeparatorWidth + WindowTabSeparatorMargin));

                // 元の場所に戻してから動かす。
                shift.X = 0;
                if (steps != 0) MoveWindowTab(window, steps);
            };
            tab.LostMouseCapture += delegate
            {
                if (grabbed == null) return;
                grabbed = null;
                dragging = false;
                Panel.SetZIndex(tab, 0);
                shift.X = 0;
            };
            tab.MouseEnter += delegate { if (!self) tab.Background = (Brush)FindResource("HoverFill"); };
            tab.MouseLeave += delegate { if (!self) tab.Background = Brushes.Transparent; };
            return tab;
        }

        /// <summary>
        /// 画面のタブを steps だけ右（負なら左）へ動かす（2026-11-10）。並びは次の起動でも同じにする（app.json の Windows の順）。
        /// 先頭になった画面が最初の画面でなければ、最初の画面の役（覚えを app.json の上のほうに置く・前回のファイル）を引き継ぐ。
        /// 起動では最初の画面が先頭に来るため。
        /// </summary>
        private static void MoveWindowTab(MainWindow window, int steps)
        {
            var from = OpenWindows.IndexOf(window);
            if (from < 0) return;
            var to = Math.Max(0, Math.Min(OpenWindows.Count - 1, from + steps));
            if (to == from) return;

            OpenWindows.RemoveAt(from);
            OpenWindows.Insert(to, window);

            var first = OpenWindows[0];
            var primary = OpenWindows.FirstOrDefault(other => other.IsPrimary);
            if (!first.IsPrimary && primary != null) first.TakePrimaryFrom(primary);

            var settings = first._settings;
            var order = OpenWindows.Where(other => other._session != null).Select(other => other._session).ToList();
            settings.Windows.RemoveAll(order.Contains);
            settings.Windows.InsertRange(0, order);
            settings.Save();

            RefreshWindowLists();
        }

        /// <summary>
        /// 並べ替えで先頭になったので、最初の画面の役を old から引き継ぐ（2026-11-10）。覚えの置き場所を入れ替える
        /// （この画面の覚えを app.json の上のほうの項目へ、old の覚えをこの画面が使っていた Windows の1つへ）。
        /// </summary>
        private void TakePrimaryFrom(MainWindow old)
        {
            old.RememberLastScreen();
            RememberLastScreen();

            var session = _session;
            var screen = session.Screen;
            var collapsed = session.Collapsed;
            var aiTabs = session.AiTabs;
            session.Screen = _settings.Screen;
            session.Collapsed = _settings.Collapsed;
            session.AiTabs = _settings.AiTabs;
            _settings.Screen = screen;
            _settings.Collapsed = collapsed;
            _settings.AiTabs = aiTabs;

            old._session = session;
            _session = null;

            _settings.LastFile = RememberableFile();
            if (Application.Current != null) Application.Current.MainWindow = this;
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
            _settings.Windows.RemoveAll(session => !OpenWindows.Any(window => ReferenceEquals(window._session, session)));
            _settings.Save();
        }
    }
}
