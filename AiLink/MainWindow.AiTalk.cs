using System;
using System.Linq;
using System.Text;
using System.Windows;

namespace AiLink
{
    // MainWindow の一部: 会話のタブどうしの伝言（2026-11-25。利用者の判断。PORTING.md）。
    //
    // Codex には Claude Code のような会話どうしのメッセージが無いので、AiLink が仲立ちする。
    // AI が talk.ps1 -To "#1" で頼むと、AiLink はその会話のタブの受け箱に「【#2 からの伝言】…」を預け、見出しに「●伝言」を出す。
    // 受け箱は保存しない（AiLink を閉じると、入力欄に置いていない伝言は消える）。入力欄にじかには置かない（相手の AI が「このコマンドを動かしてよいか」の確かめを出していると、伝言の文字が答えのキーとして効いてしまうため。
    // 2026-11-25 のレビュー）。利用者がそのタブで「伝言を入力欄に置く」を押すと入力欄に置き（Enter は押さない）、中身を見て Enter を押すと AI が読む。
    public partial class MainWindow
    {
        /// <summary>伝言の長さの上限（入力欄に置くので、長すぎるものは断る）。</summary>
        private const int AiTalkLimit = 1000;   // 長いと Codex が貼り付けとして畳み、利用者が中身を見ずに送ってしまうため

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

            target.Inbox.Add("【" + from.Name + " からの伝言】" + body);
            target.Title.Text = AiTabHeaderText(target);
            if (ReferenceEquals(target, _aiTab)) ShowAiInbox();
            Status(from.Name + " から " + target.Name + " へ伝言が来ました（" + target.Name + " のタブの下の「伝言を入力欄に置く」で入力欄に置けます）");
            return "ok\t" + target.Name + " の受け箱に置いた（まだ入力欄には置いていない。利用者が " + target.Name + " のタブで「伝言を入力欄に置く」を押し、中身を見て Enter を押すと、"
                   + target.Name + " の AI が読む）。";
        }

        /// <summary>選んでいるタブの受け箱に伝言があれば、欄の下に「伝言を入力欄に置く」を出す（タブを選んだとき・伝言が来たとき・置いたとき）。</summary>
        private void ShowAiInbox()
        {
            AiFileLinks.Children.Clear();
            var tab = _aiTab;
            if (tab == null || tab.Inbox.Count == 0) return;

            var message = tab.Inbox[0];
            var button = new System.Windows.Controls.Button
            {
                Content = "伝言を入力欄に置く: " + (message.Length > 60 ? message.Substring(0, 60) + "…" : message)
                          + (tab.Inbox.Count > 1 ? "（ほかに " + (tab.Inbox.Count - 1) + " 件）" : string.Empty),
                ToolTip = message + "\n\n押すと入力欄に置く（Enter は押さない）。中身を見てから Enter を押すと、この会話の AI が読む。\nAI が「動かしてよいか」を尋ねているときは押さない（文字が答えのキーとして効いてしまう）。",
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
                if (!tab.View.IsRunning)
                {
                    Status(tab.Name + " の端末が動いていないので、置けませんでした。");
                    return;
                }
                if (!tab.View.WriteInput(tab.Inbox[0]))
                {
                    Status(tab.Name + " の入力欄に置けませんでした。");
                    return;
                }
                tab.Inbox.RemoveAt(0);
                tab.Title.Text = AiTabHeaderText(tab);
                ShowAiInbox();
                FocusAiTerminal(tab);
            };
            AiFileLinks.Children.Add(button);
        }
    }
}
