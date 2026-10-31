namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 1行の解析結果。装飾を決めるためだけに使う。
    /// 文書そのものは AvalonEdit がプレーンテキストとして持つので、
    /// ここでの解析結果がどうであれファイルの内容には影響しない。
    /// </summary>
    public sealed class LineInfo
    {
        /// <summary>行の生テキスト（改行を含まない）。</summary>
        public string Source;

        public LineKind Kind;

        /// <summary>見出しならレベル 1-6、リストならネストの深さ。</summary>
        public int Level;

        /// <summary>行頭の記号部分。"## " や "- " など。カーソルの無い行では画面から消す。</summary>
        public string Marker;

        /// <summary>Marker を取り除いた本文。</summary>
        public string Content;

        /// <summary>タスクリストのチェック状態。</summary>
        public bool IsChecked;

        /// <summary>
        /// 直前の見出しから決まる見た目の段差（SPEC 8章）。
        /// 見出し自身は「自分より一段浅い」位置、その配下の行は見出しと同じ深さに置く。
        /// ファイルには一切書き込まない。本文の字下げと縦線に使う。
        /// </summary>
        public int Depth;

        /// <summary>
        /// 2行以上にまたがる下線式（Setext）の見出しの、2行目以降。
        /// 見出しとして飾るが、アウトラインでは1行目と合わせて1つの見出しにする。
        /// </summary>
        public bool ContinuesHeading;

        public LineInfo()
        {
            Source = string.Empty;
            Marker = string.Empty;
            Content = string.Empty;
        }

    }
}
