using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Syntax;

namespace Tsuzuri.WordPress
{
    /// <summary>
    /// WordPress に送る HTML を、本文の Markdown から作る（SPEC 20章）。エディタのアップロードと、全記事の確かめ（tools/wp-check）が同じものを使う
    /// （2026-10-31。確かめが本物と違う処理で確かめていては意味がないので、MainWindow.WordPress.cs から切り出した）。
    /// </summary>
    public static class WordPressHtml
    {
        /// <summary>
        /// WordPress に送るときの Markdown の解釈（2026-10-30 のレビュー）。エディタの解釈（MarkdownParser.Pipeline）から2つ外す。
        /// ・裸の URL を自動でリンクにしない。逃がした文字（a\_b）がリンク先に入って壊れ、行に1つだけの URL（WordPress が埋め込みにする）もただのリンクになったため。
        ///   URL のリンクは &lt;URL&gt; か [文字](URL) で書く（読み込んだ記事はそう書かれる）
        /// ・強調の追加の記法は打消し（~~）だけにする。^（上付き）・==（マーカー）・++（下線）で、本文の 2^10・x==y が化けたため
        /// </summary>
        public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseFootnotes()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UsePreciseSourceLocation()
            .Build();

        public static MarkdownDocument Parse(string markdown)
        {
            return Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);   // Tsuzuri.Markdown（名前空間）と区別する
        }

        /// <summary>読んだ本文（画像の URL を差し替えたあとのもの）を HTML にする。</summary>
        public static string Render(MarkdownDocument document)
        {
            var writer = new StringWriter();
            var renderer = new HtmlRenderer(writer);
            Pipeline.Setup(renderer);
            CrayonCodeRenderer.Use(renderer);   // Crayon の印のコードブロックは <pre class="…"> に戻す
            renderer.Render(document);
            writer.Flush();
            return RestoreShortcodes(writer.ToString());
        }

        public static string FromMarkdown(string markdown)
        {
            return Render(Parse(markdown));
        }

        private static readonly Regex ShortcodeTag = new Regex(@"\[/?[A-Za-z][\w-]*(?:\s[^\[\]]*)?/?\]", RegexOptions.Compiled);

        /// <summary>
        /// ショートコード（[caption width="300"]・[gallery ids="1,2"]）の中の文字参照を戻す。Markdig は本文の " を &amp;quot; にするので、
        /// そのままだと WordPress が属性を読めない（2026-10-30 のレビュー）。
        /// </summary>
        private static string RestoreShortcodes(string html)
        {
            return ShortcodeTag.Replace(html, match => match.Value.IndexOf('&') < 0 ? match.Value : WebUtility.HtmlDecode(match.Value));
        }
    }
}
