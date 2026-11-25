using System.Windows.Media;

namespace AiLink.Views
{
    /// <summary>ファイラーの色（Tsuzuri の Markdown/Theme.cs から、使う5色だけを写したもの）。</summary>
    public static class Palette
    {
        public static readonly Brush Background = Freeze("#FF101827");
        public static readonly Brush Text = Freeze("#FFDDDFE4");
        public static readonly Brush Muted = Freeze("#FF93979F");
        public static readonly Brush Divider = Freeze("#FF31384A");
        public static readonly Brush Selection = Freeze("#FF26457A");

        private static Brush Freeze(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
