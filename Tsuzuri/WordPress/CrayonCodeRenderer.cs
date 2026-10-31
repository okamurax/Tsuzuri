using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Tsuzuri.WordPress
{
    /// <summary>
    /// WordPress へ送るときのコードブロック（2026-10-31。#7873）。言語の欄が Crayon の印（```lang:default decode:true）なら、
    /// 読み込んだときの形 <pre class="lang:default decode:true">…</pre> に戻す。サイトの Crayon（Urvanov Syntax Highlighter）が色を付けられるように。
    /// それ以外のコードブロックは、ふつうどおり（<pre><code class="language-…">）。
    /// </summary>
    public sealed class CrayonCodeRenderer : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock block)
        {
            var fenced = block as FencedCodeBlock;
            var info = fenced == null ? null : (fenced.Info + " " + fenced.Arguments).Trim();
            if (string.IsNullOrEmpty(info) || !HtmlToMarkdown.IsCrayonClass(info))
            {
                base.Write(renderer, block);
                return;
            }

            renderer.EnsureLine();
            renderer.Write("<pre class=\"").Write(info).Write("\">");
            renderer.WriteLeafRawLines(block, true, true);
            renderer.WriteLine("</pre>");
        }

        /// <summary>renderer のコードブロックの描き方を、これに替える。</summary>
        public static void Use(HtmlRenderer renderer)
        {
            renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new CrayonCodeRenderer());
        }
    }
}
