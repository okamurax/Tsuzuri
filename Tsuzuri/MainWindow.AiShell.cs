using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: AI の欄の共通ターミナル（SPEC 19章。2026-11-08）。
    //
    // 上の段の claude と別に、利用者と Claude が一緒に使う PowerShell を下の段に出す（画面に1つ）。
    // ・画面を Claude に渡す: 話しかけるたびに、最後の 20 行と選んでいる文字を今の状態に足す。全部が要るときは Claude が
    //   terminal.ps1 -Read で頼み、覚えている全部（1 万行まで）をファイルに書き出す。パスワードなどの行は伏せる
    // ・Claude が文字を置く: terminal.ps1 -Type（-TypeFile）で頼み、Enter を付けずに入力欄に置く。Enter は利用者が押す。
    //   中のプログラムが貼り付けの印（bracketed paste）を受け付けるなら複数行をまとめて置き、受け付けないなら
    //   （RTX・PowerShell など。改行がそのまま実行になる）1行目だけを置き、残りは利用者が Enter を押すたびに1行ずつ置く
    // 頼みは今の状態と同じパイプで来る（ServeAiRequest の shell-type・shell-dump）。
    public partial class MainWindow
    {
        /// <summary>共通ターミナル。開くまでと閉じたあとは null。</summary>
        private TerminalView _aiShell;

        /// <summary>下の段を出しているか（隠しても中の PowerShell は動いたまま）。</summary>
        private bool _aiShellShown;

        /// <summary>
        /// 入力欄に文字が残っていそうか（利用者が打ちかけている・Claude が置いたまま）。Enter・Ctrl+C・Esc で空と見なす。
        /// 残っているときは Claude の文字を置かない（混ざって、思わぬコマンドになるため）。
        /// </summary>
        private bool _aiShellPending;

        /// <summary>1行ずつ置く残りの行。利用者が Enter を押し、出力が落ち着いたら次を置く。</summary>
        private readonly Queue<string> _aiShellQueue = new Queue<string>();

        /// <summary>Enter のあと、出力が落ち着くのを待っている（落ち着いたら次の行を置く。RTX はプロンプトが出る前の文字を捨てる）。</summary>
        private bool _aiShellWaitingQuiet;

        private DispatcherTimer _aiShellQuiet;

        /// <summary>タブの段の、共通ターミナルを出し入れするボタン。</summary>
        private Button _aiShellButton;

        /// <summary>話しかけられたときに読んだ共通ターミナルの画面（今の状態に書く）。読めなければ null。</summary>
        private volatile ShellScreen _aiShellScreen;

        /// <summary>毎回の今の状態に入れる行数。</summary>
        private const int AiShellTailLines = 20;

        private sealed class ShellScreen
        {
            public List<string> Lines = new List<string>();
            public string Selection = string.Empty;
            public bool Bracketed;
        }

        /// <summary>タブの段の右に置くボタン。共通ターミナルを出す・隠す。</summary>
        private Button AiShellButton()
        {
            if (_aiShellButton != null) return _aiShellButton;

            // 文字のボタンにする（2026-11-08。アイコンのフォントのコマンドプロンプトの記号は、この大きさでは潰れて読めなかった）。
            _aiShellButton = new Button
            {
                Content = "共通ターミナル",
                ToolTip = "共通ターミナルを、この欄の下に出す・隠す（利用者と Claude が一緒に使う。Claude が文字を置き、Enter は自分で押す）",
                FontSize = 12,
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
            if (style != null) _aiShellButton.Style = style;
            _aiShellButton.Click += delegate { ToggleAiShell(); };
            return _aiShellButton;
        }

        private void ToggleAiShell()
        {
            if (_aiShell != null && _aiShellShown)
            {
                _aiShellShown = false;
                ApplyAiShellRows();
                if (_aiTab != null) FocusAiTerminal(_aiTab);
                return;
            }

            if (_aiShell == null) CreateAiShell();
            _aiShellShown = true;
            ApplyAiShellRows();
            FocusAiShell();
        }

        private void CreateAiShell()
        {
            var powershell = "\"" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") + "\" -NoLogo";
            _aiShell = new TerminalView(
                () => powershell,
                () => HasDocument && WordPressLinkOf(Active) == null
                    ? Path.GetDirectoryName(Active.FilePath)
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            _aiShell.UserInput += OnAiShellInput;
            _aiShell.OutputWritten += OnAiShellOutput;
            _aiShell.FocusRequested += delegate { if (_aiShellShown) FocusAiShell(); };
            AiShellHost.Children.Add(_aiShell);
        }

        private void AiShellClose_Click(object sender, RoutedEventArgs e)
        {
            CloseAiShell();
            if (_aiTab != null && AiPane.Visibility == Visibility.Visible) FocusAiTerminal(_aiTab);
        }

        /// <summary>共通ターミナルを閉じる。中の PowerShell も終わる。エディタを閉じるときも。</summary>
        private void CloseAiShell()
        {
            if (_aiShell == null) return;

            _aiShell.Dispose();
            AiShellHost.Children.Remove(_aiShell);
            _aiShell = null;
            _aiShellShown = false;
            _aiShellPending = false;
            _aiShellScreen = null;
            CancelAiShellQueue();
            ApplyAiShellRows();
        }

        /// <summary>上下の段の高さと、ボタンの色を合わせる。</summary>
        private void ApplyAiShellRows()
        {
            var shown = _aiShell != null && _aiShellShown;
            var split = _settings.AiShellSplit;
            AiUpperRow.Height = new GridLength(shown ? split : 1, GridUnitType.Star);
            AiShellSplitterRow.Height = new GridLength(shown ? 4 : 0);
            AiShellRow.Height = shown ? new GridLength(1 - split, GridUnitType.Star) : new GridLength(0);
            AiShellSplitter.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            AiShellPane.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (_aiShellButton != null) _aiShellButton.Foreground = (Brush)FindResource(shown ? "AccentText" : "TextMuted");
        }

        /// <summary>上下の仕切りを動かしたら、上の段の割合を覚える。</summary>
        private void AiShellSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            if (e.VerticalChange == 0) return;

            var total = AiUpperRow.ActualHeight + AiShellRow.ActualHeight;
            if (total < 1) return;

            _settings.AiShellSplit = Math.Max(0.15, Math.Min(0.85, AiUpperRow.ActualHeight / total));
            _settings.Save();
            ApplyAiShellRows();
        }

        /// <summary>共通ターミナルにキーボードを渡す。上の段と同じく、受け皿を経てから（日本語の変換の位置がずれないように）。</summary>
        private void FocusAiShell()
        {
            if (_aiShell == null) return;

            Keyboard.Focus(AiFocusSink());
            var shell = _aiShell;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (ReferenceEquals(_aiShell, shell) && _aiShellShown && AiPane.Visibility == Visibility.Visible) shell.FocusTerminal();
            }), DispatcherPriority.Background);
        }

        /// <summary>
        /// 利用者が共通ターミナルに打った。Enter で、入力欄は空になったと見なし、1行ずつ置いている残りがあれば、出力が落ち着くのを待って次を置く。
        /// Esc・Ctrl+C は、残りをやめる。
        /// </summary>
        private void OnAiShellInput(string data)
        {
            if (data.IndexOf('\r') >= 0)
            {
                _aiShellPending = false;
                if (_aiShellQueue.Count > 0)
                {
                    _aiShellWaitingQuiet = true;
                    RestartAiShellQuiet();
                }
                return;
            }

            if (data == "\x1b" || data.IndexOf('\x03') >= 0)
            {
                _aiShellPending = false;
                CancelAiShellQueue();
                return;
            }

            // 矢印などのキー（ESC で始まる並び）は数えない。字・Backspace は、入力欄に何かある印にする。
            var plain = Regex.Replace(data, @"\x1b(\[[0-9;?]*[ -/]*[@-~]|O.|.)", string.Empty);
            if (plain.Any(c => c >= ' ' || c == '\b')) _aiShellPending = true;
        }

        /// <summary>出力を書いた。Enter のあとで次の行を待っているなら、落ち着くまで待ち直す。</summary>
        private void OnAiShellOutput()
        {
            if (_aiShellWaitingQuiet) RestartAiShellQuiet();
        }

        /// <summary>出力が 0.5 秒止まったら次の行を置く。</summary>
        private void RestartAiShellQuiet()
        {
            if (_aiShellQuiet == null)
            {
                _aiShellQuiet = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _aiShellQuiet.Tick += delegate
                {
                    _aiShellQuiet.Stop();
                    if (!_aiShellWaitingQuiet) return;
                    _aiShellWaitingQuiet = false;
                    PlaceNextAiShellLine();
                };
            }

            _aiShellQuiet.Stop();
            _aiShellQuiet.Start();
        }

        private void PlaceNextAiShellLine()
        {
            if (_aiShell == null || _aiShellQueue.Count == 0)
            {
                ShowAiShellStatus();
                return;
            }

            var line = _aiShellQueue.Dequeue();
            if (line.Length > 0 && _aiShell.WriteInput(line)) _aiShellPending = true;
            ShowAiShellStatus();
        }

        private void CancelAiShellQueue()
        {
            _aiShellQueue.Clear();
            _aiShellWaitingQuiet = false;
            if (_aiShellQuiet != null) _aiShellQuiet.Stop();
            ShowAiShellStatus();
        }

        /// <summary>下の段の見出しに、1行ずつ置いている残りか、Claude が置いたことを出す。</summary>
        private void ShowAiShellStatus(string text = null)
        {
            AiShellStatus.Text = _aiShellQueue.Count > 0
                ? "残り " + _aiShellQueue.Count + " 行（Enter で次・Esc でやめる）"
                : text ?? string.Empty;
        }

        /// <summary>
        /// Claude の文字を置く（画面のスレッドで。ServeAiRequest の shell-type）。Enter は付けない。
        /// 返すのは「ok」か「error」と、Claude に見せる一言（タブ区切り）。
        /// </summary>
        private string TypeIntoAiShell(string text, bool bracketed)
        {
            if (_aiShell == null || !_aiShell.IsRunning)
                return "error\t共通ターミナルが開いていない。利用者に、AI の欄の上端の「共通ターミナル」のボタンで出してもらう。";
            if (_aiShellQueue.Count > 0)
                return "error\t前に置いた行の残りがまだある（" + _aiShellQueue.Count + " 行）。利用者が Enter で進めるか、Esc でやめるまで待つ。";
            if (_aiShellPending)
                return "error\t入力欄に文字が残っている（利用者が打ちかけているか、前に置いた文字のまま）。利用者に Enter か Ctrl+C で空にしてもらう。";

            // 制御の文字は落とす（貼り付けの印の終わりや Enter を中に混ぜて、勝手に実行させないため）。最後の改行も付けない。
            var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var clean = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (c == '\n' || c == '\t' || (c >= ' ' && c != '\x7f')) clean.Append(c);
            }
            var body = clean.ToString().TrimEnd('\n');
            if (body.Trim().Length == 0) return "error\t置く文字が無い。";

            var lines = body.Split('\n');
            if (lines.Length == 1 || bracketed)
            {
                // 印を受け付けるなら、印の中の改行は実行にならない（xterm の貼り付けと同じく、改行は CR で送る）。
                // 1行なら印は要らない。タブは補完になるので、印の外では空白にする。
                var data = lines.Length == 1 && !bracketed
                    ? body.Replace("\t", "    ")
                    : "\x1b[200~" + body.Replace("\n", "\r") + "\x1b[201~";
                if (!_aiShell.WriteInput(data)) return "error\t共通ターミナルに書けなかった。";
                _aiShellPending = true;
                ShowAiShellStatus("Claude が置いた（Enter は自分で）");
                return "ok\t" + (lines.Length == 1 ? "1 行" : lines.Length + " 行をまとめて") + "置いた（Enter は押していない。押すのは利用者）。";
            }

            // 印を受け付けない（改行がそのまま実行になる）。1行目だけを置き、残りは利用者の Enter ごとに。
            foreach (var line in lines.Skip(1)) _aiShellQueue.Enqueue(line.Replace("\t", "    "));
            var first = lines[0].Replace("\t", "    ");
            if (first.Length > 0 && !_aiShell.WriteInput(first))
            {
                CancelAiShellQueue();
                return "error\t共通ターミナルに書けなかった。";
            }
            _aiShellPending = first.Length > 0;
            ShowAiShellStatus();
            return "ok\t1 行目を置いた。中のプログラムが貼り付けの印を受け付けないので、残りの " + (lines.Length - 1)
                   + " 行は、利用者が Enter を押すたびに1行ずつ置く（Esc でやめられる）。";
        }

        /// <summary>
        /// 共通ターミナルの画面を読む（パイプのスレッドから呼ぶ。画面のスレッドは空いていること）。
        /// count 行（0 なら覚えている全部）。出していない・読めなければ null。パスワードなどの行は伏せる。
        /// </summary>
        private ShellScreen ReadAiShellScreen(int count, int timeout)
        {
            Task<string> task;
            try
            {
                task = Dispatcher.Invoke(new Func<Task<string>>(() =>
                    _aiShell != null && _aiShell.IsRunning ? _aiShell.RunScriptAsync("tsuzuriScreen(" + count + ")") : null));
            }
            catch (Exception)
            {
                return null;
            }

            if (task == null) return null;
            try
            {
                if (!task.Wait(timeout)) return null;
                var value = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(task.Result) as Dictionary<string, object>;
                if (value == null) return null;

                var screen = new ShellScreen();
                object item;
                if (value.TryGetValue("lines", out item) && item is object[])
                    screen.Lines.AddRange(((object[])item).Select(line => MaskSecrets(line as string ?? string.Empty)));
                if (value.TryGetValue("selection", out item) && item is string)
                    screen.Selection = string.Join("\n", ((string)item).Replace("\r\n", "\n").Split('\n').Select(MaskSecrets));
                if (value.TryGetValue("bracketed", out item) && item is bool) screen.Bracketed = (bool)item;
                return screen;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// パスワード・事前共有鍵・community などの値を *** に伏せる（ssh-session.ps1 と同じ決まり）。
        /// RTX の show config はパスワードを * で出すが、Linux の設定ファイルなどを cat したときのため。
        /// </summary>
        private static string MaskSecrets(string line)
        {
            return Regex.Replace(line, @"(?i)\b(password|passwd|pre-shared-key|secret|community|pp auth username|pp auth myname)(\s*[=:]?\s+|\s*[=:]\s*)\S.*$", "$1$2***");
        }

        /// <summary>今の状態に足す、共通ターミナルの段（画面のスレッドで）。開いていなければ何も足さない。</summary>
        private void AppendAiShellContext(StringBuilder text)
        {
            if (_aiShell == null || !_aiShell.IsRunning) return;

            text.Append("共通ターミナル（利用者と一緒に使う PowerShell" + (_aiShellShown ? "" : "。今は隠している") + "）");
            var screen = _aiShellScreen;
            if (screen == null)
            {
                text.Append(": 画面を読めなかった。\n");
                return;
            }

            text.Append("の画面の最後の ").Append(screen.Lines.Count).Append(" 行（全部は指示のとおり -Read で）:\n```\n")
                .Append(string.Join("\n", screen.Lines)).Append("\n```\n");
            if (screen.Selection.Trim().Length > 0)
                text.Append("共通ターミナルで選んでいる文字:\n```\n").Append(screen.Selection).Append("\n```\n");
            if (_aiShellQueue.Count > 0)
                text.Append("共通ターミナルには、1行ずつ置いている残りが ").Append(_aiShellQueue.Count).Append(" 行ある。\n");
            else if (_aiShellPending)
                text.Append("共通ターミナルの入力欄には文字が残っている（Enter を押していない）。\n");
        }

        /// <summary>全部を書き出すファイル。画面ごと。エディタを閉じるときに消す。</summary>
        private string AiShellDumpPath
        {
            get { return Path.Combine(AiFolder, "terminal-" + WindowKey + ".txt"); }
        }

        /// <summary>覚えている全部をファイルに書き出す（パイプのスレッドから）。返すのは「ok→パス」か「error→一言」。</summary>
        private string DumpAiShell()
        {
            var screen = ReadAiShellScreen(0, 10000);
            if (screen == null) return "error\t共通ターミナルが開いていないか、読めなかった。";

            try
            {
                File.WriteAllText(AiShellDumpPath, string.Join("\n", screen.Lines) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                return "error\t書き出せなかった: " + ex.Message.Replace("\r", " ").Replace("\n", " ");
            }

            return "ok\t" + AiShellDumpPath + "（" + screen.Lines.Count + " 行。パスワードなどの行は伏せた）";
        }
    }
}
