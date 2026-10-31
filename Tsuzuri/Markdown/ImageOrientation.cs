using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tsuzuri.Markdown
{
    /// <summary>
    /// 写真の向き（EXIF の Orientation。2026-10-18）。
    ///
    /// スマホやカメラの写真は、画素は撮ったときの向きのまま保存し、「表示するときに 90° 回す」などの印だけを付けていることが多い。
    /// エクスプローラーやフォトはこの印を見て正しい向きで出すが、WPF の画像の読み込みは見ないので、本文に入れると回って見えた。
    /// 本文の表示（MarkdownView）と Word への書き出し（DocxExporter）で、この印に従って向きを直す。ファイルそのものは書き換えない。
    /// </summary>
    internal static class ImageOrientation
    {
        /// <summary>画像の向きの印（1〜8）。無い・読めないなら 1（そのまま）。</summary>
        public static int Read(BitmapFrame frame)
        {
            try
            {
                var metadata = frame.Metadata as BitmapMetadata;
                if (metadata == null) return 1;

                var value = metadata.GetQuery("System.Photo.Orientation");
                if (value == null) return 1;

                var orientation = Convert.ToInt32(value);
                return orientation >= 1 && orientation <= 8 ? orientation : 1;
            }
            catch (Exception)
            {
                // 印を持たない形式（PNG など）や、読めない印。
                return 1;
            }
        }

        /// <summary>
        /// 印に従って向きを直した画像。1 ならそのまま返す。
        /// 2: 左右反転、3: 180°、4: 上下反転、5: 左右反転して 270°、6: 90°、7: 左右反転して 90°、8: 270°（時計回り）。
        /// </summary>
        public static BitmapSource Apply(BitmapSource source, int orientation)
        {
            if (orientation <= 1 || orientation > 8) return source;

            var transform = new TransformGroup();
            if (orientation == 2 || orientation == 5 || orientation == 7) transform.Children.Add(new ScaleTransform(-1, 1));
            if (orientation == 4) transform.Children.Add(new ScaleTransform(1, -1));

            switch (orientation)
            {
                case 3: transform.Children.Add(new RotateTransform(180)); break;
                case 5: transform.Children.Add(new RotateTransform(270)); break;
                case 6: transform.Children.Add(new RotateTransform(90)); break;
                case 7: transform.Children.Add(new RotateTransform(90)); break;
                case 8: transform.Children.Add(new RotateTransform(270)); break;
            }

            var result = new TransformedBitmap(source, transform);
            result.Freeze();
            return result;
        }
    }
}
