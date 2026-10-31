using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 右の AI の欄（SPEC 19章。2026-10-24）。Claude Code と話すチャット。
    ///
    /// Claude Code は画面を出さずに動かし（claude -p の stream-json）、行ごとの JSON でやり取りする。
    /// 端末の画面を入れ子にするのはやめた（フォーカス・日本語入力・色で癖が続いたため）。
    /// 普通の WPF の欄なので、フォーカスも日本語入力もエディタと同じに動き、色もエディタに揃う。
    ///
    /// claude は1つ起動したまま、話しかけるたびに標準入力へ1行送る。止めたり落ちたりしたら、次に話しかけたときに
    /// 同じ会話（session_id）で起動し直す。「新しい会話」で会話を捨てる。
    /// ファイルの編集は確かめずに許し（acceptEdits）、コマンドの実行（Bash）は使わせない。
    /// それ以外で Claude Code が許可を求めたとき（\\Mac\... のようなネットワーク上の場所を読む等）は、欄の中に「許可しますか」を出す
    /// （--permission-prompt-tool stdio。許可の問い合わせが control_request で来て、control_response で答える。2026-10-24）。
    /// </summary>
    public sealed class ChatView : DockPanel
    {
        private readonly Func<string> _folder;
        private readonly Func<string> _context;
        private readonly Action _beforeSend;
        private readonly Action _filesMayHaveChanged;
        private readonly Func<string> _instructionsPath;

        private readonly StackPanel _messages = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer();
        private readonly TextBox _input = new TextBox();
        private readonly TextBlock _state = new TextBlock();
        private readonly Button _stop;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private Process _process;
        private string _sessionId;
        private string _workingFolder;
        private bool _busy;
        private readonly StringBuilder _errors = new StringBuilder();

        /// <summary>「この会話では常に許可」にした道具の名前。新しい会話で忘れる。</summary>
        private readonly HashSet<string> _alwaysAllowed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>答えを待っている許可の問い合わせの、ボタンの段。止めたら押せなくする。</summary>
        private readonly List<Panel> _pendingAsks = new List<Panel>();

        /// <summary>今書き足している返事の欄。文の塊が始まるたびに作る。</summary>
        private TextBox _reply;

        /// <param name="folder">claude を起動するフォルダ（開いているファイルのフォルダ）。起動するときに尋ねる。</param>
        /// <param name="context">話しかけるたびに添える、エディタの今の状態。</param>
        /// <param name="beforeSend">話しかける前に呼ぶ（エディタを保存して、ディスクを画面に揃える）。</param>
        /// <param name="filesMayHaveChanged">Claude が道具を使い終えたとき（ファイルを書き換えたかもしれない）。</param>
        /// <param name="instructionsPath">Claude に最初に渡す指示のファイル。起動するときに尋ねる（書けなければ null）。</param>
        public ChatView(Func<string> folder, Func<string> context, Action beforeSend, Action filesMayHaveChanged,
            Func<string> instructionsPath)
        {
            _folder = folder;
            _context = context;
            _beforeSend = beforeSend;
            _filesMayHaveChanged = filesMayHaveChanged;
            _instructionsPath = instructionsPath;

            Background = ResourceBrush("PageBackground");
            LastChildFill = true;

            // 上端の段。エディタの検索欄の段と高さ・下の線をそろえる。
            var title = new TextBlock
            {
                Text = "AI",
                FontSize = 13,
                Foreground = ResourceBrush("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0),
            };

            var fresh = CreateButton("新しい会話", "今の会話を終えて、新しく話し始める");
            fresh.Click += delegate { NewConversation(); };

            _stop = CreateButton("止める", "Claude の作業を止める。会話は残るので、続けて話しかけられる");
            _stop.Click += delegate { Interrupt(); };
            _stop.Visibility = Visibility.Collapsed;

            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(8, 0, 8, 0) };
            DockPanel.SetDock(fresh, Dock.Right);
            DockPanel.SetDock(_stop, Dock.Right);
            header.Children.Add(fresh);
            header.Children.Add(_stop);
            header.Children.Add(title);

            var headerBar = new Border
            {
                Height = 32,
                BorderBrush = ResourceBrush("BorderBrushSoft"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = header,
            };
            DockPanel.SetDock(headerBar, Dock.Top);
            Children.Add(headerBar);

            // 下端は、今の様子と入力欄。
            _state.FontSize = 12;
            _state.Foreground = ResourceBrush("TextMuted");
            _state.Margin = new Thickness(2, 0, 0, 4);
            _state.Text = "Enter で送る・Shift+Enter で改行";

            _input.AcceptsReturn = true;
            _input.TextWrapping = TextWrapping.Wrap;
            _input.MinHeight = 64;
            _input.MaxHeight = 220;
            _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _input.FontSize = 14;
            _input.Padding = new Thickness(6, 5, 6, 5);
            _input.Background = ResourceBrush("SidebarBackground");
            _input.Foreground = ResourceBrush("TextPrimary");
            _input.CaretBrush = ResourceBrush("TextPrimary");
            _input.BorderBrush = ResourceBrush("InputBorder");
            _input.BorderThickness = new Thickness(1);
            _input.PreviewKeyDown += OnInputKeyDown;
            _input.GotKeyboardFocus += delegate { _input.BorderBrush = ResourceBrush("AccentBrush"); };
            _input.LostKeyboardFocus += delegate { _input.BorderBrush = ResourceBrush("InputBorder"); };

            var bottom = new DockPanel { Margin = new Thickness(12, 6, 12, 12) };
            DockPanel.SetDock(_state, Dock.Top);
            bottom.Children.Add(_state);
            bottom.Children.Add(_input);
            DockPanel.SetDock(bottom, Dock.Bottom);
            Children.Add(bottom);

            _messages.Margin = new Thickness(12, 10, 12, 10);
            _scroll.Content = _messages;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Children.Add(_scroll);
        }

        /// <summary>入力欄にキーボードを渡す。欄を出したとき。</summary>
        public void FocusInput()
        {
            Dispatcher.BeginInvoke(new Action(delegate { _input.Focus(); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>エディタを閉じるとき。claude を終わらせる（残すと窓の無いまま動き続ける）。</summary>
        public void Stop()
        {
            Kill();
        }

        // ---------------------------------------------------------------
        // 話しかける
        // ---------------------------------------------------------------

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            // 変換中の Enter は ImeProcessed で来るので、ここには当たらない（確定の Enter で送ってしまわない）。
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

            e.Handled = true;
            Send();
        }

        private void Send()
        {
            var text = _input.Text.Trim();
            if (text.Length == 0 || _busy) return;

            if (!EnsureProcess()) return;

            // Claude が読む前に、ディスクを今の画面に揃えておく。
            if (_beforeSend != null) _beforeSend();

            var context = _context != null ? _context() : null;

            var content = new StringBuilder("[");
            if (!string.IsNullOrEmpty(context))
            {
                content.Append("{\"type\":\"text\",\"text\":").Append(Quote(
                    "（エディタの今の状態。利用者が書いた文ではない）\n" + context)).Append("},");
            }
            content.Append("{\"type\":\"text\",\"text\":").Append(Quote(text)).Append("}]");

            if (!WriteLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + content + "}}")) return;

            AddUser(text);
            _input.Clear();
            _reply = null;
            SetBusy(true, "考えています…");
        }

        /// <summary>
        /// claude の標準入力へ JSON を1行送る。ASCII 以外は \u で書き直してから送る（標準入力の文字コードに左右されないように）。
        /// 送れなければ知らせて claude を終わらせ、false。
        /// </summary>
        private bool WriteLine(string json)
        {
            var ascii = new StringBuilder(json.Length + 16);
            foreach (var c in json)
            {
                // ASCII 以外の文字は JSON の文字列の中にしか出ないので、\u にしても意味は変わらない。
                if (c > 0x7E) ascii.Append("\\u").Append(((int)c).ToString("x4"));
                else ascii.Append(c);
            }
            ascii.Append('\n');

            try
            {
                var bytes = Encoding.ASCII.GetBytes(ascii.ToString());
                _process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                _process.StandardInput.BaseStream.Flush();
                return true;
            }
            catch (Exception ex)
            {
                AddNote("送れませんでした: " + ex.Message, true);
                Kill();
                return false;
            }
        }

        private void NewConversation()
        {
            Kill();
            _sessionId = null;
            _alwaysAllowed.Clear();
            _messages.Children.Clear();
            _reply = null;
            _state.Text = "新しい会話です。Enter で送る・Shift+Enter で改行";
            FocusInput();
        }

        /// <summary>作業を止める。claude を終わらせ、次に話しかけたときに同じ会話で起動し直す。</summary>
        private void Interrupt()
        {
            Kill();
            AddNote("止めました。続けて話しかけられます。", false);
        }

        // ---------------------------------------------------------------
        // claude の起動と終わり
        // ---------------------------------------------------------------

        private bool EnsureProcess()
        {
            if (_process != null && !_process.HasExited) return true;

            var claude = FindClaude();
            if (claude == null)
            {
                AddNote("claude.exe が見つかりません。Claude Code を入れて、%USERPROFILE%\\.local\\bin か PATH の通った場所に置いてください。", true);
                return false;
            }

            // 起動したフォルダは会話の途中で変えない（同じ会話で起動し直すときも、前のフォルダで）。
            if (_workingFolder == null || _sessionId == null) _workingFolder = _folder != null ? _folder() : null;
            if (string.IsNullOrEmpty(_workingFolder) || !Directory.Exists(_workingFolder))
            {
                AddNote("先にファイルを開いてください。", true);
                return false;
            }

            var arguments = new StringBuilder();
            arguments.Append("-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages");
            arguments.Append(" --permission-mode acceptEdits --disallowedTools Bash --permission-prompt-tool stdio");

            var instructions = _instructionsPath != null ? _instructionsPath() : null;
            if (instructions != null) arguments.Append(" --append-system-prompt-file \"").Append(instructions).Append('"');
            if (_sessionId != null) arguments.Append(" --resume ").Append(_sessionId);

            var start = new ProcessStartInfo(claude, arguments.ToString());
            start.WorkingDirectory = _workingFolder;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);

            Process process;
            try
            {
                process = Process.Start(start);
            }
            catch (Exception ex)
            {
                AddNote("Claude Code を起動できませんでした: " + ex.Message, true);
                return false;
            }

            _process = process;
            _errors.Clear();

            // 出力は1行ずつ別のスレッドで読み、画面のスレッドで扱う。
            var reader = new Thread(delegate ()
            {
                try
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        var copy = line;
                        Dispatcher.BeginInvoke(new Action(delegate { OnLine(process, copy); }));
                    }
                }
                catch (Exception)
                {
                    // 終わらせたとき等。
                }

                Dispatcher.BeginInvoke(new Action(delegate { OnExited(process); }));
            });
            reader.IsBackground = true;
            reader.Start();

            var errors = new Thread(delegate ()
            {
                try
                {
                    string line;
                    while ((line = process.StandardError.ReadLine()) != null)
                    {
                        var copy = line;
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (!ReferenceEquals(process, _process)) return;
                            if (_errors.Length < 4000) _errors.AppendLine(copy);
                        }));
                    }
                }
                catch (Exception)
                {
                }
            });
            errors.IsBackground = true;
            errors.Start();

            return true;
        }

        /// <summary>入れた場所（%USERPROFILE%\.local\bin）か、PATH の通った場所の claude.exe。無ければ null。</summary>
        internal static string FindClaude()
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (File.Exists(local)) return local;

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var folder in path.Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim().Trim('"'), "claude.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                    // PATH に変な項目があっても飛ばす。
                }
            }

            return null;
        }

        private void Kill()
        {
            var process = _process;
            _process = null;

            if (process != null)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception)
                {
                }
            }

            // 答えを待っていた問い合わせは、もう答えても届かない。
            foreach (var ask in _pendingAsks) ask.IsEnabled = false;
            _pendingAsks.Clear();

            SetBusy(false, "Enter で送る・Shift+Enter で改行");
        }

        private void OnExited(Process process)
        {
            // 自分で終わらせたもの（止める・新しい会話）は知らせない。
            if (!ReferenceEquals(process, _process)) return;

            _process = null;
            var detail = _errors.ToString().Trim();
            AddNote("Claude Code が終了しました。" + (detail.Length > 0 ? Environment.NewLine + detail : string.Empty), true);
            SetBusy(false, "続けて話しかければ、同じ会話で起動し直します");
        }

        // ---------------------------------------------------------------
        // claude からの知らせ（1行に JSON が1つ）
        // ---------------------------------------------------------------

        private void OnLine(Process process, string line)
        {
            if (!ReferenceEquals(process, _process)) return;   // 終わらせたものの残り

            Dictionary<string, object> message;
            try
            {
                message = _json.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return;   // JSON でない行は読み飛ばす
            }

            if (message == null) return;

            switch (Text(message, "type"))
            {
                case "system":
                    if (Text(message, "subtype") == "init") _sessionId = Text(message, "session_id") ?? _sessionId;
                    break;

                case "stream_event":
                    OnStreamEvent(Object(message, "event"));
                    break;

                case "assistant":
                    // 文は少しずつ届いた分（stream_event）で出している。ここでは道具の使用だけを出す。
                    foreach (var block in Blocks(Object(message, "message")))
                    {
                        if (Text(block, "type") == "tool_use") AddToolUse(Text(block, "name"), Object(block, "input"));
                    }
                    break;

                case "user":
                    // 道具の結果。失敗だけを出す。書き換えたかもしれないので、エディタに読み直させる。
                    foreach (var block in Blocks(Object(message, "message")))
                    {
                        if (Text(block, "type") != "tool_result") continue;

                        if (block.ContainsKey("is_error") && Equals(block["is_error"], true))
                            AddNote(Shorten(ToolResultText(block), 300), true);
                    }

                    if (_filesMayHaveChanged != null) _filesMayHaveChanged();
                    SetBusy(true, "作業しています…");
                    break;

                case "control_request":
                    var request = Object(message, "request");
                    if (Text(request, "subtype") == "can_use_tool") OnPermissionRequest(Text(message, "request_id"), request);
                    break;

                case "result":
                    if (Equals(message.ContainsKey("is_error") ? message["is_error"] : null, true) || Text(message, "subtype") != "success")
                        AddNote("うまくいきませんでした: " + (Text(message, "result") ?? Text(message, "subtype")), true);

                    _reply = null;
                    if (_filesMayHaveChanged != null) _filesMayHaveChanged();
                    SetBusy(false, "Enter で送る・Shift+Enter で改行");
                    break;
            }
        }

        private void OnStreamEvent(Dictionary<string, object> e)
        {
            if (e == null) return;

            switch (Text(e, "type"))
            {
                case "content_block_start":
                    var block = Object(e, "content_block");
                    var kind = Text(block, "type");
                    if (kind == "text") _reply = AddReply();
                    else if (kind == "thinking") SetBusy(true, "考えています…");
                    break;

                case "content_block_delta":
                    var delta = Object(e, "delta");
                    if (Text(delta, "type") != "text_delta") return;

                    if (_reply == null) _reply = AddReply();
                    _reply.AppendText(Text(delta, "text") ?? string.Empty);
                    SetBusy(true, "書いています…");
                    FollowBottom();
                    break;
            }
        }

        // ---------------------------------------------------------------
        // 許可の問い合わせ
        // ---------------------------------------------------------------

        /// <summary>Claude が道具を使う許可を求めてきた。「常に許可」にした道具なら黙って許し、それ以外は欄に尋ねる段を出す。</summary>
        private void OnPermissionRequest(string requestId, Dictionary<string, object> request)
        {
            if (requestId == null) return;

            var tool = Text(request, "tool_name") ?? "?";
            var input = Object(request, "input") ?? new Dictionary<string, object>();

            if (_alwaysAllowed.Contains(tool))
            {
                Answer(requestId, input, true, null);
                return;
            }

            var target = Text(input, "file_path") ?? Text(input, "path") ?? Text(input, "url")
                         ?? Text(input, "command") ?? Text(input, "pattern") ?? Text(request, "description");

            var question = CreateText("Claude が「" + (Text(request, "display_name") ?? tool) + "」を使おうとしています。"
                                      + (target != null ? Environment.NewLine + target : string.Empty), ResourceBrush("TextPrimary"));
            question.FontSize = 13;

            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var allow = CreateButton("許可", "今回だけ許可する");
            var always = CreateButton("この会話では常に許可", "この会話の間は「" + tool + "」を尋ねずに許可する");
            var deny = CreateButton("断る", "許可しない。Claude には断ったと伝わる");
            allow.Style = Application.Current != null ? Application.Current.Resources["AccentButton"] as Style : allow.Style;
            buttons.Children.Add(allow);
            buttons.Children.Add(always);
            buttons.Children.Add(deny);

            var card = new StackPanel();
            card.Children.Add(question);
            card.Children.Add(buttons);

            _messages.Children.Add(new Border
            {
                Background = ResourceBrush("SidebarBackground"),
                BorderBrush = ResourceBrush("InputBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 6),
                Child = card,
            });
            _pendingAsks.Add(buttons);

            var answered = false;
            Action<bool, bool> reply = (granted, remember) =>
            {
                if (answered) return;
                answered = true;

                _pendingAsks.Remove(buttons);
                if (remember) _alwaysAllowed.Add(tool);

                buttons.Children.Clear();
                buttons.Children.Add(new TextBlock
                {
                    Text = granted ? (remember ? "この会話では常に許可します" : "許可しました") : "断りました",
                    FontSize = 12,
                    Foreground = ResourceBrush("TextMuted"),
                });

                Answer(requestId, input, granted, granted ? null : "利用者が断りました。");
                SetBusy(true, "作業しています…");
                FocusInput();
            };

            allow.Click += delegate { reply(true, false); };
            always.Click += delegate { reply(true, true); };
            deny.Click += delegate { reply(false, false); };

            _reply = null;
            SetBusy(true, "許可を待っています…");
            ScrollToBottom();
        }

        /// <summary>許可の問い合わせに答える。許すときは、道具に渡す中身をそのまま返す。</summary>
        private void Answer(string requestId, Dictionary<string, object> input, bool granted, string message)
        {
            if (_process == null) return;

            var response = granted
                ? "{\"behavior\":\"allow\",\"updatedInput\":" + _json.Serialize(input) + "}"
                : "{\"behavior\":\"deny\",\"message\":" + Quote(message ?? string.Empty) + "}";

            WriteLine("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":"
                      + Quote(requestId) + ",\"response\":" + response + "}}");
        }

        // ---------------------------------------------------------------
        // 表示
        // ---------------------------------------------------------------

        private void AddUser(string text)
        {
            var box = CreateText(text, ResourceBrush("TextPrimary"));
            var bubble = new Border
            {
                Background = ResourceBrush("AccentFill"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(32, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = box,
            };
            _messages.Children.Add(bubble);
            ScrollToBottom();
        }

        private TextBox AddReply()
        {
            var box = CreateText(string.Empty, ResourceBrush("TextPrimary"));
            box.Margin = new Thickness(0, 4, 0, 4);
            _messages.Children.Add(box);
            return box;
        }

        /// <summary>道具を使ったことを、薄い1行で出す（何のファイルを読んだ・直した等）。</summary>
        private void AddToolUse(string name, Dictionary<string, object> input)
        {
            string verb;
            switch (name)
            {
                case "Read": verb = "読む"; break;
                case "Edit":
                case "MultiEdit": verb = "直す"; break;
                case "Write": verb = "書く"; break;
                case "Glob":
                case "Grep": verb = "探す"; break;
                case "WebSearch": verb = "Web で調べる"; break;
                case "WebFetch": verb = "Web を読む"; break;
                case "TodoWrite": return;   // 作業の段取り。出しても読む人の役に立たない
                default: verb = name; break;
            }

            var target = Text(input, "file_path") ?? Text(input, "pattern") ?? Text(input, "query") ?? Text(input, "url");
            if (target != null && (name == "Read" || name == "Edit" || name == "MultiEdit" || name == "Write"))
                target = Path.GetFileName(target);

            _reply = null;   // 道具のあとの文は、別の塊として出す
            AddNote("・" + verb + (target != null ? "  " + target : string.Empty), false);
        }

        private void AddNote(string text, bool error)
        {
            var box = CreateText(text, error ? Brushes.IndianRed : ResourceBrush("TextMuted"));
            box.FontSize = 12;
            box.Margin = new Thickness(0, 2, 0, 2);
            _messages.Children.Add(box);
            ScrollToBottom();
        }

        /// <summary>文を出す欄。選んでコピーできるよう、読み取り専用の入力欄にする。</summary>
        private static TextBox CreateText(string text, Brush foreground)
        {
            return new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = foreground,
                CaretBrush = foreground,
                FontSize = 14,
                Padding = new Thickness(0),
            };
        }

        private void SetBusy(bool busy, string state)
        {
            _busy = busy;
            _state.Text = state;
            _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>下端を見ているときだけ、書き足しに合わせて下へ送る。上を読み返している最中は動かさない。</summary>
        private void FollowBottom()
        {
            if (_scroll.VerticalOffset >= _scroll.ScrollableHeight - 40) ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            Dispatcher.BeginInvoke(new Action(_scroll.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static Button CreateButton(string text, string tip)
        {
            var button = new Button { Content = text, ToolTip = tip, FontSize = 12, Padding = new Thickness(8, 1, 8, 1), Focusable = false };
            var style = Application.Current != null ? Application.Current.Resources["ToolButton"] as Style : null;
            if (style != null) button.Style = style;
            button.VerticalAlignment = VerticalAlignment.Center;
            return button;
        }

        private static Brush ResourceBrush(string key)
        {
            var brush = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            return brush ?? Brushes.Transparent;
        }

        // ---------------------------------------------------------------
        // JSON
        // ---------------------------------------------------------------

        private static string Text(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value as string : null;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        /// <summary>message.content の塊。文字列だけのときは空。</summary>
        private static IEnumerable<Dictionary<string, object>> Blocks(Dictionary<string, object> message)
        {
            object value;
            if (message == null || !message.TryGetValue("content", out value)) return Enumerable.Empty<Dictionary<string, object>>();

            var list = value as IEnumerable;
            if (list == null || value is string) return Enumerable.Empty<Dictionary<string, object>>();

            return list.OfType<Dictionary<string, object>>();
        }

        private static string ToolResultText(Dictionary<string, object> block)
        {
            object value;
            if (!block.TryGetValue("content", out value)) return string.Empty;

            var text = value as string;
            if (text != null) return text;

            var parts = (value as IEnumerable ?? new object[0]).OfType<Dictionary<string, object>>()
                .Select(part => Text(part, "text")).Where(part => part != null);
            return string.Join(Environment.NewLine, parts.ToArray());
        }

        private static string Shorten(string text, int limit)
        {
            return text.Length <= limit ? text : text.Substring(0, limit) + "…";
        }

        /// <summary>JSON の文字列にする。ASCII 以外は \u で書く（標準入力の文字コードに左右されないように）。</summary>
        private static string Quote(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
