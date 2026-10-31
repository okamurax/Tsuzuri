using System.Windows;
using System.Windows.Media;

namespace TableEditor
{
    /// <summary>
    /// 表エディタの色と字（SPEC 8.4.2）。本体（Tsuzuri の App.xaml・Theme.cs）と同じ値を写す。
    /// 本体のプロジェクトはこのプロジェクトを参照する側なので、本体の色を直接は読めない。値を変えたら両方を直す。
    /// </summary>
    internal static class Palette
    {
        /// <summary>本文の地（App.xaml の PageBackground）。セルの地。</summary>
        public static readonly Brush Page = Make(0x10, 0x18, 0x27);

        /// <summary>ツールバーの紺（ChromeBackground）。一番上の段と、行・列の見出しの地。</summary>
        public static readonly Brush Chrome = Make(0x19, 0x23, 0x39);

        /// <summary>本文の字（TextPrimary）。</summary>
        public static readonly Brush Text = Make(0xDD, 0xDF, 0xE4);

        /// <summary>罫線（BorderBrushSoft）。</summary>
        public static readonly Brush Rule = Make(0x31, 0x38, 0x4A);

        /// <summary>打ち足せる空きの行・列（表の外）の地。セルより少し暗い。</summary>
        public static readonly Brush Spare = Make(0x0D, 0x14, 0x22);

        /// <summary>行・列の無い所（表のさらに外）の地。空きより暗い。</summary>
        public static readonly Brush Outside = Make(0x0A, 0x0F, 0x1A);

        /// <summary>空きの行・列の見出しの字（TextMuted）。</summary>
        public static readonly Brush Muted = Make(0x93, 0x97, 0x9F);

        /// <summary>選んだ範囲の地（本文の選択の青を、透かして重ねる）。</summary>
        public static readonly Brush Selection = Make(0x26, 0x45, 0x7A, 0x99);

        /// <summary>小さな画面（寄せを選ぶ）の地（SidebarBackground。本体のエクスポートの画面と同じ）。</summary>
        public static readonly Brush Sidebar = Make(0x16, 0x1E, 0x32);

        /// <summary>小さな画面の枠（FrameBorder）。</summary>
        public static readonly Brush FrameBorder = Make(0x29, 0x31, 0x42);

        /// <summary>小さな画面の見出しの字（SidebarHeaderText）。</summary>
        public static readonly Brush SidebarHeaderText = Make(0xB3, 0xB6, 0xBC);

        /// <summary>載せている項目の地（HoverFill）。</summary>
        public static readonly Brush Hover = Make(0x22, 0x2B, 0x3F);

        /// <summary>選んでいる行・列の見出しの地（PressedFill）。</summary>
        public static readonly Brush HeaderSelected = Make(0x2C, 0x34, 0x46);

        /// <summary>今のセルの枠・選んだ範囲の枠・選んでいる見出しの下線（AccentBrush）。</summary>
        public static readonly Brush Accent = Make(0x4A, 0x7C, 0xE8);

        /// <summary>コピーした範囲の破線と、オートフィルで埋める先の破線。</summary>
        public static readonly Brush Marquee = Make(0xDD, 0xDF, 0xE4);

        public static readonly FontFamily Font = new FontFamily("Yu Gothic UI");

        public static readonly Typeface Cell = new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        /// <summary>見出し行（1行目）の字。やや太字。</summary>
        public static readonly Typeface Bold = new Typeface(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        /// <summary>セルの字の大きさ（10pt）。</summary>
        public const double FontSize = 13.333;

        private static Brush Make(byte r, byte g, byte b, byte a = 0xFF)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
