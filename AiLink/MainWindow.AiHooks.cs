using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using AiLink.Agents;

namespace AiLink
{
    // MainWindow の一部: Codex のフック（2026-11-25。試しのフック（hook-probe）で Windows での動きを確かめてから作った）。
    //
    // フックは会話を始めるフォルダの .codex\hooks.json に置き、どれも Ai\hook.ps1 で AiLink のパイプへ中継する（「hook→イベント→タブ→Base64 の JSON」）。
    // 確かめたこと: フックは囲いの外で、利用者と同じユーザーで動き、タブの PowerShell の環境変数（AILINK_TAB・AILINK_PIPE）が見える。
    // 標準入力の JSON に session_id・turn_id・cwd・prompt・tool_name（シェルは "Bash"）・tool_input.command・last_assistant_message・source がある。
    // SessionStart・UserPromptSubmit の hookSpecificOutput.additionalContext は AI に届く（先頭が「[」の文を返すと JSON として読まれて失敗した）。
    // ・SessionStart: 会話の番号をタブに覚え（次の起動で codex resume で開き直す）、今の状態を渡す。待っている伝言があれば渡す
    // ・UserPromptSubmit: 今の状態を渡す。作業中の印を付ける。利用者が打った頼みなら、伝言を自動で渡した回数を 0 に戻す
    // ・PreToolUse: AI に渡さない場所（AiGuard）を断る
    // ・Stop: 作業が終わった。待っている伝言があれば、それを続けて渡す（decision: block の reason が次の頼みになる）。無ければ待っている印にする
    // ・SessionEnd: 会話が終わった（codex を閉じた）
    public partial class MainWindow
    {
        private static readonly JavaScriptSerializer HookJson = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>フックの設定（hooks.json）。会話を始めるたびに書き直す（中身が変わると、Codex が利用者に信頼するかを尋ね直す）。</summary>
        private static string AiHooksConfig()
        {
            var relay = Path.Combine(AiFolder, "hook.ps1");
            Func<string, string, int, string> entry = (name, matcher, timeout) =>
            {
                // exe はパスを書かず powershell（Codex はフックのコマンドを PowerShell で動かすので、"…\powershell.exe" -NoProfile は文法の誤りになった）
                var command = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + relay + "\" " + name;
                var escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return "\"" + name + "\": [{" + (matcher != null ? "\"matcher\": \"" + matcher + "\", " : string.Empty)
                       + "\"hooks\": [{\"type\": \"command\", \"command\": \"" + escaped + "\", \"timeout\": " + timeout + "}]}]";
            };
            return "{\n  \"description\": \"AiLink\",\n  \"hooks\": {\n    "
                   + string.Join(",\n    ", new[]
                   {
                       entry("SessionStart", null, 30),
                       entry("UserPromptSubmit", null, 30),
                       entry("PreToolUse", "*", 15),
                       entry("Stop", null, 30),
                       entry("SessionEnd", null, 3),
                   })
                   + "\n  }\n}\n";
        }

        /// <summary>hook.ps1 の頼みに応える（パイプのスレッドから）。答えは「ok→Base64 の標準出力」（出すものが無ければ「ok→」）。</summary>
        private string ServeAiHook(string request)
        {
            var parts = request.Split('\t');
            if (parts.Length < 4) return "ok\t";
            Dictionary<string, object> input;
            try
            {
                input = HookJson.DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]))) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return "ok\t";
            }
            if (input == null) return "ok\t";

            var eventName = parts[1];
            var tabId = parts[2];
            string output = null;
            try
            {
                if (eventName == "PreToolUse")
                {
                    output = AiHookGuard(input);   // 画面のスレッドは使わない
                }
                else
                {
                    output = Dispatcher.Invoke(new Func<string>(() => AiHookOnScreen(eventName, tabId, input)), TimeSpan.FromSeconds(10)) as string;
                }
            }
            catch (Exception)
            {
                // AI に渡さない場所の確かめは、確かめられなければ断る（通さない）
                if (eventName == "PreToolUse") output = AiGuardDeny("AI連携ツールが確かめられなかったので、この操作は断った。少し待ってからやり直す。続くなら利用者に伝える。");
            }
            // 画面のスレッドが間に合わなかったときも、作業中の印だけは付ける（付けないと、作業中のタブに伝言を打ってしまう）
            if (output == null && eventName == "UserPromptSubmit")
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var late = _aiTabs.FirstOrDefault(other => other.Id == tabId);
                    if (late == null) return;
                    late.Busy = true;
                    late.AwaitingPrompt = false;
                }));
            // 道具を使う前: AI は作業中（確かめの画面で Esc を押して Busy が外れていても、立て直す）
            if (eventName == "PreToolUse")
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var working = _aiTabs.FirstOrDefault(other => other.Id == tabId);
                    if (working == null) return;
                    working.Busy = true;
                    working.AgentReady = true;
                    if (!working.HooksSeen)
                    {
                        working.HooksSeen = true;
                        working.Header.ToolTip = AiTabToolTip(working);
                    }
                }));
            return "ok\t" + (string.IsNullOrEmpty(output) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(output)));
        }

        private static string HookString(Dictionary<string, object> input, string key)
        {
            object value;
            return input.TryGetValue(key, out value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        /// <summary>AI に渡す文（additionalContext）の答え。</summary>
        private static string HookContext(string eventName, string context)
        {
            return HookJson.Serialize(new Dictionary<string, object>
            {
                { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", eventName }, { "additionalContext", context } } },
            });
        }

        /// <summary>道具を使う前: AI に渡さない場所（AiGuard）に触れるなら断る。断る答えの JSON、よければ null。</summary>
        private string AiHookGuard(Dictionary<string, object> input)
        {
            var tool = HookString(input, "tool_name") ?? string.Empty;
            var cwd = HookString(input, "cwd") ?? string.Empty;
            var items = new List<KeyValuePair<string, string>>();
            object toolInput;
            if (input.TryGetValue("tool_input", out toolInput))
            {
                var fields = toolInput as Dictionary<string, object>;
                if (fields != null)
                {
                    foreach (var field in fields)
                    {
                        if (field.Value is string) items.Add(new KeyValuePair<string, string>(field.Key, (string)field.Value));
                    }
                }
                else if (toolInput is string)
                {
                    items.Add(new KeyValuePair<string, string>("command", (string)toolInput));
                }
            }

            // AiLink の台本は、ルール（.codex\rules\ailink.rules）で確かめなしに囲いの外で動く。その後ろに別のコマンドをつなげて、
            // 確かめなしに動かされないよう、引用符の外にコマンドをつなぐ記号があれば断る（2026-11-25 のレビュー）
            foreach (var item in items)
            {
                if (item.Key != "command" || item.Value.IndexOf(AiFolder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (ChainsCommands(item.Value))
                    return AiGuardDeny("AI連携ツールの台本（state.ps1・excel.ps1・talk.ps1）は、ほかのコマンドとつなげず、1つだけで動かす。引数は引用符で囲み、"
                                       + "引用符の外には英数字・空白・- : \\ / . _ # , = だけを書く（; | & < > ( ) { } @ $ 改行は書かない）。二重引用符の中にも $ と ` は書かない。全角の引用符（‘ ’ “ ” など）はどこにも書かない。"
                                       + "伝言に \" $ ` が入るときは -TextFile で渡す。");
            }

            var refusal = AiGuard.Check(tool, cwd, items);
            if (refusal == null) return null;
            Dispatcher.BeginInvoke(new Action(() => Status("AI が AI に渡さない場所（" + AiGuard.MarkerName + "）に触れようとしたので、断りました（" + tool + "）。")));
            return AiGuardDeny(refusal);
        }

        /// <summary>
        /// AiLink の台本のコマンドに、ほかのコマンド・式を紛れ込ませる書き方があるか（2026-11-25 のレビュー）。PowerShell は引数の中の式も動かすので、
        /// 引用符の外は許す文字（英数字・空白・- : \ / . _ # , =・全角などの ASCII でない文字）だけにする。二重引用符の中は $ と ` を許さない（式・読み替えになる）。
        /// 単引用符の中は文字のまま読まれるので、何でもよい。引用符が閉じていなければ、紛れ込ませているとみなす。
        /// </summary>
        private static bool ChainsCommands(string command)
        {
            // 全角の引用符（‘ ’ ‚ ‛ “ ” „）は、PowerShell が ' や " と同じに扱うので、どこにあっても断る（引用符の数え違いですり抜けられるため）
            if (command.IndexOfAny(new[] { '\u2018', '\u2019', '\u201A', '\u201B', '\u201C', '\u201D', '\u201E' }) >= 0) return true;
            char quote = '\0';
            foreach (var c in command)
            {
                if (quote == '\'')
                {
                    if (c == '\'') quote = '\0';
                    continue;
                }
                if (quote == '"')
                {
                    if (c == '"') quote = '\0';
                    else if (c == '$' || c == '`') return true;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }
                if (c > 127 || char.IsLetterOrDigit(c) || c == ' ' || c == '\t' || "-:\\/._#,=".IndexOf(c) >= 0) continue;
                return true;
            }
            return quote != '\0';
        }

        private static string AiGuardDeny(string reason)
        {
            return HookJson.Serialize(new Dictionary<string, object>
            {
                { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", "PreToolUse" }, { "permissionDecision", "deny" }, { "permissionDecisionReason", reason } } },
            });
        }

        /// <summary>道具を使う前のフックのほかのフック（会話の始まり・話しかけた・作業の終わり・会話の終わり。画面のスレッドで）。Codex への答え（JSON）か null。</summary>
        private string AiHookOnScreen(string eventName, string tabId, Dictionary<string, object> input)
        {
            var tab = _aiTabs.FirstOrDefault(other => other.Id == tabId);
            if (tab == null) return null;
            var session = HookString(input, "session_id");

            // どのフックが来ても、そのタブの AI は動いていて、この会話の番号だと分かる（会話の始まりのフックは、フックを信頼する前に過ぎていることがある。
            // 2026-11-25 に Windows で、フックを信頼し直したタブが「AI が動いていない」になった）
            if (!tab.HooksSeen)
            {
                tab.HooksSeen = true;
                tab.Header.ToolTip = AiTabToolTip(tab);
            }
            if (eventName != "SessionEnd")
            {
                tab.AgentReady = true;
                if (_agent.IsValidSessionId(session) && tab.SessionId != session)
                {
                    tab.SessionId = session;
                    tab.Folder = HookString(input, "cwd");
                    StoreAiTabs();
                }
            }

            switch (eventName)
            {
                case "SessionStart":
                    tab.Busy = false;
                    tab.ReadySince = DateTime.UtcNow;
                    return HookContext(eventName, AiStateText(tabId));   // 待っている伝言は、見回り（AiTalkTick）が少し置いてから渡す

                case "UserPromptSubmit":
                    tab.Busy = true;
                    tab.AwaitingPrompt = false;
                    var prompt = HookString(input, "prompt") ?? string.Empty;
                    if (prompt.IndexOf("【#", StringComparison.Ordinal) < 0) tab.AutoTalks = 0;   // 利用者が打った頼み
                    return HookContext(eventName, AiStateText(tabId));

                case "Stop":
                    // 待っている伝言があれば、続けて渡す（入力欄に打たない。Codex は reason を次の頼みとして続ける）。
                    // このときは話しかけたフックが来ないので、今の状態は添わらない（指示で state.ps1 を動かさせる）
                    if (tab.Inbox.Count > 0 && tab.AutoTalks < AiAutoTalkLimit)
                    {
                        var message = tab.Inbox[0];
                        tab.Inbox.RemoveAt(0);
                        tab.AutoTalks++;
                        AfterAiInboxChanged(tab);
                        Status(tab.Name + " に伝言を渡しました");
                        return HookJson.Serialize(new Dictionary<string, object> { { "decision", "block" }, { "reason", message } });
                    }
                    tab.Busy = false;
                    tab.ReadySince = DateTime.UtcNow;
                    return null;

                case "SessionEnd":
                    if (tab.SessionId == session && !_aiStopping)
                    {
                        tab.SessionId = null;
                        StoreAiTabs();
                    }
                    tab.Busy = false;
                    tab.AgentReady = false;
                    return null;
            }
            return null;
        }
    }
}
