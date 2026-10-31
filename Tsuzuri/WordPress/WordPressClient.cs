using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Tsuzuri.WordPress
{
    /// <summary>WordPress の投稿1つ（必要な分だけ）。</summary>
    public sealed class WordPressPost
    {
        public int Id;
        public string Title;

        /// <summary>本文の HTML（編集用の生の中身。context=edit の content.raw）。一覧では null。</summary>
        public string Content;

        /// <summary>publish・draft・pending・private・future。</summary>
        public string Status;

        /// <summary>最後に更新された時刻（UTC。modified_gmt をそのまま）。上書きする前に、WordPress 側で直されていないかを見る。</summary>
        public string Modified;

        public string Link;
    }

    /// <summary>WordPress が断った・つながらなかった。Message は利用者に見せる文。</summary>
    public sealed class WordPressException : Exception
    {
        public WordPressException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// WordPress の REST API（/wp-json/wp/v2/posts）。アプリケーションパスワードの Basic 認証で読み書きする（SPEC 20章）。
    /// 呼び出しは待つ（同期）。保存のときは、反映できたかで保存の成否を決めるため。一覧などは呼び出し側が裏のスレッドで呼ぶ。
    /// </summary>
    public sealed class WordPressClient
    {
        private const int TimeoutMs = 30000;
        private const string ListFields = "id,title,status,modified_gmt,link";

        /// <summary>一覧で一度に読む件数。WordPress が1回で返すのは 100 件まで。</summary>
        public const int PageSize = 100;

        private readonly WordPressSite _site;

        static WordPressClient()
        {
            // .NET Framework 4.8 は OS の既定に従うが、古い既定の環境でも TLS 1.2 でつながるようにしておく。
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public WordPressClient(WordPressSite site)
        {
            _site = site;
        }

        /// <summary>
        /// 最近更新した順に、page 枚目（1 から）の 100 件。search があれば WordPress の検索で絞る（全記事が対象）。下書き・非公開も含める。
        /// total は、条件に合う記事の総数（WordPress が X-WP-Total で返す）。「さらに読む」を出すかを決めるのに使う（2026-10-31）。
        /// </summary>
        public List<WordPressPost> List(string search, int page, out int total)
        {
            var query = "posts?context=edit&per_page=" + PageSize + "&page=" + page
                + "&orderby=modified&order=desc&status=publish,future,draft,pending,private&_fields=" + ListFields;
            if (!string.IsNullOrWhiteSpace(search)) query += "&search=" + Uri.EscapeDataString(search.Trim());

            var count = -1;
            var posts = new List<WordPressPost>();
            var items = Request("GET", query, null, null, null, null, response =>
            {
                int value;
                if (int.TryParse(response.Headers["X-WP-Total"], NumberStyles.None, CultureInfo.InvariantCulture, out value)) count = value;
            }) as IEnumerable;
            total = count;
            if (items == null) return posts;

            foreach (var item in items)
            {
                var post = ToPost(item as Dictionary<string, object>);
                if (post != null) posts.Add(post);
            }

            return posts;
        }

        /// <summary>本文まで含めて1つ読む。</summary>
        public WordPressPost Get(int id)
        {
            var post = ToPost(Request("GET", "posts/" + id + "?context=edit", null) as Dictionary<string, object>);
            if (post == null) throw new WordPressException("投稿 " + id + " を読めませんでした。");

            // 編集用の本文（raw）が無ければ開かない。表示用（rendered）には広告・関連記事・ショートコードを展開した結果が入っていて、
            // それを直して送ると、記事に焼き込まれてしまう（2026-10-30 のレビュー。前は rendered で代わりにしていた）。
            if (post.Content == null)
                throw new WordPressException("投稿 " + id + " の編集用の本文を読めませんでした（この投稿を編集する権限が無いか、プラグインが本文を隠しています）。");
            return post;
        }

        /// <summary>一覧と同じ欄だけで1つ読む（ID で探すとき）。</summary>
        public WordPressPost Find(int id)
        {
            return ToPost(Request("GET", "posts/" + id + "?context=edit&_fields=" + ListFields, null) as Dictionary<string, object>);
        }

        /// <summary>最後に更新された時刻だけ読む。</summary>
        public string ModifiedOf(int id)
        {
            var data = Request("GET", "posts/" + id + "?context=edit&_fields=modified_gmt", null) as Dictionary<string, object>;
            return data == null ? null : Str(data, "modified_gmt");
        }

        /// <summary>
        /// 書く。id が 0 なら新しい投稿を作る。状態が分かっていれば送る（公開中は公開のまま）。
        /// 分からない（null・空）ときは送らない（WordPress 側の状態のまま）。前は「下書き」を送っていて、公開中の記事が下書きに戻りかねなかった（2026-10-30 のレビュー）。
        /// 新しい投稿で状態を送らなければ、WordPress の既定（下書き）になる。
        /// 書いたあとの投稿（ID・時刻・状態・URL）を返す。
        /// </summary>
        public WordPressPost Save(int id, string title, string html, string status)
        {
            var body = new Dictionary<string, object>
            {
                { "title", title },
                { "content", html },
            };
            if (!string.IsNullOrEmpty(status)) body["status"] = status;

            var path = id > 0 ? "posts/" + id : "posts";
            var post = ToPost(Request("POST", path + "?context=edit", body) as Dictionary<string, object>);
            if (post == null) throw new WordPressException("WordPress の返事を読めませんでした。");
            return post;
        }

        /// <summary>
        /// 画像をメディアにアップロードし、その URL（source_url）を返す（2026-10-30）。
        /// ファイル名は Content-Disposition で渡す。日本語の名前は filename* で（WordPress は両方読む）。
        /// </summary>
        public string UploadMedia(string fileName, byte[] data)
        {
            var ascii = new string(fileName.Select(c => c < 128 && c != '"' && c != '\\' ? c : '_').ToArray());
            var disposition = "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + Uri.EscapeDataString(fileName);

            var result = Request("POST", "media", null, data, MediaType(fileName), disposition, null) as Dictionary<string, object>;
            var url = result == null ? null : Str(result, "source_url");
            if (string.IsNullOrEmpty(url)) throw new WordPressException("画像をアップロードしましたが、URL が返ってきませんでした: " + fileName);
            return url;
        }

        private static string MediaType(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        // ---------------------------------------------------------------

        private object Request(string method, string path, Dictionary<string, object> body)
        {
            return Request(method, path, body, null, null, null, null);
        }

        /// <param name="raw">JSON の代わりに送るバイト列（メディア）。</param>
        /// <param name="inspect">返事のヘッダーを見る（一覧の総数など）。要らなければ null。</param>
        private object Request(string method, string path, Dictionary<string, object> body, byte[] raw, string rawType, string disposition, Action<HttpWebResponse> inspect)
        {
            var url = _site.Url.TrimEnd('/') + "/wp-json/wp/v2/" + path;

            HttpWebRequest request;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
            }
            catch (Exception ex)
            {
                throw new WordPressException("URL が正しくありません: " + url + Environment.NewLine + ex.Message);
            }

            request.Method = method;
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.Accept = "application/json";
            request.UserAgent = "Tsuzuri";
            request.Headers[HttpRequestHeader.Authorization] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(_site.User + ":" + (_site.AppPassword ?? string.Empty).Replace(" ", string.Empty)));

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            try
            {
                if (body != null || raw != null)
                {
                    var data = raw ?? Encoding.UTF8.GetBytes(serializer.Serialize(body));
                    request.ContentType = raw != null ? rawType : "application/json; charset=utf-8";
                    if (disposition != null) request.Headers["Content-Disposition"] = disposition;
                    request.ContentLength = data.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (inspect != null) inspect(response);
                    return serializer.DeserializeObject(ReadBody(response));
                }
            }
            catch (WebException ex)
            {
                throw new WordPressException(Describe(ex, serializer));
            }
            catch (ArgumentException ex)
            {
                // 返事が JSON でない（WordPress ではないページ・URL の誤り）。
                throw new WordPressException("WordPress の返事を読めませんでした（URL が正しいか確かめてください）。" + Environment.NewLine + ex.Message);
            }
        }

        private static string ReadBody(WebResponse response)
        {
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>WordPress が返した理由（message）があればそれを、無ければ通信の誤りを、見せる文にする。</summary>
        private static string Describe(WebException ex, JavaScriptSerializer serializer)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null) return "WordPress につながりませんでした。" + Environment.NewLine + ex.Message;

            var code = (int)response.StatusCode;
            string message = null;
            try
            {
                var data = serializer.DeserializeObject(ReadBody(response)) as Dictionary<string, object>;
                if (data != null) message = Str(data, "message");
            }
            catch (Exception)
            {
            }

            var text = "WordPress が受け付けませんでした（HTTP " + code + "）。";
            if (code == 401 || code == 403) text += Environment.NewLine + "ユーザー名とアプリケーションパスワード（wordpress.json）を確かめてください。";
            if (code == 404) text += Environment.NewLine + "投稿が見つかりません（消された・URL の誤り）。";
            if (!string.IsNullOrEmpty(message)) text += Environment.NewLine + WebUtility.HtmlDecode(message);
            return text;
        }

        private static WordPressPost ToPost(Dictionary<string, object> data)
        {
            if (data == null || !data.ContainsKey("id")) return null;

            int id;
            if (!int.TryParse(Convert.ToString(data["id"], CultureInfo.InvariantCulture), out id)) return null;

            return new WordPressPost
            {
                Id = id,
                Title = Raw(data, "title", true),
                Content = data.ContainsKey("content") ? Raw(data, "content", false) : null,
                Status = Str(data, "status"),
                Modified = Str(data, "modified_gmt"),
                Link = Str(data, "link"),
            };
        }

        /// <summary>
        /// title・content は {raw, rendered}。編集用の raw を使う。raw が無いとき、renderedFallback なら rendered（一覧のタイトルの表示用）、
        /// そうでなければ null（本文。表示用で代わりにしない）。
        /// </summary>
        private static string Raw(Dictionary<string, object> data, string key, bool renderedFallback)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return renderedFallback ? string.Empty : null;

            var parts = value as Dictionary<string, object>;
            if (parts == null) return renderedFallback ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

            var raw = Str(parts, "raw");
            if (raw != null) return raw;
            return renderedFallback ? Str(parts, "rendered") ?? string.Empty : null;
        }

        private static string Str(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }
    }
}
