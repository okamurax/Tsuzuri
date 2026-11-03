using System.Windows.Media;

namespace Tsuzuri.Markdown
{
    /// <summary>本文の描画に使う色とサイズ。Mattermost の Indigo テーマに合わせる（SPEC 16章）。</summary>
    public static class Theme
    {
        /// <summary>英数字は同梱の Open Sans（Mattermost と同じ）、日本語は Yu Gothic UI で出す。</summary>
        public static readonly FontFamily BodyFont = new FontFamily(
            new System.Uri("pack://application:,,,/"), "./Fonts/#Open Sans, Yu Gothic UI, Meiryo UI");
        /// <summary>
        /// コードブロック・インラインのコード・表の字（2026-10-29）。日本語も等幅の BIZ UDゴシック（Windows 10 以降に入っている）で、
        /// 半角の英数字がちょうど全角の半分の幅になり、日本語が混ざっても桁がそろう。無ければ MS ゴシック。
        /// 前は Consolas で、日本語は Yu Gothic UI（等幅でない）に落ちていた。
        /// </summary>
        public static readonly FontFamily CodeFont = new FontFamily("BIZ UDGothic, MS Gothic, Consolas");

        public const double BodySize = 16.0;

        /// <summary>見出し1段ぶんの字下げ（px）。ほぼ全角1文字強（SPEC 8.4）。</summary>
        public const double IndentStep = 20.0;

        public static readonly Brush Background  = Freeze("#FF101827");
        public static readonly Brush Text        = Freeze("#FFDDDFE4");
        public static readonly Brush Heading     = Freeze("#FFEEEFF2");
        public static readonly Brush Muted       = Freeze("#FF93979F");
        public static readonly Brush MarkerFaint = Freeze("#FF525863");

        /// <summary>画像の題（画像の上に出す名前）。薄い字（Muted）よりさらに引っ込める。記号（MarkerFaint）よりは読める（2026-11-02）。</summary>
        public static readonly Brush ImageCaption = Freeze("#FF6B717C");
        public static readonly Brush Link        = Freeze("#FF5D89EA");

        /// <summary>同じファイルのサブページへのリンク（`[名](#p3)`）。ほかのリンクと見分けるため緑がかった色にする（2026-11-03）。</summary>
        public static readonly Brush PageLink    = Freeze("#FF4FB89A");

        /// <summary>見出しへのリンク（`[名](#見出し)`・`[名](other.md#見出し)`）。紫にする（2026-11-03）。</summary>
        public static readonly Brush HeadingLink = Freeze("#FFA98BEA");
        public static readonly Brush CodeFore    = Freeze("#FFCE9178");
        public static readonly Brush CodeBack    = Freeze("#FF20283A");
        public static readonly Brush QuoteBar    = Freeze("#FF3D4456");
        public static readonly Brush RuleLine    = Freeze("#FF31384A");

        /// <summary>検索欄やファイラーの段などを分ける線。App.xaml の BorderBrushSoft と同じ色。</summary>
        public static readonly Brush Divider     = Freeze("#FF31384A");

        /// <summary>見出し1・2の下に引く線の色（2026-11-03）。水平線より少し明るい、薄いグレー。</summary>
        public static readonly Brush HeadingRule = Freeze("#FF474C57");

        /// <summary>見出し2の下に引く線の色。見出し1の線より目立たない、背景に近い色。</summary>
        public static readonly Brush HeadingRuleFaint = Freeze("#FF1B2231");

        /// <summary>水平線として実際に描く線の色。</summary>
        public static readonly Brush RuleStroke  = Freeze("#FF31384A");

        /// <summary>行番号。</summary>
        public static readonly Brush LineNumber  = Freeze("#FF525863");

        /// <summary>見出しの深さを示す縦線。</summary>
        public static readonly Brush IndentGuide = Freeze("#FF1E2638");

        /// <summary>検索に当たった箇所の地色。</summary>
        public static readonly Brush SearchHit   = Freeze("#FF5C4A1C");

        /// <summary>選択範囲。</summary>
        public static readonly Brush Selection   = Freeze("#FF26457A");

        /// <summary>正規表現として読めないときの文字色。</summary>
        public static readonly Brush InvalidInput = Freeze("#FFD24B4E");

        /// <summary>未保存の編集があるページの左端に出す色。</summary>
        public static readonly Brush DirtyMark   = Freeze("#FFF5AB00");

        /// <summary>見出しレベル 1-6 の文字サイズ。</summary>
        public static double HeadingSize(int level)
        {
            switch (level)
            {
                case 1:  return 29.0;
                case 2:  return 23.5;
                case 3:  return 20.0;
                case 4:  return 18.0;
                case 5:  return 17.0;
                default: return 16.0;
            }
        }

        private static Brush Freeze(string argb)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
            brush.Freeze();
            return brush;
        }
    }
}
