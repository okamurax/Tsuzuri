using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 画面の状態（SPEC 4章・7章。2026-10-25）。終了したとき（app.json）とブックマークを登録したとき（state.json）に覚え、
    /// 起動したとき・ブックマークを開いたときに、そのとおりに戻す。
    /// 開いていたページの並びと、それぞれのカーソル・スクロール・検索欄、フォーカス、ランチャー、AI の欄。
    /// </summary>
    [DataContract]
    public sealed class ScreenState
    {
        /// <summary>開いていたページ。左から。先頭はファイルのルート。</summary>
        [DataMember(Name = "pages")]
        public List<PageState> Pages { get; set; }

        /// <summary>フォーカスのあったページ（Pages の何番目か）。</summary>
        [DataMember(Name = "focus")]
        public int Focus { get; set; }

        /// <summary>ランチャー（とファイラー）を開いていたか。</summary>
        [DataMember(Name = "launcher", EmitDefaultValue = false)]
        public bool Launcher { get; set; }

        /// <summary>AI の欄を出していたか。出していれば、戻すときに欄を出す（会話のタブは app.json の aiTabs から claude --resume で戻す）。</summary>
        [DataMember(Name = "ai", EmitDefaultValue = false)]
        public bool Ai { get; set; }

        /// <summary>
        /// WordPress の記事を開いていたら、そのサイトの URL と記事の番号（2026-10-31）。一時ファイルは閉じたら消すので、Pages は空にして、
        /// 起動したときに WordPress から読み込み直す。新しい記事（まだ WordPress に作っていない）は覚えない。
        /// </summary>
        [DataMember(Name = "wordpressSite", EmitDefaultValue = false)]
        public string WordPressSite { get; set; }

        [DataMember(Name = "wordpressPost", EmitDefaultValue = false)]
        public int WordPressPost { get; set; }
    }

    /// <summary>開いていた1ページぶん。</summary>
    [DataContract]
    public sealed class PageState
    {
        [DataMember(Name = "file")]
        public string File { get; set; }

        /// <summary>ファイルの中のページ。空ならルート。</summary>
        [DataMember(Name = "page")]
        public string Page { get; set; }

        /// <summary>カーソルの行と桁（1 始まり）。</summary>
        [DataMember(Name = "line")]
        public int Line { get; set; }

        [DataMember(Name = "column")]
        public int Column { get; set; }

        /// <summary>縦のスクロールの位置。</summary>
        [DataMember(Name = "scroll")]
        public double Scroll { get; set; }

        /// <summary>そのページの検索欄に入れていた文字。</summary>
        [DataMember(Name = "search", EmitDefaultValue = false)]
        public string Search { get; set; }
    }
}
