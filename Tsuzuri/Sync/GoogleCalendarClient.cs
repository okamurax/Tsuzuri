using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Tsuzuri.Markdown;

namespace Tsuzuri.Sync
{
    /// <summary>
    /// Google のクライアント（SPEC 22章「Google へ送る」。2026-11-23）。exe の隣の google.json。
    /// 利用者が自分の Google Cloud のプロジェクトで作った OAuth クライアント（種類は「デスクトップ アプリ」）の ID と秘密。
    /// Tsuzuri には埋め込まない（公開しているリポジトリに、利用者のプロジェクトの名札を載せないため）。.gitignore で GitHub に出さない。
    /// </summary>
    [DataContract]
    public sealed class GoogleSettings
    {
        [DataMember(Name = "clientId")]
        public string ClientId { get; set; }

        [DataMember(Name = "clientSecret")]
        public string ClientSecret { get; set; }

        public static string StorePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "google.json"); }
        }

        public bool IsUsable
        {
            get
            {
                return !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
                    && ClientId.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal);
            }
        }

        /// <summary>読む。ファイルが無ければ null。書き方が誤っていれば投げる（呼び出し側が知らせる）。</summary>
        public static GoogleSettings Load()
        {
            if (!File.Exists(StorePath)) return null;
            var serializer = new DataContractJsonSerializer(typeof(GoogleSettings));
            using (var stream = new MemoryStream(File.ReadAllBytes(StorePath)))
                return (GoogleSettings)serializer.ReadObject(stream);
        }

        /// <summary>ひな形を書く（無いときだけ）。利用者がメモ帳で埋める。</summary>
        public static bool WriteTemplate()
        {
            if (File.Exists(StorePath)) return false;
            const string template =
                "{\n" +
                "  \"clientId\": \"xxxx.apps.googleusercontent.com\",\n" +
                "  \"clientSecret\": \"xxxx\"\n" +
                "}\n";
            File.WriteAllText(StorePath, template, new UTF8Encoding(false));
            return true;
        }
    }

    public class GoogleException : Exception
    {
        public GoogleException(string message) : base(message)
        {
        }
    }

    public sealed class GoogleSendResult
    {
        /// <summary>入れた予定の数。</summary>
        public int Sent;

        /// <summary>消した前の予定の数。</summary>
        public int Removed;

        /// <summary>消せなかった前の予定の数（次に送るときにまた消す）。</summary>
        public int NotRemoved;

        /// <summary>前の予定が無いのに、送る予定も無いので何もしなかった・利用者がやめた。</summary>
        public bool Cancelled;

        /// <summary>カレンダーを新しく作った。</summary>
        public bool CreatedCalendar;
    }

    /// <summary>
    /// Google カレンダーへ送る口（SPEC 22章「Google へ送る」。2026-11-23）。
    /// Tsuzuri 専用のカレンダー（Tsuzuri が作る。名前は「Tsuzuri」）へ片方向で送る。直すのはいつも Tsuzuri で、Google 側の直しは次に送ると戻る。
    /// 送るたびに、そのカレンダーの「今日以降に掛かる予定」を全部消し、Tsuzuri の今日以降に掛かる予定を全部入れる（API に上書きが無いため）。
    /// 最後に送った中身や予定の ID の対応は覚えない（事故のもとだと利用者が判断）。覚えるのは、ログインの更新用の鍵（google-token.dat。暗号）と、作ったカレンダーの ID・最後に送り終えた日（google-calendar.json）だけ。
    ///
    /// ログイン: OAuth（デスクトップ アプリ。ブラウザで一度ログインし、手元の 127.0.0.1 の空いている番号で返事を受ける。PKCE）。
    /// 権限は、Tsuzuri が作ったカレンダーとその予定だけ（calendar.app.created）と、時間帯を読むだけ（calendar.settings.readonly）。
    /// ほかのカレンダーは読めず、さわれない。更新用の鍵は exe の隣の google-token.dat に、Windows の DPAPI（この Windows の利用者だけが読める）で暗号にして置く。
    ///
    /// 呼び出しは待つ（同期）。呼び出し側が裏のスレッドで呼ぶ。
    /// </summary>
    public sealed class GoogleCalendarClient
    {
        private const string Scopes = "https://www.googleapis.com/auth/calendar.app.created https://www.googleapis.com/auth/calendar.settings.readonly";
        private const string Api = "https://www.googleapis.com/calendar/v3/";
        private const string BatchUrl = "https://www.googleapis.com/batch/calendar/v3";
        private const string CalendarName = "Tsuzuri";
        private const string CalendarDescription = "Tsuzuri のカレンダーから送った予定。直すのは Tsuzuri で（ここで直しても、次に Tsuzuri から送ると戻る）。";
        private const int TimeoutMs = 30000;
        private const int LoginWaitMinutes = 5;
        private const int BatchSize = 25;   // バッチは 50 件まで。続けて書くと速さの上限に掛かりやすいので少なめに

        private readonly GoogleSettings _settings;
        private readonly Action<string> _progress;
        private string _accessToken;

        static GoogleCalendarClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public GoogleCalendarClient(GoogleSettings settings, Action<string> progress)
        {
            _settings = settings;
            _progress = progress ?? delegate { };
        }

        public static string TokenPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "google-token.dat"); }
        }

        /// <summary>
        /// Tsuzuri カレンダーの「from 以降に掛かる予定」を、build(from) の予定に入れ替える。from は今日と、前に送った日の早い方
        /// （前に送ってから今日までに終わった・過去へ動かした予定も、Google の一覧と Tsuzuri の両方で同じ範囲に入れ、履歴ごと消さないため）。
        /// 先に全部入れてから前のを消す。入れられなかったら、今回入れた分（送るたびの印 tsuzuriSend で探す。時間切れで実は入っていた分も）を消して前のを残す。
        /// 送る予定が無いのに前の予定があれば、confirmEmpty（前の予定の数）で尋ねる（読めない予定のファイルで全部消さないため）。
        /// </summary>
        public GoogleSendResult Replace(Func<DateTime, List<ExportEvent>> build, DateTime today, Func<int, bool> confirmEmpty)
        {
            var result = new GoogleSendResult();
            var token = TokenState.Load();
            SignIn(token);

            _progress("Google のカレンダーを確かめています…");
            var timeZone = ReadTimeZone();
            var state = CalendarState.Load();
            var calendarId = state.CalendarId != null && CalendarExists(state.CalendarId) ? state.CalendarId : null;
            if (calendarId == null)
            {
                calendarId = CreateCalendar(timeZone);
                state.CalendarId = calendarId;
                state.LastSent = null;
                state.Save();
                result.CreatedCalendar = true;
            }

            var from = state.LastSentDate.HasValue && state.LastSentDate.Value < today.Date ? state.LastSentDate.Value : today.Date;
            var events = build(from);
            var existing = result.CreatedCalendar ? new List<string>() : ListFrom(calendarId, from);
            if (events.Count == 0 && existing.Count > 0 && !confirmEmpty(existing.Count))
            {
                result.Cancelled = true;
                return result;
            }

            // 入れる（送るたびの印を付ける）
            var path = "/calendar/v3/calendars/" + Uri.EscapeDataString(calendarId) + "/events";
            var tag = Guid.NewGuid().ToString("N");
            var inserts = events.Select(e => new GoogleCalendarFormat.BatchPart
            {
                Method = "POST",
                Path = path,
                Body = GoogleCalendarFormat.EventJson(e, timeZone, local => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(), tag),
            }).ToList();
            var known = new HashSet<string>();   // 入ったと返事で分かった ID（送り直しで2つ入ったものも、どちらも）
            var failed = new List<string>();
            var ids = new List<string>();
            try
            {
                var inserted = RunBatches(inserts, "予定を送っています", (i, answer) =>
                {
                    var id = answer.Status / 100 == 2 ? IdOf(answer.Body) : null;
                    if (id != null) known.Add(id);
                });
                for (var i = 0; i < inserts.Count; i++)
                {
                    GoogleCalendarFormat.BatchResult answer;
                    var id = inserted.TryGetValue(i, out answer) && answer.Status / 100 == 2 ? IdOf(answer.Body) : null;
                    if (id != null) ids.Add(id);
                    else failed.Add("「" + events[i].Title + "」" + (answer != null ? "（" + answer.Status + " " + ReasonOf(answer.Body) + "）" : "（返事が無い）"));
                }
            }
            catch (GoogleException ex)
            {
                throw new GoogleException(ex.Message + Environment.NewLine + Undo(calendarId, path, tag, known));
            }
            if (failed.Count > 0)
                throw new GoogleException("Google へ送れなかった予定があります:" + Environment.NewLine
                    + string.Join(Environment.NewLine, failed.Take(5)) + (failed.Count > 5 ? Environment.NewLine + "ほか " + (failed.Count - 5) + " 件" : string.Empty)
                    + Environment.NewLine + Undo(calendarId, path, tag, known));
            result.Sent = ids.Count;

            // 送り直しで二重に入った分（返事が来なかったが、実は入っていた）を消す
            var extra = ListTagged(calendarId, tag).Union(known).Where(id => !ids.Contains(id)).ToList();
            if (extra.Count > 0) RunBatches(extra.Select(id => DeletePart(path, id)).ToList(), "二重に入った予定を消しています");

            // 前のを消す（もう無いもの 404・410 は消えたとみなす）
            var deletes = existing.Select(id => DeletePart(path, id)).ToList();
            var deleted = RunBatches(deletes, "前の予定を消しています");
            for (var i = 0; i < deletes.Count; i++)
            {
                GoogleCalendarFormat.BatchResult answer;
                if (deleted.TryGetValue(i, out answer) && (answer.Status / 100 == 2 || answer.Status == 404 || answer.Status == 410)) result.Removed++;
                else result.NotRemoved++;
            }

            // 前のを消しきれなかったら、次も同じ日から数える（消せなかった分を次に拾うため）
            if (result.NotRemoved == 0)
            {
                state.LastSent = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                state.Save();
            }
            return result;
        }

        /// <summary>
        /// 今回入れた分を消す。返事で分かった ID（known）と、印 tag の付いた予定（時間切れで実は入っていた分も）を合わせて消す
        /// （入れた直後の一覧に出ないことがあっても、返事で分かった分は消せるように）。結果を知らせる文を返す。
        /// </summary>
        private string Undo(string calendarId, string path, string tag, HashSet<string> known)
        {
            List<string> ids;
            try
            {
                ids = ListTagged(calendarId, tag).Union(known).ToList();
            }
            catch (GoogleException)
            {
                ids = known.ToList();
            }
            try
            {
                if (ids.Count == 0) return "今回送った予定はありません（前の予定はそのまま）。";
                var parts = ids.Select(id => DeletePart(path, id)).ToList();
                var done = RunBatches(parts, "送った分を取り消しています");
                var left = Enumerable.Range(0, parts.Count).Count(i =>
                {
                    GoogleCalendarFormat.BatchResult answer;
                    return !(done.TryGetValue(i, out answer) && (answer.Status / 100 == 2 || answer.Status == 404 || answer.Status == 410));
                });
                return left == 0 ? "今回送った分は取り消しました（前の予定はそのまま）。"
                                 : "今回送った分のうち " + left + " 件を取り消せませんでした。Google で二重に見えます（次に送ると入れ替わります）。";
            }
            catch (GoogleException)
            {
                return "今回送った分を取り消せませんでした。Google で二重に見えることがあります（次に送ると入れ替わります）。";
            }
        }

        private static GoogleCalendarFormat.BatchPart DeletePart(string path, string id)
        {
            return new GoogleCalendarFormat.BatchPart { Method = "DELETE", Path = path + "/" + Uri.EscapeDataString(id) };
        }

        // ---------------------------------------------------------------
        // カレンダー
        // ---------------------------------------------------------------

        private string ReadTimeZone()
        {
            var data = Json(Call("GET", Api + "users/me/settings/timezone", null)) as Dictionary<string, object>;
            var value = data != null && data.ContainsKey("value") ? data["value"] as string : null;
            if (string.IsNullOrEmpty(value)) throw new GoogleException("Google カレンダーの時間帯を読めませんでした。");
            return value;
        }

        private bool CalendarExists(string id)
        {
            try
            {
                Call("GET", Api + "calendars/" + Uri.EscapeDataString(id), null);
                return true;
            }
            catch (GoogleHttpException ex)
            {
                if (ex.Status == 404 || ex.Status == 410) return false;   // 利用者が Google で消した（作り直す）
                throw;
            }
        }

        private string CreateCalendar(string timeZone)
        {
            _progress("Google に「" + CalendarName + "」のカレンダーを作っています…");
            var body = "{\"summary\":" + GoogleCalendarFormat.Quote(CalendarName) + ",\"description\":" + GoogleCalendarFormat.Quote(CalendarDescription)
                     + ",\"timeZone\":" + GoogleCalendarFormat.Quote(timeZone) + "}";
            var id = IdOf(Call("POST", Api + "calendars", body));
            if (id == null) throw new GoogleException("Google にカレンダーを作れませんでした。");
            return id;
        }

        /// <summary>from 以降に掛かる予定（終わりが from の 0:00 より後）の ID。繰り返しは1件（singleEvents=false）。</summary>
        private List<string> ListFrom(string calendarId, DateTime from)
        {
            var timeMin = DateTime.SpecifyKind(from.Date, DateTimeKind.Local).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            return ListIds(calendarId, "&timeMin=" + Uri.EscapeDataString(timeMin));
        }

        private List<string> ListIds(string calendarId, string filter)
        {
            var ids = new List<string>();
            string page = null;
            do
            {
                var url = Api + "calendars/" + Uri.EscapeDataString(calendarId) + "/events?singleEvents=false&showDeleted=false&maxResults=2500"
                        + "&fields=items(id),nextPageToken" + filter
                        + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : string.Empty);
                var data = Json(Call("GET", url, null)) as Dictionary<string, object>;
                var items = data != null && data.ContainsKey("items") ? data["items"] as IEnumerable : null;
                if (items != null)
                    foreach (var item in items)
                    {
                        var entry = item as Dictionary<string, object>;
                        var id = entry != null && entry.ContainsKey("id") ? entry["id"] as string : null;
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                page = data != null && data.ContainsKey("nextPageToken") ? data["nextPageToken"] as string : null;
            }
            while (!string.IsNullOrEmpty(page));
            return ids.Distinct().ToList();
        }

        /// <summary>印 tsuzuriSend=tag の付いた予定の ID（今回送った分）。</summary>
        private List<string> ListTagged(string calendarId, string tag)
        {
            return ListIds(calendarId, "&privateExtendedProperty=" + Uri.EscapeDataString("tsuzuriSend=" + tag));
        }

        /// <summary>
        /// まとめて送る（BatchSize 件ずつ）。速さの上限（403 の rateLimitExceeded・429）と Google の不調（5xx）・返事の無い part は、間をあけて送り直す（5回まで）。
        /// 結果は parts の番号ごと（最後の返事）。
        /// </summary>
        private Dictionary<int, GoogleCalendarFormat.BatchResult> RunBatches(List<GoogleCalendarFormat.BatchPart> parts, string what,
                                                                             Action<int, GoogleCalendarFormat.BatchResult> onResult = null)
        {
            var results = new Dictionary<int, GoogleCalendarFormat.BatchResult>();
            var pending = Enumerable.Range(0, parts.Count).ToList();
            for (var attempt = 0; attempt < 5 && pending.Count > 0; attempt++)
            {
                if (attempt > 0) Thread.Sleep(1000 << attempt);
                var retry = new List<int>();
                for (var start = 0; start < pending.Count; start += BatchSize)
                {
                    var chunk = pending.Skip(start).Take(BatchSize).ToList();
                    _progress(what + "… " + (start + chunk.Count) + " / " + pending.Count + (attempt > 0 ? "（送り直し）" : string.Empty));
                    var boundary = "tsuzuri_" + Guid.NewGuid().ToString("N");
                    var body = GoogleCalendarFormat.BatchBody(chunk.Select(i => parts[i]).ToList(), boundary);
                    string contentType;
                    var text = Send("POST", BatchUrl, body, "multipart/mixed; boundary=" + boundary, out contentType);
                    var answers = GoogleCalendarFormat.ParseBatch(contentType, text);
                    for (var k = 0; k < chunk.Count; k++)
                    {
                        GoogleCalendarFormat.BatchResult answer;
                        if (answers.TryGetValue(k, out answer))
                        {
                            results[chunk[k]] = answer;
                            if (onResult != null) onResult(chunk[k], answer);   // 途中で投げても、そこまでに分かった結果を呼び出し側が持てるように
                        }
                        if (answer == null || answer.Status == 429 || answer.Status >= 500
                            || (answer.Status == 403 && answer.Body.IndexOf("ratelimit", StringComparison.OrdinalIgnoreCase) >= 0))
                            retry.Add(chunk[k]);
                    }
                }
                pending = retry;
            }
            return results;
        }

        // ---------------------------------------------------------------
        // ログイン（OAuth。デスクトップ アプリ・PKCE・127.0.0.1 で受ける）
        // ---------------------------------------------------------------

        private void SignIn(TokenState state)
        {
            if (!string.IsNullOrEmpty(state.RefreshToken))
            {
                _progress("Google にログインしています…");
                try
                {
                    var data = Json(PostForm("https://oauth2.googleapis.com/token", new Dictionary<string, string>
                    {
                        { "client_id", _settings.ClientId.Trim() },
                        { "client_secret", _settings.ClientSecret.Trim() },
                        { "refresh_token", state.RefreshToken },
                        { "grant_type", "refresh_token" },
                    })) as Dictionary<string, object>;
                    _accessToken = data != null && data.ContainsKey("access_token") ? data["access_token"] as string : null;
                    if (!string.IsNullOrEmpty(_accessToken)) return;
                }
                catch (GoogleHttpException ex)
                {
                    // 取り消された・期限が切れた鍵（invalid_grant）は捨てて、ブラウザでログインし直す
                    if (ex.Status != 400 && ex.Status != 401) throw;
                }
                state.RefreshToken = null;
                state.Save();
            }
            LoginInBrowser(state);
        }

        private void LoginInBrowser(TokenState state)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var redirect = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                var verifier = RandomText(48);
                string challenge;
                using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
                var stateText = RandomText(16);
                var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent"
                        + "&client_id=" + Uri.EscapeDataString(_settings.ClientId.Trim())
                        + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                        + "&scope=" + Uri.EscapeDataString(Scopes)
                        + "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + stateText;

                _progress("ブラウザで Google にログインしてください（" + LoginWaitMinutes + "分まで待ちます）…");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                var code = WaitForCode(listener, stateText);
                _progress("Google にログインしています…");
                var data = Json(PostForm("https://oauth2.googleapis.com/token", new Dictionary<string, string>
                {
                    { "client_id", _settings.ClientId.Trim() },
                    { "client_secret", _settings.ClientSecret.Trim() },
                    { "code", code },
                    { "code_verifier", verifier },
                    { "redirect_uri", redirect },
                    { "grant_type", "authorization_code" },
                })) as Dictionary<string, object>;
                _accessToken = data != null && data.ContainsKey("access_token") ? data["access_token"] as string : null;
                var refresh = data != null && data.ContainsKey("refresh_token") ? data["refresh_token"] as string : null;
                if (string.IsNullOrEmpty(_accessToken)) throw new GoogleException("Google にログインできませんでした（鍵を受け取れませんでした）。");
                if (!string.IsNullOrEmpty(refresh))
                {
                    state.RefreshToken = refresh;
                    state.Save();
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>ブラウザが 127.0.0.1 へ戻ってくるのを待ち、code を受け取る。ほかの要求（favicon など）には 404 を返して待ち続ける。</summary>
        private static string WaitForCode(TcpListener listener, string expectedState)
        {
            var deadline = DateTime.UtcNow.AddMinutes(LoginWaitMinutes);
            while (true)
            {
                var accept = listener.AcceptTcpClientAsync();
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !accept.Wait(left))
                    throw new GoogleException("Google へのログインを " + LoginWaitMinutes + " 分待ちましたが、終わりませんでした。もう一度「Google へ送る」を押してください。");

                using (var client = accept.Result)
                using (var stream = client.GetStream())
                {
                    // ブラウザは何も送らない予備の接続を開くことがある。読めない接続は捨てて次を待つ
                    stream.ReadTimeout = 3000;
                    string line;
                    try
                    {
                        line = ReadLine(stream);   // GET /?state=…&code=… HTTP/1.1
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    var pieces = (line ?? string.Empty).Split(' ');
                    var target = pieces.Length >= 2 ? pieces[1] : string.Empty;
                    var query = ParseQuery(target);
                    string code, error, state;
                    query.TryGetValue("code", out code);
                    query.TryGetValue("error", out error);
                    query.TryGetValue("state", out state);
                    var ok = error == null && state == expectedState;
                    try
                    {
                        if (code == null && error == null) Reply(stream, "404 Not Found", string.Empty);
                        else Reply(stream, "200 OK", ok
                            ? "Tsuzuri: Google にログインしました。このタブは閉じてかまいません。"
                            : "Tsuzuri: Google にログインできませんでした（" + (error ?? "返事が合いません") + "）。このタブを閉じて、Tsuzuri でもう一度押してください。");
                    }
                    catch (IOException)
                    {
                        // ブラウザが先に閉じた。返事は受け取れているので続ける
                    }
                    if (code == null && error == null) continue;
                    if (error != null) throw new GoogleException(error == "access_denied" ? "Google へのログインをやめました。" : "Google にログインできませんでした: " + error);
                    if (!ok) throw new GoogleException("Google からの返事が合いませんでした（別のログインの返事かもしれません）。もう一度押してください。");
                    return code;
                }
            }
        }

        private static string ReadLine(NetworkStream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 8192)
            {
                var b = stream.ReadByte();
                if (b < 0 || b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void Reply(NetworkStream stream, string status, string message)
        {
            var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Tsuzuri</title></head><body style=\"font-family:sans-serif;margin:3em\"><p>"
                     + WebUtility.HtmlEncode(message) + "</p></body></html>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                     + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static Dictionary<string, string> ParseQuery(string target)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            var mark = target.IndexOf('?');
            if (mark < 0) return query;
            foreach (var pair in target.Substring(mark + 1).Split('&'))
            {
                var equal = pair.IndexOf('=');
                if (equal <= 0) continue;
                query[Uri.UnescapeDataString(pair.Substring(0, equal).Replace('+', ' '))] = Uri.UnescapeDataString(pair.Substring(equal + 1).Replace('+', ' '));
            }
            return query;
        }

        private static string RandomText(int bytes)
        {
            var data = new byte[bytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(data);
            return Base64Url(data);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---------------------------------------------------------------
        // 通信
        // ---------------------------------------------------------------

        private sealed class GoogleHttpException : GoogleException
        {
            public readonly int Status;

            public GoogleHttpException(int status, string message) : base(message)
            {
                Status = status;
            }
        }

        private string Call(string method, string url, string json)
        {
            string contentType;
            return Send(method, url, json, json != null ? "application/json; charset=UTF-8" : null, out contentType);
        }

        private string PostForm(string url, Dictionary<string, string> form)
        {
            var body = string.Join("&", form.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
            string contentType;
            return Send("POST", url, body, "application/x-www-form-urlencoded", out contentType, false);
        }

        private string Send(string method, string url, string body, string bodyType, out string responseType, bool withToken = true)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.UserAgent = "Tsuzuri";
            if (withToken) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _accessToken;

            // 通信の失敗はどれも GoogleException にする（送る途中で切れたとき、呼び出し側が今回の分を取り消せるように。
            // 応答を読む途中の時間切れは IOException、送る前の接続の失敗は GetRequestStream の WebException で来る）
            try
            {
                if (body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = bodyType;
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                else if (method == "POST") request.ContentLength = 0;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    responseType = response.ContentType;
                    return ReadAll(response);
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) throw new GoogleException("Google につながりませんでした: " + ex.Message);
                using (response)
                {
                    var text = ReadAll(response);
                    var status = (int)response.StatusCode;
                    throw new GoogleHttpException(status, "Google が断りました（" + status + " " + ReasonOf(text) + "）。");
                }
            }
            catch (IOException ex)
            {
                throw new GoogleException("Google との通信が途中で切れました: " + ex.Message);
            }
        }

        private static string ReadAll(HttpWebResponse response)
        {
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static object Json(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string IdOf(string json)
        {
            var data = Json(json) as Dictionary<string, object>;
            return data != null && data.ContainsKey("id") ? data["id"] as string : null;
        }

        /// <summary>Google の断りの理由（error.message か error_description）。読めなければ空。</summary>
        private static string ReasonOf(string json)
        {
            var data = Json(json) as Dictionary<string, object>;
            if (data == null || !data.ContainsKey("error")) return string.Empty;
            var error = data["error"] as Dictionary<string, object>;
            if (error != null) return error.ContainsKey("message") ? error["message"] as string ?? string.Empty : string.Empty;
            var description = data.ContainsKey("error_description") ? data["error_description"] as string : null;
            return description ?? data["error"] as string ?? string.Empty;
        }

        // ---------------------------------------------------------------
        // 手元に覚えるもの（google-calendar.json と、DPAPI で暗号にした google-token.dat）
        // ---------------------------------------------------------------

        /// <summary>
        /// 作ったカレンダーの ID と、最後に送り終えた日（exe の隣の google-calendar.json）。秘密ではないので暗号にしない
        /// （ログインの鍵が読めなくなっても、カレンダーを作り直して二重にしないため。カレンダーは calendar.app.created では探し直せない）。
        /// 予定の中身や ID の対応は覚えない（利用者の判断）。
        /// </summary>
        [DataContract]
        private sealed class CalendarState
        {
            private static string StorePath
            {
                get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "google-calendar.json"); }
            }

            [DataMember(Name = "calendarId", EmitDefaultValue = false)]
            public string CalendarId { get; set; }

            /// <summary>最後に送り終えた日（yyyy-MM-dd）。</summary>
            [DataMember(Name = "lastSent", EmitDefaultValue = false)]
            public string LastSent { get; set; }

            public DateTime? LastSentDate
            {
                get
                {
                    DateTime date;
                    return DateTime.TryParseExact(LastSent ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ? date : (DateTime?)null;
                }
            }

            /// <summary>読む。無ければ空。書き方が誤っていれば投げる（勝手に作り直すと、カレンダーが二重になるため）。</summary>
            public static CalendarState Load()
            {
                if (!File.Exists(StorePath)) return new CalendarState();
                try
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(StorePath)))
                        return (CalendarState)new DataContractJsonSerializer(typeof(CalendarState)).ReadObject(stream) ?? new CalendarState();
                }
                catch (SerializationException)
                {
                    throw new GoogleException("google-calendar.json を読めませんでした（「Tsuzuri」カレンダーの ID を書いたファイル）。書き方を直すか、" +
                                              "Google の「Tsuzuri」カレンダーを消してからこのファイルを消してください（新しく作ります）。");
                }
            }

            public void Save()
            {
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(CalendarState)).WriteObject(stream, this);
                    File.WriteAllBytes(StorePath, stream.ToArray());
                }
            }
        }

        [DataContract]
        private sealed class TokenState
        {
            private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Tsuzuri.GoogleCalendar");

            [DataMember(Name = "refreshToken", EmitDefaultValue = false)]
            public string RefreshToken { get; set; }

            /// <summary>読む。無い・読めない（別の Windows の利用者が書いた・Windows のパスワードを初期化した等）なら空（ログインし直す）。</summary>
            public static TokenState Load()
            {
                try
                {
                    if (!File.Exists(TokenPath)) return new TokenState();
                    var bytes = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
                    using (var stream = new MemoryStream(bytes))
                        return (TokenState)new DataContractJsonSerializer(typeof(TokenState)).ReadObject(stream) ?? new TokenState();
                }
                catch (Exception ex) when (ex is CryptographicException || ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException)
                {
                    return new TokenState();
                }
            }

            public void Save()
            {
                using (var stream = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(TokenState)).WriteObject(stream, this);
                    File.WriteAllBytes(TokenPath, ProtectedData.Protect(stream.ToArray(), Entropy, DataProtectionScope.CurrentUser));
                }
            }
        }
    }
}
