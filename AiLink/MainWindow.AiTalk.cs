using System;
using System.Linq;
using System.Text;
using System.Windows;
using AiLink.Views;

namespace AiLink
{
    // MainWindow の一部: 会話のタブどうしの伝言（2026-11-25。利用者の判断。AiLink/SPEC.md 12章）。
    //
    // Codex には Claude Code のような会話どうしのメッセージが無いので、AiLink が仲立ちする。Enter は要らない（利用者の判断）。
    // AI が talk.ps1 -To "#1" で頼むと、AiLink は「【#2 からの伝言】…」をそのタブの受け箱に入れ、すぐ渡せるなら渡す:
    // ・相手の AI が待っている（作業の終わりのフックから 3 秒たち、利用者が 3 秒キーを打っておらず、画面に入力欄が空の印が出ている）:
    //   入力欄に置き、少し置いて Enter を送る（見回り AiTalkTick が 2 秒ごとに試す）
    // ・相手の AI が作業中（確かめを出している間も）: 入力欄には打たない（文字が確かめの答えのキーとして効くため。2026-11-25 のレビュー）。
    //   作業の終わりのフック（Stop）で、伝言を次の頼みとして返す（MainWindow.AiHooks.cs）
    // ・フックがまだ来ていないタブ（フックを信頼する前に始まった会話など）でも、画面の確かめで渡す（利用者の判断。2026-11-25）
    // ・相手の AI が動いていない・利用者が話しかけないまま自動で渡した回数が上限（AiAutoTalkLimit）に達した: 受け箱に残し、
    //   見出しに「●伝言」、欄の下に「伝言を渡す」を出す（押すと回数を 0 に戻して渡す）
    // 受け箱は保存しない（AiLink を閉じると、渡していない伝言は消える）。
    public partial class MainWindow
    {
        /// <summary>伝言の長さの上限（入力欄に置くので、長すぎるものは断る）。</summary>
        private const int AiTalkLimit = 1000;   // 長いと Codex が貼り付けとして畳み、利用者が中身を見ずに送ってしまうため

        /// <summary>利用者が話しかけないまま、伝言を自動で渡す回数の上限（タブごと。AI どうしが話し続けないように）。</summary>
        private const int AiAutoTalkLimit = 4;

        /// <summary>伝言を打ってよいかを見回る（受け箱に伝言があるタブ）。</summary>
        private System.Windows.Threading.DispatcherTimer _aiTalkTimer;

        /// <summary>talk.ps1 の頼み「talk→送るタブの番号→宛先（Base64）→伝言（Base64）」に応える（パイプのスレッドから）。</summary>
        private string ServeAiTalk(string request)
        {
            var parts = request.Split('\t');
            if (parts.Length < 4) return "error\t頼みの形が違う。";
            string to, text;
            try
            {
                to = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).Trim();
                text = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            }
            catch (FormatException)
            {
                return "error\t宛先か伝言を読めなかった。";
            }
            try
            {
                return Dispatcher.Invoke(new Func<string>(() => PlaceAiTalk(parts[1], to, text)), TimeSpan.FromSeconds(10)) as string
                       ?? "error\tAI連携ツールが今は応えない。少し待ってからやり直す。";
            }
            catch (Exception ex)
            {
                return "error\t伝言を置けなかった: " + ex.Message;
            }
        }

        /// <summary>伝言を宛先のタブの受け箱に預ける（画面のスレッドで）。答えは「ok→一言」か「error→わけ」。</summary>
        private string PlaceAiTalk(string fromId, string to, string text)
        {
            var from = _aiTabs.FirstOrDefault(tab => tab.Id == fromId);
            if (from == null) return "error\tどの会話からの伝言か分からなかった（AI連携ツールのタブの中で動かしていない）。";

            if (!to.StartsWith("#", StringComparison.Ordinal)) to = "#" + to;   // 「1」と書かれても #1 とみなす
            var target = _aiTabs.FirstOrDefault(tab => tab.Name == to);
            var names = string.Join("、", _aiTabs.Where(tab => !ReferenceEquals(tab, from)).Select(tab => tab.Name));
            if (target == null)
                return "error\t" + to + " という会話は無い（今あるほかの会話: " + (names.Length > 0 ? names : "無し") + "）。";
            if (ReferenceEquals(target, from)) return "error\t自分の会話には送れない。";

            // 1行にする（入力欄で改行を送ると、その場で送信されてしまうことがあるため）。制御の文字・改行の印は落とす
            var flat = new StringBuilder();
            foreach (var c in (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                if (c == '\n' || c == '\t' || c == '\u2028' || c == '\u2029') flat.Append(' ');
                else if (!char.IsControl(c)) flat.Append(c);
            }
            var body = flat.ToString().Trim();
            if (body.Length == 0) return "error\t伝言が空。";
            if (body.Length > AiTalkLimit) return "error\t伝言が長すぎる（" + AiTalkLimit + " 字まで）。長い中身は作業のフォルダのファイルに書き、伝言にはそのファイルの場所と頼みだけを書いて送り直す。";

            body = body.Replace("【#", "［#");   // 本文に差出人の見出しの形を書いて、ほかの会話になりすませないように
            // $ は全角の ＄ にする（2026-11-26。利用者の判断）。Codex の入力欄は $ のあとに字を打つとスキルの候補を開き、あとで送る Enter がその候補を選んで、
            // 伝言の中身が別の語に置き換わった（$a が $openai-templates:… になった）。受け取る AI には、指示で「＄ は $ のこと」と伝える
            body = body.Replace("$", "＄");
            // 入力欄が空の印と同じ文字は本文から外す（画面の確かめを釣らないように）
            if (_agent.EmptyInputMarker != null) body = body.Replace(_agent.EmptyInputMarker, "(…)");
            target.Inbox.Add("【" + from.Name + " からの伝言】" + body);
            AfterAiInboxChanged(target);
            EnsureAiTalkTimer();
            TryDeliverAiTalk(target);
            if (target.AgentStarted && target.View.IsRunning && (target.AgentReady || !target.HooksSeen) && target.AutoTalks < AiAutoTalkLimit)
            {
                Status(from.Name + " から " + target.Name + " へ伝言を預けました（" + target.Name + " の手が空いていれば、すぐ渡ります）");
                return "ok\t" + target.Name + " に預けた（" + target.Name + " の手が空いていれば、すぐに渡る。作業中なら終わったときに渡る）。";
            }
            Status(from.Name + " から " + target.Name + " へ伝言が来ました（" + target.Name + " のタブの下の「伝言を渡す」で渡せます）");
            return "ok\t" + target.Name + " の受け箱に預けた（" + (target.AutoTalks >= AiAutoTalkLimit ? "利用者が話しかけないまま自動で渡せる回数を超えた" : target.Name + " の AI が動いていない")
                   + "。利用者が " + target.Name + " のタブで「伝言を渡す」を押すと渡る）。";
        }

        /// <summary>
        /// 受け箱の伝言を、今渡せるなら1つ渡す。まず次が全部そろっているかを見る:
        /// フックが来ている・AI が会話を始めていて作業中でない・前に打った伝言の受け付けを待っていない・作業を終えて（会話を始めて）3 秒たった・
        /// 利用者が最後にキーを打って 3 秒たった・自動で渡した回数が上限より少ない・タブの PowerShell の下で AI のプロセスが動いている。
        /// そろっていれば端末の画面を読み、入力欄が空の印（IAgent.EmptyInputMarker）が出ているときだけ打つ（書きかけ・確かめの画面に打ち込まない。
        /// キーの流れから書きかけを推し量るのは、フォーカスの知らせなどが混ざって当てにならなかった。2026-11-25）。
        /// 渡せなければ、見回り（AiTalkTick）がまた試す。
        /// </summary>
        private void TryDeliverAiTalk(AiTab tab)
        {
            if (!CanDeliverAiTalk(tab)) return;
            var marker = _agent.EmptyInputMarker;
            if (marker == null) return;

            System.Threading.Tasks.Task<string> read;
            try
            {
                read = tab.View.RunScriptAsync("tsuzuriScreen(20)");
            }
            catch (Exception)
            {
                return;
            }
            if (read == null) return;
            tab.Checking = true;
            read.ContinueWith(task =>
            {
                tab.Checking = false;
                if (task.IsFaulted || task.IsCanceled || !CanDeliverAiTalk(tab)) return;
                if (!InputLineIs(task.Result, marker, true)) return;
                TypeAiTalk(tab, tab.HooksSeen);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private bool CanDeliverAiTalk(AiTab tab)
        {
            var now = DateTime.UtcNow;
            // フックがまだ来ていないタブ（フックを信頼する前に始まった会話など）でも渡す（利用者の判断。ボタンを押さずに届くように）。
            // そのときは作業中かを知れないが、画面の確かめ（入力欄が空の印が出ているか）で、確かめの画面・書きかけには打たない。
            // 打ったあとは、少なくとも 5 秒は次を打たない（受け付けの知らせが来ないので）
            // 作業の終わりのフックが来ないまま 3 分たった Busy は、画面の確かめ（作業中の表示・入力欄が空の印）に任せる
            var busy = tab.Busy && now - tab.BusySince < TimeSpan.FromMinutes(3);
            if (tab.Inbox.Count == 0 || tab.Checking || busy || tab.AwaitingPrompt || !tab.View.IsRunning || !tab.AgentStarted
                || tab.AutoTalks >= AiAutoTalkLimit || now - tab.ReadySince < TimeSpan.FromSeconds(3) || now - tab.LastUserKey < TimeSpan.FromSeconds(3)
                || now - tab.TypedAt < TimeSpan.FromSeconds(5))
                return false;
            if (tab.HooksSeen && !tab.AgentReady) return false;   // フックが来ていて、会話が終わった（SessionEnd）
            // タブの PowerShell の下で AI が動いていなければ打たない（AI が落ちて会話の終わりのフックが来なかったとき、
            // 打った伝言が PowerShell のコマンドとして動いてしまうため。2026-11-25 のレビュー）
            if (!ProcessTree.HasDescendantNamed(tab.View.ProcessId, _agent.ProcessNames))
            {
                tab.AgentReady = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 端末の画面（terminal.html の tsuzuriScreen の JSON）で、入力欄の行（IAgent.InputLinePrefix で始まる、いちばん下の行）に text があるか。
        /// idle なら加えて、作業中の表示（IAgent.WorkingMarker）がどこにも出ていないこと。画面の下のどこかに印があるだけでは良しとしない
        /// （会話の表示に残った文字や、確かめの画面の上に残った文字に釣られないため。2026-11-25 のレビュー）。
        /// </summary>
        private bool InputLineIs(string json, string text, bool idle)
        {
            try
            {
                var value = HookJson.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                object lines;
                if (value == null || !value.TryGetValue("lines", out lines) || !(lines is object[])) return false;
                var all = ((object[])lines).Select(line => line as string ?? string.Empty).ToList();
                if (idle && _agent.WorkingMarker != null && all.Any(line => line.IndexOf(_agent.WorkingMarker, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
                // 入力欄の行は、画面のいちばん下のほう（下に出るのは、モデル・フォルダなどの数行だけ）。上のほうの › の行は、会話に残った前の頼み
                var index = all.FindLastIndex(line => line.TrimStart().StartsWith(_agent.InputLinePrefix, StringComparison.Ordinal));
                if (index < 0 || all.Count - index > 6) return false;
                return all[index].IndexOf(text, StringComparison.Ordinal) >= 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 受け箱の先頭の伝言を入力欄に置き、少し置いて Enter を送る。track なら、頼みとして受け付けられるのを待つ印を付ける
        /// （フックが動いていないタブでは、受け付けの知らせが来ないので付けない）。
        /// </summary>
        private bool TypeAiTalk(AiTab tab, bool track)
        {
            if (tab.Inbox.Count == 0 || !tab.View.IsRunning) return false;
            var message = tab.Inbox[0];
            if (!tab.View.WriteInput(message)) return false;
            tab.Inbox.RemoveAt(0);
            tab.AutoTalks++;
            tab.TypedAt = DateTime.UtcNow;
            if (track)
            {
                tab.Busy = true;   // 話しかけたフックが来るまでの間に、次の伝言を重ねて打たない
                tab.AwaitingPrompt = true;
            }
            AfterAiInboxChanged(tab);
            Status(tab.Name + " に伝言を渡しました");

            // Enter は少し置いて別に送る（一度に流し込むと、Codex が貼り付けとみなして Enter を改行にすることがあるため）。
            // 送る直前に画面を読み直し、入力欄の行に打った伝言の頭が入っているときだけ送る（その間に確かめの画面が出ていれば、Enter が答えとして効くため）
            // 確かめに使うのは見出し（【#n からの伝言】）を除いた本文の頭（見出しは同じ差出人の前の伝言と同じなので、見分けにならない）
            var bodyStart = message.IndexOf('】');
            var bodyText = bodyStart >= 0 ? message.Substring(bodyStart + 1) : message;
            var head = bodyText.Length > 12 ? bodyText.Substring(0, 12) : bodyText;
            var enter = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            enter.Tick += delegate
            {
                enter.Stop();
                if (!tab.View.IsRunning) return;
                System.Threading.Tasks.Task<string> read = null;
                try
                {
                    read = tab.View.RunScriptAsync("tsuzuriScreen(20)");
                }
                catch (Exception)
                {
                }
                if (read == null) return;
                read.ContinueWith(task =>
                {
                    if (task.IsFaulted || task.IsCanceled || !tab.View.IsRunning) return;
                    // 長いと Codex が貼り付けとして畳み「[Pasted Content … chars]」と出すので、それも打った伝言とみなす
                    if (InputLineIs(task.Result, head, false) || InputLineIs(task.Result, "[Pasted Content", false)) tab.View.WriteInput("\r");
                    else Status(tab.Name + " の入力欄に伝言を置きましたが、送りませんでした（画面が変わったため。中身を見て Enter を押してください）");
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            };
            enter.Start();
            return true;
        }

        /// <summary>受け箱に伝言のあるタブがあれば、見回りを動かす（2 秒ごと）。</summary>
        private void EnsureAiTalkTimer()
        {
            if (_aiTalkTimer == null)
            {
                _aiTalkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _aiTalkTimer.Tick += delegate { AiTalkTick(); };
            }
            if (!_aiTalkTimer.IsEnabled) _aiTalkTimer.Start();
        }

        /// <summary>
        /// 見回り: 打った伝言が 15 秒たっても頼みとして受け付けられなければ、待つ印を外す（入力欄に残っていれば、画面の確かめで打たない）。
        /// 渡せるタブには渡す。受け箱が空になったら止まる。
        /// </summary>
        private void AiTalkTick()
        {
            var waiting = false;
            foreach (var tab in _aiTabs.ToList())
            {
                if (tab.AwaitingPrompt && DateTime.UtcNow - tab.TypedAt > TimeSpan.FromSeconds(15))
                {
                    // 受け付けられなかった（入力欄に残ったなど）。次は画面を読んで、入力欄が空のときだけ打つ
                    tab.AwaitingPrompt = false;
                    tab.Busy = false;
                }
                if (tab.Inbox.Count == 0) continue;
                TryDeliverAiTalk(tab);
                if (tab.Inbox.Count > 0) waiting = true;
            }
            if (!waiting && _aiTalkTimer != null) _aiTalkTimer.Stop();
        }

        /// <summary>
        /// 利用者がタブの端末に打った（Views/TerminalView の UserInput）。最後にキーを打った時刻を覚える（打っている最中には伝言を打たない）。
        /// Esc・Ctrl+C は作業を止めることがあり、そのときは作業の終わりのフックが来ないので、作業中の印も外す（次に道具を使えば立て直す）。
        /// 制御の並び（ESC で始まるもの。フォーカスの知らせ ESC [ I・ESC [ O、矢印キー、マウス）はキーに数えない。
        /// </summary>
        private void OnAiTabInput(AiTab tab, string data)
        {
            if (data != "\x1b")
            {
                data = System.Text.RegularExpressions.Regex.Replace(data, @"\x1b(\[[0-9;?<>=]*[ -/]*[@-~]|O.|.)", string.Empty);
                if (data.Length == 0) return;
            }
            tab.LastUserKey = DateTime.UtcNow;
            // 話しかけたか（2 字以上を打って Enter）を数える。フックの見張り（EnsureAiHookWatch）は、話しかけてからフックが来ないときだけ知らせる
            if (data.IndexOf('\r') >= 0)
            {
                var before = data.Substring(0, data.IndexOf('\r')).Length;
                if (tab.TypedSinceEnter + before >= 2) tab.PromptedAt = DateTime.UtcNow;
                tab.TypedSinceEnter = 0;
            }
            else if (data != "\x1b")
            {
                tab.TypedSinceEnter += data.Length;
            }
            // 利用者がこのタブで Enter を押した: 自分で話しかけたので、自動で渡した回数を数え直す（フックが来ないタブでは、話しかけたフックで数え直せないため）
            if (data == "\r" && !tab.HooksSeen) tab.AutoTalks = 0;
            if (data == "\x1b" || data.IndexOf('\x03') >= 0)
            {
                tab.AwaitingPrompt = false;
                if (tab.Busy)
                {
                    tab.Busy = false;
                    tab.ReadySince = DateTime.UtcNow;
                }
            }
        }

        /// <summary>受け箱が変わった: 見出しの印と、欄の下のボタンを合わせる。</summary>
        private void AfterAiInboxChanged(AiTab tab)
        {
            tab.Title.Text = AiTabHeaderText(tab);
            if (ReferenceEquals(tab, _aiTab)) ShowAiInbox();
        }

        /// <summary>選んでいるタブの受け箱に伝言が残っていれば、欄の下に「伝言を渡す」を出す（タブを選んだとき・受け箱が変わったとき）。</summary>
        private void ShowAiInbox()
        {
            AiFileLinks.Children.Clear();
            var tab = _aiTab;
            if (tab == null || tab.Inbox.Count == 0) return;

            var message = tab.Inbox[0];
            var button = new System.Windows.Controls.Button
            {
                Content = "伝言を渡す: " + (message.Length > 60 ? message.Substring(0, 60) + "…" : message)
                          + (tab.Inbox.Count > 1 ? "（ほかに " + (tab.Inbox.Count - 1) + " 件）" : string.Empty),
                ToolTip = message + "\n\n押すと、この会話の AI に渡す（作業中なら、手が空いたときに渡す。入力欄に書きかけがあれば、その後ろに付いて送られる）。AI どうしで自動で渡せる回数も数え直す。",
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Focusable = false,
            };
            var style = TryFindResource("ToolButton") as Style;
            if (style != null) button.Style = style;
            button.Click += delegate
            {
                if (!ReferenceEquals(_aiTab, tab) || tab.Inbox.Count == 0) return;
                if (!ProcessTree.HasDescendantNamed(tab.View.ProcessId, _agent.ProcessNames))
                {
                    Status(tab.Name + " の AI が動いていないので、渡せません（タブで codex と打って起動すると、渡します）。");
                    return;
                }
                tab.AgentReady = true;   // AI は動いている（フックを信頼する前に始まった会話では、フックがまだ来ていないことがある）
                tab.AutoTalks = 0;       // 利用者が渡すと決めた
                if (tab.HooksSeen && (tab.Busy || tab.AwaitingPrompt))
                {
                    Status(tab.Name + " は作業中なので、手が空いたら渡します");
                    EnsureAiTalkTimer();
                }
                else
                {
                    // 利用者が押したので、入力欄の確かめはしない（書きかけがあれば、その後ろに付く。ツールチップに書いた）
                    TypeAiTalk(tab, tab.HooksSeen);
                    if (!tab.HooksSeen) Status(tab.Name + " に伝言を渡しました（このタブはフックが動いていないので、自動では渡せません。Codex を開き直して「Hooks need review」で信頼すると、自動で渡るようになります）");
                }
                FocusAiTerminal(tab);
            };
            AiFileLinks.Children.Add(button);
        }
    }
}
