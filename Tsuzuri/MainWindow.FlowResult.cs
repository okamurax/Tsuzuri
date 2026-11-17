using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;
using Tsuzuri.Markdown;
using Tsuzuri.Views;

namespace Tsuzuri
{
    // MainWindow の一部: フローの結果のカードに答えを受け取る（2026-11-17。SPEC 21章）。
    // Claude のカードを送るとき、つながる結果のカードごとに印（<<<結果 番号 合言葉>>> … <<<ここまで 番号 合言葉>>>）で囲んで書くよう頼み、
    // その会話の記録（%USERPROFILE%\.claude\projects\フォルダの名前\会話の番号.jsonl）に Claude の答えが足されるのを見て、印の間だけを取り出す。
    // 画面（ターミナルの文字）は読まない（折り返し・色の制御・描き直しで崩れるため）。記録は Claude Code が書く JSON の行で、答えの文字がそのまま入っている。
    // 見るのは送った時点より後に足された行の、Claude の答え（type: assistant の text）だけ（頼んだ文には印がそのまま入っているので、それを拾わないため）。
    public partial class MainWindow
    {
        /// <summary>答えを待っている送り（送るたびに1つ）。</summary>
        private readonly List<FlowWatch> _flowWatches = new List<FlowWatch>();

        /// <summary>答えを待つ上限（長い作業もあるので長め）。</summary>
        private static readonly TimeSpan FlowWatchLimit = TimeSpan.FromHours(3);

        /// <summary>
        /// 次に話しかけたときのフック（UserPromptSubmit）で Claude に渡す、結果を印で囲む頼み。会話の番号ごと。
        /// フックは HTTP のスレッドから読むので、lock で守る。
        /// </summary>
        private readonly Dictionary<string, string> _flowPrompts = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>会話の次の話しかけに付ける頼みを置く（null で消す）。</summary>
        private void SetFlowPrompt(string session, string instruction)
        {
            if (session == null) return;
            lock (_flowPrompts)
            {
                if (instruction == null) _flowPrompts.Remove(session);
                else _flowPrompts[session] = instruction;
            }
        }

        /// <summary>この会話に渡す頼みがあれば取り出す（1回だけ渡す）。無ければ null。HTTP のスレッドから呼ぶ。</summary>
        private string TakeFlowPrompt(string session)
        {
            if (session == null) return null;
            lock (_flowPrompts)
            {
                string instruction;
                if (!_flowPrompts.TryGetValue(session, out instruction)) return null;
                _flowPrompts.Remove(session);
                return instruction;
            }
        }

        private sealed class FlowWatch
        {
            public string Key;
            public PageDocument Document;
            public string Page;
            public AiTab Tab;
            public string Session;
            public string Transcript;
            public long Offset;
            public readonly StringBuilder Answer = new StringBuilder();
            public List<FlowWant> Wants;
            public DateTime Started;
            public DispatcherTimer Timer;
        }

        /// <summary>送る直前に、会話の記録の今の長さを覚える（それより後に足された答えだけを見る）。会話の番号が分からなければ null。</summary>
        private FlowWatch PrepareFlowWatch(FlowView view, AiTab tab, List<FlowWant> wants)
        {
            if (tab.SessionId == null)
            {
                view.ShowStatus("会話の番号が分からないので、結果は受け取れません（送るだけ送ります）");
                return null;
            }

            // 新しい会話を待つ間にフローを閉じることもあるので、文書は見出し（ファイル#ページ）から探す
            var page = view.FlowKey.Substring(view.FlowKey.LastIndexOf('#') + 1);
            var document = _stack.Select(each => each.Owner)
                                 .FirstOrDefault(owner => owner != null && owner.FilePath + "#" + page == view.FlowKey);
            var watch = new FlowWatch
            {
                Key = view.FlowKey,
                Document = document,
                Page = page,
                Tab = tab,
                Session = tab.SessionId,
                Wants = wants,
                Started = DateTime.UtcNow,
            };
            watch.Transcript = FindTranscript(tab);
            watch.Offset = LengthOf(watch.Transcript);
            return watch;
        }

        private void StartFlowWatch(FlowView view, FlowWatch watch)
        {
            // 同じ結果のカードを待っている前の送りは、もう待たない（送り直した）
            foreach (var other in _flowWatches.ToList())
            {
                if (other.Key != watch.Key) continue;
                other.Wants.RemoveAll(want => watch.Wants.Any(mine => mine.Title == want.Title));
                if (other.Wants.Count == 0) StopFlowWatch(other);
            }

            foreach (var want in watch.Wants) FlowView.SetWaiting(watch.Key, want.Title, true);
            view.RefreshWaiting();

            _flowWatches.Add(watch);
            watch.Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            watch.Timer.Tick += delegate { PollFlowWatch(watch); };
            watch.Timer.Start();
        }

        private void StopFlowWatch(FlowWatch watch)
        {
            if (watch.Timer != null) watch.Timer.Stop();
            _flowWatches.Remove(watch);
            foreach (var want in watch.Wants) FlowView.SetWaiting(watch.Key, want.Title, false);
            if (_flowView != null && _flowView.FlowKey == watch.Key) _flowView.RefreshWaiting();
        }

        private void PollFlowWatch(FlowWatch watch)
        {
            if (!_aiTabs.Contains(watch.Tab) || watch.Tab.View == null || !watch.Tab.View.IsRunning)
            {
                Status("フローの結果を待っていた会話（" + watch.Tab.Name + "）が閉じたので、待つのをやめました");
                StopFlowWatch(watch);
                return;
            }
            if (DateTime.UtcNow - watch.Started > FlowWatchLimit)
            {
                Status("フローの結果が " + FlowWatchLimit.TotalHours + " 時間届かなかったので、待つのをやめました");
                StopFlowWatch(watch);
                return;
            }

            // /clear などで会話が変わったら、新しい会話の記録を頭から見る
            if (watch.Tab.SessionId != null && watch.Tab.SessionId != watch.Session)
            {
                watch.Session = watch.Tab.SessionId;
                watch.Transcript = null;
                watch.Offset = 0;
            }
            if (watch.Transcript == null)
            {
                watch.Transcript = FindTranscript(watch.Tab);
                if (watch.Transcript == null) return;   // 最初に話しかけるまで、claude は記録を作らない
            }

            if (!ReadNewAnswers(watch)) return;

            var answer = watch.Answer.ToString();
            foreach (var want in watch.Wants.ToList())
            {
                var value = FlowResults.Extract(answer, want.Start, want.End);
                if (value == null) continue;
                watch.Wants.Remove(want);
                FlowView.SetWaiting(watch.Key, want.Title, false);
                DeliverFlowResult(watch, want.Title, value);
            }
            if (watch.Wants.Count == 0) StopFlowWatch(watch);
        }

        /// <summary>覚えた位置より後に足された行を読み、Claude の答えの文字を足す。新しい答えがあれば true。書きかけの行は次に読む。</summary>
        private static bool ReadNewAnswers(FlowWatch watch)
        {
            byte[] bytes;
            try
            {
                using (var stream = new FileStream(watch.Transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < watch.Offset) watch.Offset = 0;   // 作り直された
                    if (stream.Length == watch.Offset) return false;
                    stream.Seek(watch.Offset, SeekOrigin.Begin);
                    bytes = new byte[Math.Min(stream.Length - watch.Offset, 64L * 1024 * 1024)];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var count = stream.Read(bytes, read, bytes.Length - read);
                        if (count <= 0) break;
                        read += count;
                    }
                    if (read < bytes.Length) Array.Resize(ref bytes, read);
                }
            }
            catch (Exception)
            {
                return false;
            }

            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0) return false;
            watch.Offset += end + 1;

            var found = false;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
            {
                var text = AnswerTextOf(serializer, line);
                if (text == null) continue;
                watch.Answer.Append(text).Append('\n');
                found = true;
            }
            return found;
        }

        /// <summary>記録の1行が Claude の答え（type: assistant。下請けの会話は除く）なら、その文字。違えば null。</summary>
        internal static string AnswerTextOf(JavaScriptSerializer serializer, string line)
        {
            if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) return null;
            Dictionary<string, object> entry;
            try
            {
                entry = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
            if (entry == null || !Equals(Value(entry, "type"), "assistant") || Equals(Value(entry, "isSidechain"), true)) return null;

            var message = Value(entry, "message") as Dictionary<string, object>;
            var content = message != null ? Value(message, "content") : null;
            if (content is string) return (string)content;
            var parts = content as IEnumerable;
            if (parts == null) return null;

            var text = new StringBuilder();
            foreach (var part in parts.OfType<Dictionary<string, object>>())
            {
                if (Equals(Value(part, "type"), "text") && Value(part, "text") is string) text.Append((string)Value(part, "text")).Append('\n');
            }
            return text.Length > 0 ? text.ToString() : null;
        }

        private static object Value(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>受け取った答えを結果のカードに入れる。開いているフローなら画面で（元に戻せる）、閉じていれば文書のページに直接書く。</summary>
        private void DeliverFlowResult(FlowWatch watch, string title, string value)
        {
            if (_flowView != null && _flowView.FlowKey == watch.Key)
            {
                if (_flowView.SetResult(title, value))
                {
                    _flowView.ShowStatus("結果のカード " + title + " に答えを受け取りました");
                    Status("フローの結果（カード " + title + "）を受け取りました");
                    return;
                }
            }
            else if (watch.Document != null && watch.Document.Contains(watch.Page)
                     && _stack.Any(view => ReferenceEquals(view.Owner, watch.Document)))
            {
                var flow = FlowFile.Parse(watch.Document.TextOf(watch.Page));
                var card = flow.Find(title);
                if (card != null && card.Kind == FlowKind.Result)
                {
                    card.Body = value;
                    if (watch.Document.SetText(watch.Page, flow.Serialize()))
                    {
                        UpdateChrome();
                        Status("フローの結果（カード " + title + "）を受け取りました（保存で文書に書きます）");
                        return;
                    }
                }
            }

            // 入れる先が無くなった（カードを消した・文書を閉じた）。答えはクリップボードに置く
            try
            {
                Clipboard.SetText(value);
                Status("フローの結果（カード " + title + "）の入れ先が無いので、クリップボードに置きました");
            }
            catch (Exception)
            {
                Status("フローの結果（カード " + title + "）の入れ先がありませんでした");
            }
        }

        /// <summary>
        /// 会話の記録のファイル。claude はフォルダの名前の英数字以外を - にした名前のフォルダに書く。そこに無ければ、projects の下のフォルダを1段だけ探す。
        /// 無ければ（まだ話しかけていない）null。
        /// </summary>
        private static string FindTranscript(AiTab tab)
        {
            var session = tab.SessionId;
            if (session == null) return null;
            try
            {
                var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(home))
                    home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                var projects = Path.Combine(home, "projects");
                if (!Directory.Exists(projects)) return null;

                var name = session + ".jsonl";
                if (!string.IsNullOrEmpty(tab.Folder))
                {
                    var guess = Path.Combine(projects, new string(tab.Folder.Select(c => c < 128 && char.IsLetterOrDigit(c) ? c : '-').ToArray()), name);
                    if (File.Exists(guess)) return guess;
                }
                foreach (var folder in Directory.EnumerateDirectories(projects))
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static long LengthOf(string path)
        {
            try
            {
                return path != null && File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
