using System;
using System.Linq;
using System.Text;
using System.Windows;
using AiLink.Views;

namespace AiLink
{
    // MainWindow の一部: 会話のタブどうしの伝言（2026-11-25。利用者の判断。PORTING.md）。
    //
    // Codex には Claude Code のような会話どうしのメッセージが無いので、AiLink が仲立ちする。Enter は要らない（利用者の判断）。
    // AI が talk.ps1 -To "#1" で頼むと、AiLink は「【#2 からの伝言】…」をそのタブの受け箱に入れ、すぐ渡せるなら渡す:
    // ・相手の AI が待っている（作業の終わりのフックから 3 秒たち、利用者がそのタブで書きかけていない・3 秒キーを打っていない）:
    //   入力欄に置き、少し置いて Enter を送る（見回り AiTalkTick が 2 秒ごとに試す）
    // ・相手の AI が作業中（確かめを出している間も）: 入力欄には打たない（文字が確かめの答えのキーとして効くため。2026-11-25 のレビュー）。
    //   作業の終わりのフック（Stop）で、伝言を次の頼みとして返す（MainWindow.AiHooks.cs）
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
            target.Inbox.Add("【" + from.Name + " からの伝言】" + body);
            var delivered = DeliverAiTalk(target);
            AfterAiInboxChanged(target);
            if (target.Inbox.Count > 0) EnsureAiTalkTimer();
            if (delivered)
            {
                Status(from.Name + " から " + target.Name + " へ伝言を渡しました");
                return "ok\t" + target.Name + " に渡した（" + target.Name + " の AI が読んで取り組む。返事を頼んでいれば、伝言としてこちらに届く）。";
            }
            if (target.AgentReady && target.AutoTalks < AiAutoTalkLimit)
            {
                Status(from.Name + " から " + target.Name + " へ伝言を預けました（" + target.Name + " の手が空いたら渡します）");
                return "ok\t" + target.Name + " は作業中か、利用者が入力欄を使っているので預けた（手が空くと自動で渡る）。";
            }
            Status(from.Name + " から " + target.Name + " へ伝言が来ました（" + target.Name + " のタブの下の「伝言を渡す」で渡せます）");
            return "ok\t" + target.Name + " の受け箱に預けた（" + (target.AgentReady ? "利用者が話しかけないまま自動で渡せる回数を超えた" : target.Name + " の AI が動いていない")
                   + "。利用者が " + target.Name + " のタブで「伝言を渡す」を押すと渡る）。";
        }

        /// <summary>
        /// 受け箱の伝言を、今すぐ渡せるなら1つ渡す（入力欄に置き、少し置いて Enter）。渡せるのは、次が全部そろったときだけ:
        /// AI が会話を始めていて作業中でない・作業を終えて（会話を始めて）3 秒たった・利用者が書きかけていない・利用者が最後にキーを打って 3 秒たった・
        /// 前に打った伝言がまだ頼みとして受け付けられていない、ではない・自動で渡した回数が上限より少ない。渡したら true。
        /// 渡せなければ、見回り（AiTalkTick）がまた試す。
        /// </summary>
        private bool DeliverAiTalk(AiTab tab)
        {
            var now = DateTime.UtcNow;
            if (tab.Inbox.Count == 0 || !tab.AgentReady || tab.Busy || tab.AwaitingPrompt || tab.InputPending || !tab.View.IsRunning
                || tab.AutoTalks >= AiAutoTalkLimit || now - tab.ReadySince < TimeSpan.FromSeconds(3) || now - tab.LastUserKey < TimeSpan.FromSeconds(3))
                return false;
            // タブの PowerShell の下で AI が動いていなければ打たない（AI が落ちて会話の終わりのフックが来なかったとき、
            // 打った伝言が PowerShell のコマンドとして動いてしまうため。2026-11-25 のレビュー）
            if (!ProcessTree.HasChild(tab.View.ProcessId))
            {
                tab.AgentReady = false;
                return false;
            }

            var message = tab.Inbox[0];
            if (!tab.View.WriteInput(message)) return false;
            tab.Inbox.RemoveAt(0);
            tab.AutoTalks++;
            tab.Busy = true;   // 話しかけたフックが来るまでの間に、次の伝言を重ねて打たない
            tab.AwaitingPrompt = true;
            tab.TypedAt = now;
            AfterAiInboxChanged(tab);

            // Enter は少し置いて別に送る（一度に流し込むと、Codex が貼り付けとみなして Enter を改行にすることがあるため）
            var enter = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            enter.Tick += delegate
            {
                enter.Stop();
                if (tab.View.IsRunning) tab.View.WriteInput("\r");
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
        /// 見回り: 打った伝言が 15 秒たっても頼みとして受け付けられなければ、入力欄に残ったとみなす（作業中の印を外し、書きかけと同じに扱う。
        /// 利用者が中身を見て Enter を押せば渡る）。渡せるタブには渡す。受け箱が空になったら止まる。
        /// </summary>
        private void AiTalkTick()
        {
            var waiting = false;
            foreach (var tab in _aiTabs.ToList())
            {
                if (tab.AwaitingPrompt && DateTime.UtcNow - tab.TypedAt > TimeSpan.FromSeconds(15))
                {
                    tab.AwaitingPrompt = false;
                    tab.Busy = false;
                    tab.InputPending = true;
                }
                if (tab.Inbox.Count == 0) continue;
                DeliverAiTalk(tab);
                if (tab.Inbox.Count > 0) waiting = true;
            }
            if (!waiting && _aiTalkTimer != null) _aiTalkTimer.Stop();
        }

        /// <summary>
        /// 利用者がタブの端末に打った（Views/TerminalView の UserInput）。Enter で書きかけは無くなる。Esc・Ctrl+C は、書きかけを消すか
        /// 作業を止める（止めると作業の終わりのフックが来ないことがあるので、作業中の印も外す）。AI が待っている間の字は書きかけの印にする
        /// （作業中の字は確かめへの答えなどなので数えない）。
        /// </summary>
        private void OnAiTabInput(AiTab tab, string data)
        {
            tab.LastUserKey = DateTime.UtcNow;
            if (data == "\r")
            {
                tab.InputPending = false;
                return;
            }
            if (data == "\x1b" || data.IndexOf('\x03') >= 0)
            {
                tab.InputPending = false;
                tab.AwaitingPrompt = false;
                if (tab.Busy)
                {
                    tab.Busy = false;
                    tab.ReadySince = DateTime.UtcNow;
                }
                return;
            }
            // 作業中に打った字も書きかけとみなす（Codex は作業中も入力欄に書ける）。ただし、確かめの画面への答え（y・n・a・p・数字の1字）は数えない
            if (tab.Busy && data.Length == 1 && "ynapYNAP123456789".IndexOf(data[0]) >= 0) return;
            if (data.Any(c => c >= ' ' || c == '\b')) tab.InputPending = true;
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
                ToolTip = message + "\n\n押すと、この会話の AI に渡す（作業中なら、手が空いたときに渡す。入力欄に書きかけがあるときは渡さない）。AI どうしで自動で渡せる回数も数え直す。",
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
                if (!ProcessTree.HasChild(tab.View.ProcessId))
                {
                    Status(tab.Name + " の AI が動いていないので、渡せません（タブで codex と打って起動すると、渡します）。");
                    return;
                }
                if (tab.InputPending)
                {
                    Status(tab.Name + " の入力欄に書きかけがあるので、渡せません（送るか、Esc で消してから押してください）。");
                    return;
                }
                tab.AgentReady = true;   // AI は動いている（フックを信頼する前に始まった会話では、フックがまだ来ていないことがある）
                tab.AutoTalks = 0;   // 利用者が渡すと決めた
                tab.LastUserKey = DateTime.MinValue;
                tab.ReadySince = DateTime.MinValue;
                if (DeliverAiTalk(tab)) Status(tab.Name + " に伝言を渡しました");
                else Status(tab.Name + " は作業中なので、手が空いたら渡します");
                EnsureAiTalkTimer();
                FocusAiTerminal(tab);
            };
            AiFileLinks.Children.Add(button);
        }
    }
}
