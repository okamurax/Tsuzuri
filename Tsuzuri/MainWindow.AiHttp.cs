using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Tsuzuri.Markdown;

namespace Tsuzuri
{
    // Claude Code のフックを HTTP で受ける口（2026-11-15。SPEC 19章）。
    // フックを PowerShell で動かすと、Claude がツールを使うたびに powershell.exe を起動し（1 回 0.3〜1 秒）、返事が目に見えて遅くなった。
    // Claude Code の HTTP のフック（"type": "http"）なら、フックの入力の JSON をそのまま POST してくるので、プロセスを起動しない。
    // 受けるのは、読み書きの前（PreToolUse）・書き換えたあと（PostToolUse）・話しかけたとき（UserPromptSubmit）。
    // 会話の始まり・終わり（SessionStart・SessionEnd）は今までどおり PowerShell とパイプ（始まりは HTTP を使えない。どちらも1回だけ）。
    // 共通ターミナルの terminal.ps1 もパイプのまま。
    //
    // ・受けるのはこのパソコンの中（127.0.0.1）だけ。番号は空いているものを Windows に選ばせる
    // ・同じパソコンのほかのプロセスからも届くので、URL に合言葉（画面ごとに作る乱数）を入れ、合わなければ答えない。
    //   合言葉はフックの設定（%LOCALAPPDATA%\Tsuzuri\ai\settings-*.json。本人だけが読める場所）にだけ書く
    // ・HTTP は最小限だけ読む（Content-Length の付いた POST。答えたら閉じる）。Claude Code（axios）はこの形で送ってくる
    // ・答えられなかったとき（エディタが閉じている・エラー・時間切れ）、Claude Code は通す（今までのフックと同じ）
    public partial class MainWindow
    {
        private TcpListener _aiHttp;

        /// <summary>フックの URL の頭（http://127.0.0.1:番号/合言葉/）。開いていなければ null。</summary>
        private string _aiHttpBase;

        private string _aiHttpToken;

        /// <summary>受ける本文の上限。Write の中身も来る（読むのは場所の項目だけ）。</summary>
        private const int AiHttpMaxBody = 64 * 1024 * 1024;

        /// <summary>HTTP の口を開き、フックの URL の頭を返す。開けなければ null（フックは PowerShell にする）。</summary>
        private string StartAiHttp()
        {
            if (_aiHttp != null) return _aiHttpBase;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _aiHttpToken = Guid.NewGuid().ToString("N");
                _aiHttpBase = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/" + _aiHttpToken + "/";
                _aiHttp = listener;

                var thread = new Thread(() => AcceptAiHttp(listener)) { IsBackground = true, Name = "Tsuzuri AI HTTP" };
                thread.Start();
                return _aiHttpBase;
            }
            catch (Exception ex)
            {
                _aiHttp = null;
                _aiHttpBase = null;
                Status("AI のフックの口（HTTP）を開けなかったので、PowerShell で動かします: " + ex.Message);
                return null;
            }
        }

        /// <summary>画面を閉じたとき（StopAi）。待っている口を閉じる。</summary>
        private void StopAiHttp()
        {
            var listener = _aiHttp;
            _aiHttp = null;
            if (listener == null) return;

            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
            }
        }

        private void AcceptAiHttp(TcpListener listener)
        {
            while (!_aiStopping)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (_aiStopping || !ReferenceEquals(listener, _aiHttp)) break;
                    Thread.Sleep(100);
                    continue;
                }

                // 頼みごとに別のスレッドで答える（話しかけたときは保存の確かめで待つことがあり、その間もほかのタブのフックに答えるため）
                var connection = client;
                var worker = new Thread(() => ServeAiHttp(connection)) { IsBackground = true };
                worker.Start();
            }
        }

        private void ServeAiHttp(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    var stream = client.GetStream();

                    string method, path;
                    int length;
                    if (!ReadAiHttpHead(stream, out method, out path, out length) || length < 0 || length > AiHttpMaxBody)
                    {
                        WriteAiHttp(stream, 400, string.Empty);
                        return;
                    }

                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var count = stream.Read(body, read, length - read);
                        if (count <= 0) return;
                        read += count;
                    }

                    var parts = path.Trim('/').Split('/');
                    if (method != "POST" || parts.Length != 2 || !string.Equals(parts[0], _aiHttpToken, StringComparison.Ordinal))
                    {
                        WriteAiHttp(stream, 404, string.Empty);
                        return;
                    }

                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var input = serializer.DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>;
                    if (input == null)
                    {
                        WriteAiHttp(stream, 400, string.Empty);
                        return;
                    }

                    object answer = null;
                    switch (parts[1])
                    {
                        case "pre":
                            answer = AiHttpPreToolUse(input);
                            break;
                        case "post":
                            answer = AiHttpPostToolUse(input);
                            break;
                        case "prompt":
                            answer = AiHttpPrompt();
                            break;
                        default:
                            WriteAiHttp(stream, 404, string.Empty);
                            return;
                    }

                    // 答えが無ければ空の本文（Claude Code では「何もしない」）
                    WriteAiHttp(stream, 200, answer == null ? string.Empty : serializer.Serialize(answer));
                }
            }
            catch (Exception)
            {
                // 切られた・読めない頼み等。Claude Code は通す
            }
        }

        /// <summary>読み書きの前。AI に渡さない場所なら断る（permissionDecision: deny と理由）。</summary>
        private object AiHttpPreToolUse(Dictionary<string, object> input)
        {
            var tool = Text(input, "tool_name");
            var cwd = Text(input, "cwd");
            var items = new List<KeyValuePair<string, string>>();
            var toolInput = input.ContainsKey("tool_input") ? input["tool_input"] as Dictionary<string, object> : null;
            if (toolInput != null)
            {
                foreach (var key in AiGuard.TextKeys)
                {
                    var value = Text(toolInput, key);
                    if (value != null) items.Add(new KeyValuePair<string, string>(key, value));
                }
            }

            var refusal = CheckAiGuard(tool, cwd, items);
            if (refusal == null) return null;

            return new Dictionary<string, object>
            {
                {
                    "hookSpecificOutput", new Dictionary<string, object>
                    {
                        { "hookEventName", "PreToolUse" },
                        { "permissionDecision", "deny" },
                        { "permissionDecisionReason", refusal },
                    }
                },
            };
        }

        /// <summary>書き換えたあと。図の画像を作り、読み直す。作れなかった図があれば、その知らせを Claude に見せる（decision: block と理由。ツールは取り消さない）。</summary>
        private object AiHttpPostToolUse(Dictionary<string, object> input)
        {
            var toolInput = input.ContainsKey("tool_input") ? input["tool_input"] as Dictionary<string, object> : null;
            var problem = HandleAiChanged(toolInput != null ? Text(toolInput, "file_path") : null, Text(input, "cwd"));
            if (problem == null) return null;

            return new Dictionary<string, object>
            {
                { "decision", "block" },
                { "reason", problem },
            };
        }

        /// <summary>話しかけたとき。保存して今の状態を書き出し、それを Claude への文脈として返す（additionalContext）。</summary>
        private object AiHttpPrompt()
        {
            try
            {
                PrepareAiPrompt();
            }
            catch (Exception)
            {
                // 間に合わなかった等。前に書いた状態を渡す
            }

            var path = _aiContextPath;
            string context = null;
            try
            {
                if (path != null && File.Exists(path)) context = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception)
            {
            }
            if (string.IsNullOrEmpty(context)) return null;

            return new Dictionary<string, object>
            {
                {
                    "hookSpecificOutput", new Dictionary<string, object>
                    {
                        { "hookEventName", "UserPromptSubmit" },
                        { "additionalContext", context },
                    }
                },
            };
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        /// <summary>要求行とヘッダーを読む（空行まで。64KB まで）。本文の長さは Content-Length（無ければ 0）。</summary>
        private static bool ReadAiHttpHead(Stream stream, out string method, out string path, out int length)
        {
            method = path = null;
            length = 0;

            var head = new MemoryStream();
            var last = new byte[4];
            while (head.Length < 64 * 1024)
            {
                var value = stream.ReadByte();
                if (value < 0) return false;
                head.WriteByte((byte)value);
                last[0] = last[1];
                last[1] = last[2];
                last[2] = last[3];
                last[3] = (byte)value;
                if (last[0] == '\r' && last[1] == '\n' && last[2] == '\r' && last[3] == '\n') break;
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var request = lines[0].Split(' ');
            if (request.Length < 2) return false;
            method = request[0];
            path = request[1];

            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(line.Substring(colon + 1).Trim(), out length)) return false;
                }
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    return false;   // chunked は読まない（Claude Code は Content-Length を付けて送る）
                }
            }
            return true;
        }

        private static void WriteAiHttp(Stream stream, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\n"
                                               + "Content-Type: application/json; charset=utf-8\r\n"
                                               + "Content-Length: " + bytes.Length + "\r\n"
                                               + "Connection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
