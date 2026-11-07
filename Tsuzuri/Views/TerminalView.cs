using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 右の AI の欄の端末（SPEC 19章。2026-10-24）。中では PowerShell が動き、`claude` で Claude Code の画面がそのまま動く。
    ///
    /// 描くのは WebView2 の中の xterm.js（Terminal/terminal.html。VS Code の端末と同じもの）、
    /// claude.exe とは ConPTY（Views/PseudoConsole）でつなぐ。どれも正式に公開・保守されている部品で、
    /// 前に試した「コンソールの窓を入れ子にする」形のような小細工は要らない。
    ///
    /// ページの用意ができたら（ready）、その桁数と行数で、渡されたコマンド（PowerShell。最初は中で claude も起動する）を動かす。
    /// 終わったら、Enter で新しく起動し直す（そのときのコマンドは、また尋ねる。AI の欄では PowerShell だけになる）。
    /// タブを閉じるとき・エディタを閉じるときは Dispose で、中のプログラムも WebView2 も片付ける（2026-10-27）。
    /// </summary>
    public sealed class TerminalView : Border, IDisposable
    {
        /// <summary>ページを出す仮の宿の名前。exe の隣の Terminal フォルダをこの名前で見せる。</summary>
        private const string HostName = "tsuzuri.local";

        /// <summary>この宿の下だけを開く（2026-10-27）。ほかへ移ろうとしたら止める。</summary>
        private const string PageOrigin = "https://" + HostName + "/";

        private readonly Func<string> _commandLine;
        private readonly Func<string> _folder;
        private readonly WebView2 _web = new WebView2();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private PseudoConsole _console;

        /// <summary>知らせを受けている CoreWebView2。片付けるときに外す。用意できるまでと、片付けたあとは null。</summary>
        private CoreWebView2 _core;

        private bool _ready;
        private bool _initialized;
        private bool _disposed;
        private bool _webReleased;

        /// <summary>ページを描くプロセスが落ちて読み直している（次の ready で、続きを描き直させる）。</summary>
        private bool _reloading;

        private int _columns = 80;
        private int _rows = 24;

        // 出力はためて、まとめて渡す（2026-10-27）。前は 8KB ごとに画面のスレッドへ頼んでいて（優先度も普通）、
        // 出力が多いとき（長い差分など）にエディタの打鍵や描画が待たされた。
        // ためる口は読む側のスレッド、渡すのは画面のスレッド。頼みは1つだけ出しておき、普通の優先度（Normal）で動かす（QueueOutput。
        // 2026-10-28。前は打鍵より低い Background で、打った文字が出るまでの遅れに足されていた）。
        private readonly object _outputGate = new object();
        private readonly StringBuilder _output = new StringBuilder();
        private PseudoConsole _outputConsole;
        private bool _flushQueued;

        /// <param name="commandLine">起動するコマンド（exe のパスと引数。AI の欄では powershell.exe）。起動するたびに尋ねる。null を返せば起動しない。</param>
        /// <param name="folder">起動するフォルダ（AI の欄では、戻した会話のフォルダ・開いているファイルのフォルダ・利用者のフォルダなど）。起動するたびに尋ねる。</param>
        public TerminalView(Func<string> commandLine, Func<string> folder)
        {
            _commandLine = commandLine;
            _folder = folder;

            Background = ResourceBrush("PageBackground");

            // ページが出るまでの一瞬、白く光らないように、地をエディタの色にしておく。
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x10, 0x18, 0x27);

            // ファイルやリンクを落としても、ページがそれを開きに行かないようにする（2026-10-27。落とすと端末のページから離れてしまう）。
            _web.AllowExternalDrop = false;

            Child = _web;

            Loaded += delegate { Initialize(); };
        }

        /// <summary>端末のタイトルが変わった（中のプログラムが付けたもの。PowerShell・Claude Code など）。AI の欄のタブのツールチップに出す。</summary>
        public event Action<string> TitleChanged;

        /// <summary>端末を押したのにキーボードが来ていない（2026-11-08）。エディタが端末にキーボードを渡し直す。</summary>
        public event Action FocusRequested;


        public bool IsRunning
        {
            get { return _console != null && !_console.HasExited; }
        }

        /// <summary>端末にキーボードを渡す。欄を出したとき。</summary>
        public void FocusTerminal()
        {
            if (_webReleased) return;

            _web.Focus();
            if (_core != null)
            {
                try
                {
                    _core.PostWebMessageAsJson("{\"type\":\"focus\"}");
                }
                catch (Exception)
                {
                    // 表示のプロセスが落ちた直後など。
                }
            }
        }

        /// <summary>中のプログラムを終わらせる（起動し直す前）。</summary>
        public void Stop()
        {
            if (_console != null) _console.Dispose();
            _console = null;
        }

        /// <summary>
        /// タブを閉じるとき・エディタを閉じるとき（2026-10-27）。中のプログラムを終わらせ、WebView2 の知らせを外して片付ける。
        /// 前は Stop だけで、閉じたタブの WebView2（とその裏のブラウザのプロセス）が残っていた。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            TitleChanged = null;
            ReleaseWeb();
            Child = null;
        }

        private async void Initialize()
        {
            if (_initialized || _disposed) return;
            _initialized = true;

            try
            {
                // WebView2 の作業フォルダ。既定は exe の隣で、書けない場所のことがあるので、利用者のフォルダにする。
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tsuzuri", "WebView2");
                // 窓がほかの窓に覆われていても描くようにする（2026-11-05）。画面のタブの切り替えで、次の画面を今の画面の後ろで先に描かせるため。
                // 既定では覆われた窓を描かないので、前に出たあとで AI の欄が遅れて描かれた。
                // 作業フォルダが同じなら、どこで作っても同じ設定にしないと作れない（作るのはここだけ）。
                var options = new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion");
                var environment = await CoreWebView2Environment.CreateAsync(null, data, options);
                if (_disposed) return;   // 用意している間にタブを閉じた
                await _web.EnsureCoreWebView2Async(environment);
                if (_disposed) return;

                var core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;   // F5 で読み直す・Ctrl+F で探す等を止める（端末のキーにする）
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;

                // 自動入力（前に打った言葉を候補に出す）を切る（2026-10-25）。入力補完が勝手に確定して、日本語の入力がおかしくなったため。
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;

                var pages = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Terminal");
                core.SetVirtualHostNameToFolderMapping(HostName, pages, CoreWebView2HostResourceAccessKind.Allow);

                _core = core;
                core.WebMessageReceived += OnMessage;
                core.PermissionRequested += OnPermissionRequested;
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += OnNewWindowRequested;
                core.ProcessFailed += OnProcessFailed;

                core.Navigate(PageOrigin + "terminal.html");
            }
            catch (Exception ex)
            {
                if (_disposed) return;

                // WebView2 の実行環境が無い等。Windows 11 には入っている。
                ShowProblem("AI の欄を出せませんでした（WebView2 を使えません）。" + Environment.NewLine + ex.Message);
            }
        }

        /// <summary>右クリックの貼り付けでページがクリップボードを読むのを許す（自分の端末のページだけを開くので、尋ねずに許す）。</summary>
        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) e.State = CoreWebView2PermissionState.Allow;
        }

        /// <summary>
        /// 端末のページ（https://tsuzuri.local/）から離れる移動は止める（2026-10-27）。
        /// このページはクリップボードを尋ねずに読めるので、ほかのページがこの欄で開くと困る。
        /// </summary>
        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri != null && e.Uri.StartsWith(PageOrigin, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
        }

        /// <summary>新しい窓は開かない（2026-10-27）。http・https のリンクなら、いつものブラウザで開く。</summary>
        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenInBrowser(e.Uri);
        }

        /// <summary>
        /// WebView2 の裏のプロセスが落ちた（2026-10-27）。ページを描くプロセスだけなら、読み直せば戻る（中の PowerShell・claude は動いたまま）。
        /// ブラウザのプロセスごと落ちたら、この欄はもう描けないので、そう出して、タブを閉じて開き直してもらう。
        /// ほか（GPU など）は WebView2 が自分で立て直すので何もしない。
        /// </summary>
        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            if (_disposed || _webReleased) return;

            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                _ready = false;
                _reloading = true;
                try
                {
                    _core.Reload();
                    return;
                }
                catch (Exception)
                {
                    // 読み直せなければ、下と同じく止まったと出す。
                }
            }
            else if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                return;
            }

            Stop();
            ShowProblem("端末の表示（WebView2）が止まりました。中の PowerShell と Claude Code も終わらせました。" + Environment.NewLine +
                        "このタブを × で閉じて、＋ から開き直してください。");

            // 知らせの最中に WebView2 を片付けないよう、あとで片付ける。
            Dispatcher.BeginInvoke(new Action(ReleaseWeb), DispatcherPriority.Background);
        }

        /// <summary>WebView2 の知らせを外して片付ける。1回だけ。</summary>
        private void ReleaseWeb()
        {
            if (_webReleased) return;
            _webReleased = true;

            var core = _core;
            _core = null;
            if (core != null)
            {
                try
                {
                    core.WebMessageReceived -= OnMessage;
                    core.PermissionRequested -= OnPermissionRequested;
                    core.NavigationStarting -= OnNavigationStarting;
                    core.NewWindowRequested -= OnNewWindowRequested;
                    core.ProcessFailed -= OnProcessFailed;
                }
                catch (Exception)
                {
                    // 裏のプロセスが落ちたあと等。
                }
            }

            try
            {
                _web.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void ShowProblem(string text)
        {
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ResourceBrush("TextMuted"),
                Margin = new Thickness(12),
            };
        }

        /// <summary>ページからの知らせ。打った文字・大きさ・用意ができた・リンクを押した。</summary>
        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_disposed) return;

            Dictionary<string, object> message;
            try
            {
                message = _json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return;
            }

            if (message == null) return;

            object type;
            message.TryGetValue("type", out type);

            switch (type as string)
            {
                case "ready":
                    ReadSize(message);
                    _ready = true;

                    if (_reloading)
                    {
                        // 表示のプロセスが落ちて読み直したあと（2026-10-27）。中のプログラムは動いたままなので、起動し直さない。
                        // 大きさを伝え直して、描き直させる（ConPTY は大きさが変わると画面を描き直す）。
                        _reloading = false;
                        if (IsRunning)
                        {
                            _console.Resize(_columns, _rows);
                            Post("\x1b[90m[表示が落ちたので読み直しました。中の PowerShell・claude は動いたままです]\x1b[0m\r\n");
                        }
                        else
                        {
                            Post("\x1b[90m[表示が落ちたので読み直しました。Enter で新しく起動します]\x1b[0m\r\n");
                        }

                        break;
                    }

                    if (!IsRunning) StartSession();
                    break;

                case "title":
                    object title;
                    message.TryGetValue("title", out title);
                    var changed = TitleChanged;
                    if (changed != null && title is string) changed((string)title);
                    break;

                case "refocus":
                    // 端末を押したのにキーボードが来ていない（2026-11-08。terminal.html）。エディタに渡し直してもらう。
                    var refocus = FocusRequested;
                    if (refocus != null) refocus();
                    break;

                case "link":
                    // 端末の中のリンク（OSC 8）を押した（2026-10-27）。いつものブラウザで開く（http・https だけ）。
                    object url;
                    message.TryGetValue("url", out url);
                    OpenInBrowser(url as string);
                    break;

                case "resize":
                    ReadSize(message);
                    if (_console != null) _console.Resize(_columns, _rows);
                    break;

                case "input":
                    object data;
                    message.TryGetValue("data", out data);
                    var text = data as string;
                    if (text != null) SendKeys(text);
                    break;
            }
        }

        /// <summary>打ったキー（制御コードを含む）を中のプログラムに送る。</summary>
        private void SendKeys(string text)
        {
            if (_disposed || string.IsNullOrEmpty(text)) return;

            if (IsRunning) _console.Write(text);
            else if (text.Contains("\r")) StartSession();   // 終わったあとの Enter で、新しく起動する
        }

        private void ReadSize(Dictionary<string, object> message)
        {
            object value;
            if (message.TryGetValue("cols", out value) && value is int) _columns = (int)value;
            if (message.TryGetValue("rows", out value) && value is int) _rows = (int)value;
        }

        private void StartSession()
        {
            if (!_ready || _disposed) return;

            Stop();

            var folder = _folder != null ? _folder() : null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Post("\x1b[90m先にファイルを開いてから、Enter を押してください。\x1b[0m\r\n");
                return;
            }

            // 起動するコマンドは、フォルダを確かめてから尋ねる（2026-10-27）。尋ねると最初の1回だけの起動のしかた（claude を起動する・
            // 前の会話の続き）を使い切るので、前のように先に尋ねてからフォルダが無くて起動しないと、Enter で起動し直したときにただの PowerShell になった。
            var commandLine = _commandLine != null ? _commandLine() : null;
            if (commandLine == null)
            {
                Post("\x1b[31m起動するものを用意できませんでした。Enter でやり直します。\x1b[0m\r\n");
                return;
            }

            var console = new PseudoConsole();
            console.Output += text => QueueOutput(console, text);
            console.Exited += () => Dispatcher.BeginInvoke(new Action(delegate { OnExited(console); }));

            // 起動するより先に覚える。最初の出力は Start から戻る前に届くことがあり、そのときに自分のものと分かるように。
            _console = console;

            try
            {
                console.Start(commandLine, folder, _columns, _rows);
            }
            catch (Exception ex)
            {
                _console = null;
                console.Dispose();
                Post("\x1b[31m起動できませんでした: " + ex.Message + "\x1b[0m\r\n"
                     + "\x1b[90m" + commandLine + "\x1b[0m\r\n");
            }
        }

        /// <summary>出力をためる（読む側のスレッドから）。渡す頼みがまだ出ていなければ、1つ出す。</summary>
        private void QueueOutput(PseudoConsole console, string text)
        {
            lock (_outputGate)
            {
                if (!ReferenceEquals(console, _outputConsole))
                {
                    _output.Clear();   // 前に動かしていたものの残り
                    _outputConsole = console;
                }

                _output.Append(text);
                if (_flushQueued) return;
                _flushQueued = true;
            }

            // 普通の優先度で渡す（2026-10-28。前は Background で、入力や描画の後回しになり、打った文字が出るまでの遅れに足されていた）。
            // 渡す頼みは1つしか出さないので、出力が続いても、その間に届いた分はまとめて渡る。
            Dispatcher.BeginInvoke(new Action(FlushOutput), DispatcherPriority.Normal);
        }

        /// <summary>ためた出力を端末に書く（画面のスレッドで）。今動かしているものの出力だけ。</summary>
        private void FlushOutput()
        {
            string text;
            PseudoConsole console;
            lock (_outputGate)
            {
                _flushQueued = false;
                if (_output.Length == 0) return;

                text = _output.ToString();
                console = _outputConsole;
                _output.Clear();
            }

            if (ReferenceEquals(console, _console)) Post(text);
        }

        private void OnExited(PseudoConsole console)
        {
            if (!ReferenceEquals(console, _console)) return;   // 自分で終わらせたもの

            // 終わった知らせは最後の出力を読み終えてから来る（PseudoConsole）。ためている残りを先に書いてから、終わったと出す（2026-10-27）。
            FlushOutput();

            console.Dispose();
            _console = null;

            // すぐ終わったときの手がかりに、終了コードと、このアプリが何ビットで動いているかを出す。
            Post("\r\n\x1b[90m[終了しました（終了コード " + (console.ExitCode.HasValue ? console.ExitCode.Value.ToString() : "?")
                 + "、エディタは " + (Environment.Is64BitProcess ? "64" : "32") + " ビット）。Enter で新しく起動します]\x1b[0m\r\n");
        }

        /// <summary>端末に文字（制御コードを含む）を書く。</summary>
        private void Post(string text)
        {
            if (_core == null) return;

            try
            {
                _core.PostWebMessageAsString(text);
            }
            catch (Exception)
            {
                // 表示のプロセスが落ちた直後など。
            }
        }

        /// <summary>
        /// いつものブラウザで開く（2026-10-27）。http・https だけ（ほかの種類は、端末に出た文字から勝手にプログラムを動かしうるので開かない）。
        /// </summary>
        private static void OpenInBrowser(string url)
        {
            Uri uri;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 既定のブラウザが無い等。
            }
        }

        private static Brush ResourceBrush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Transparent;
        }
    }
}
