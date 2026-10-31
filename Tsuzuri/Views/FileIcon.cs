using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Tsuzuri.Views
{
    /// <summary>
    /// ファイラーやブックマークの行の頭に置く、種類ごとに色を付けたアイコン（SPEC 16章）。
    ///
    /// 絵文字（📁 📄）は WPF では色が付かず、どれも同じ灰色の形になって見分けにくい。
    /// Visual Studio のソリューション エクスプローラーと同じく、塗らずに 1px の線だけで描く
    /// （塗りつぶしは重く見えたため。2026-10-08）。形はフォルダとファイルの2つだけで、線の色で種類を分ける。
    /// フォントに頼らず図形で描くので、どの Windows でも同じに出る。
    /// 線が画素の真ん中を通るよう、座標は .5 に置く（整数に置くと 2px にまたがってにじむ）。
    /// </summary>
    public static class FileIcon
    {
        /// <summary>フォルダの外形。左上に耳がある。</summary>
        private static readonly Geometry FolderShape = Freeze(Geometry.Parse(
            "M2.5,3.5 A1,1 0 0 1 3.5,2.5 H6.5 L8.5,4.5 H13.5 A1,1 0 0 1 14.5,5.5 V12.5 A1,1 0 0 1 13.5,13.5 H3.5 A1,1 0 0 1 2.5,12.5 Z"));

        /// <summary>フォルダの口の線。耳の下から斜めに上がる（Visual Studio のフォルダと同じ）。</summary>
        private static readonly Geometry FolderFlap = Freeze(Geometry.Parse("M2.5,6.5 H6.5 L8.5,4.5"));

        /// <summary>紙の外形。右上を折ってある。</summary>
        private static readonly Geometry FileShape = Freeze(Geometry.Parse(
            "M3.5,1.5 H9.5 L13.5,5.5 V14.5 H3.5 Z"));

        private static readonly Geometry FileFold = Freeze(Geometry.Parse("M9.5,1.5 V5.5 H13.5"));

        // フォルダは Visual Studio と同じ黄土色。ほかは Mattermost の画面の色を、線でも見えるよう少し明るくしたもの。
        private static readonly Brush Folder   = MakeBrush("#FBDD97");
        private static readonly Brush Markdown = MakeBrush("#6F98F0");   // リンクの青。このエディタの文書
        private static readonly Brush Word     = MakeBrush("#4AB0F0");
        private static readonly Brush Excel    = MakeBrush("#4FC98A");   // オンラインの緑
        private static readonly Brush Pdf      = MakeBrush("#F06A6D");   // 取り込み中の赤
        private static readonly Brush Slides   = MakeBrush("#F0904A");
        private static readonly Brush Image    = MakeBrush("#C08AF0");
        private static readonly Brush Other    = MakeBrush("#C8CAD0");

        /// <summary>16px 四方のアイコン。行の左に置く。</summary>
        public static FrameworkElement Create(string path, bool isFolder)
        {
            var canvas = new System.Windows.Controls.Canvas();
            canvas.Width = 16;
            canvas.Height = 16;
            canvas.VerticalAlignment = VerticalAlignment.Center;
            canvas.SnapsToDevicePixels = true;

            if (isFolder)
            {
                canvas.Children.Add(Outline(FolderShape, Folder));
                canvas.Children.Add(Outline(FolderFlap, Folder));
                return canvas;
            }

            var color = ColorOf(path);
            canvas.Children.Add(Outline(FileShape, color));
            canvas.Children.Add(Outline(FileFold, color));
            return canvas;
        }

        private static Brush ColorOf(string path)
        {
            var extension = (System.IO.Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();

            switch (extension)
            {
                case ".md":
                case ".markdown":
                case ".txt":
                    return Markdown;
                case ".doc":
                case ".docx":
                case ".docm":
                case ".rtf":
                    return Word;
                case ".xls":
                case ".xlsx":
                case ".xlsm":
                case ".csv":
                    return Excel;
                case ".pdf":
                    return Pdf;
                case ".ppt":
                case ".pptx":
                    return Slides;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".bmp":
                case ".webp":
                case ".svg":
                    return Image;
                default:
                    return Other;
            }
        }

        private static Path Outline(Geometry geometry, Brush stroke)
        {
            var shape = new Path();
            shape.Data = geometry;
            shape.Stroke = stroke;
            shape.StrokeThickness = 1;
            shape.StrokeLineJoin = PenLineJoin.Round;
            shape.SnapsToDevicePixels = true;
            return shape;
        }

        private static Brush MakeBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static Geometry Freeze(Geometry geometry)
        {
            geometry.Freeze();
            return geometry;
        }
    }
}
