using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Tsuzuri.WordPress
{
    /// <summary>
    /// WordPress の接続先（SPEC 20章。2026-10-30）。exe と同じ階層の wordpress.json。
    /// app.json と分けたのは、パスワードを持つため。エディタを GitHub に出しても、このファイルは出ない（.gitignore・bin/ の下）。
    /// アプリケーションパスワード（WordPress のユーザー → プロフィール → アプリケーションパスワード）を使う。ログインのパスワードではない。
    /// </summary>
    [DataContract]
    public sealed class WordPressSettings
    {
        [DataMember(Name = "sites")]
        public List<WordPressSite> Sites { get; set; }

        public static string StorePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wordpress.json"); }
        }

        /// <summary>読む。ファイルが無ければ null。書き方が誤っていれば投げる（呼び出し側が知らせる）。</summary>
        public static WordPressSettings Load()
        {
            if (!File.Exists(StorePath)) return null;

            var serializer = new DataContractJsonSerializer(typeof(WordPressSettings));
            using (var stream = new MemoryStream(File.ReadAllBytes(StorePath)))
            {
                var settings = (WordPressSettings)serializer.ReadObject(stream);
                if (settings.Sites == null) settings.Sites = new List<WordPressSite>();
                settings.Sites.RemoveAll(site => site == null || !site.IsUsable);
                return settings;
            }
        }

        /// <summary>
        /// ひな形を書く（無いときだけ）。書いたら true。利用者がメモ帳で埋める。
        /// JSON はコメントを書けないので、説明は欄の値に入れておく。
        /// </summary>
        public static bool WriteTemplate()
        {
            if (File.Exists(StorePath)) return false;

            const string template =
                "{\n" +
                "  \"sites\": [\n" +
                "    {\n" +
                "      \"name\": \"一覧に出す名前（サイトが1つなら何でもよい）\",\n" +
                "      \"url\": \"https://example.com\",\n" +
                "      \"user\": \"WordPress のユーザー名\",\n" +
                "      \"appPassword\": \"xxxx xxxx xxxx xxxx xxxx xxxx\"\n" +
                "    }\n" +
                "  ]\n" +
                "}\n";

            File.WriteAllText(StorePath, template, new UTF8Encoding(false));
            return true;
        }
    }

    [DataContract]
    public sealed class WordPressSite
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>サイトの URL（https://example.com）。wp-json の手前まで。</summary>
        [DataMember(Name = "url")]
        public string Url { get; set; }

        [DataMember(Name = "user")]
        public string User { get; set; }

        /// <summary>アプリケーションパスワード。空白は入っていても入っていなくてもよい。</summary>
        [DataMember(Name = "appPassword")]
        public string AppPassword { get; set; }

        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Url : Name; }
        }

        /// <summary>ひな形のままではなく、つなぐのに要るものが揃っているか。</summary>
        public bool IsUsable
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Url)
                    && (Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    && Url.IndexOf("example.com", StringComparison.OrdinalIgnoreCase) < 0
                    && !string.IsNullOrWhiteSpace(User)
                    && !string.IsNullOrWhiteSpace(AppPassword)
                    && AppPassword.IndexOf("xxxx", StringComparison.OrdinalIgnoreCase) < 0;
            }
        }
    }
}
