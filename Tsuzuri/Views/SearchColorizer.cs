using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Tsuzuri.Markdown;

namespace Tsuzuri.Views
{
    /// <summary>
    /// 検索に当たった箇所へ地色を敷く（SPEC 12章）。
    ///
    /// 照合そのものは文書全体に対して行うが、塗るのは表示中の行だけ。
    /// AvalonEdit が見えている行に対してのみこれを呼ぶため。
    /// </summary>
    public sealed class SearchColorizer : DocumentColorizingTransformer
    {
        private readonly MarkdownView _owner;

        public SearchColorizer(MarkdownView owner)
        {
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var regex = _owner.SearchRegex;
            if (regex == null || line.Length == 0) return;

            var text = _owner.Document.GetText(line);

            try
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (match.Length == 0) continue;   // a* のような空一致は塗らない

                    var from = line.Offset + match.Index;
                    var to = from + match.Length;
                    if (to > line.EndOffset) continue;

                    ChangeLinePart(from, to,
                        element => element.TextRunProperties.SetBackgroundBrush(Theme.SearchHit));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 時間切れの行は塗らない。描画の途中で落とさないことを優先する。
            }
        }
    }
}
