using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tsuzuri.Views
{
    /// <summary>
    /// Windows のアイコン（エクスプローラーと同じもの）。ランチャー（SPEC 4章）とファイラー（SPEC 9章）で使う。
    ///
    /// 取ったアイコンは覚えておき、描き直すたびに Windows へ問い合わせない。
    /// ファイラーの一覧は拡張子ごとに一度だけ尋ねる（中身を読まないので、ファイルの多いフォルダや共有フォルダでも重くならない）。
    /// </summary>
    public static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private const uint ShgfiIcon = 0x100;
        private const uint ShgfiLargeIcon = 0x0;
        private const uint ShgfiSmallIcon = 0x1;
        private const uint ShgfiUseFileAttributes = 0x10;
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeNormal = 0x80;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// ファイルごとにアイコンが違うもの。拡張子だけでは決まらないので、1つずつ尋ねる。
        /// ほかは拡張子が同じなら同じアイコン。
        /// </summary>
        private static readonly HashSet<string> PerFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".scr", ".cpl", ".msc",
        };

        /// <summary>
        /// そのファイル（フォルダ）のアイコン（32px）。ショートカットはリンク先のアイコンになる。
        /// 見つからないものは、拡張子から決まる一般的なアイコン。取れなければ null。ランチャーのマスに使う。
        /// </summary>
        public static ImageSource ForFile(string path)
        {
            var key = "L|" + path;

            ImageSource cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            var exists = Exists(path);
            var image = Query(path, exists ? 0 : FileAttributeNormal,
                ShgfiLargeIcon | (exists ? 0 : ShgfiUseFileAttributes));

            // 見つからないものは覚えない。戻ってきたら本来のアイコンで出したいため。
            if (image != null && exists) Cache[key] = image;
            return image;
        }

        /// <summary>
        /// 一覧の行の頭に置くアイコン（16px）。ファイラーで使う。
        /// フォルダと、ふつうのファイルは拡張子だけで決める（ファイルに触らない）。.exe・.lnk などは1つずつ。
        /// </summary>
        public static ImageSource ForListing(string path, bool isFolder)
        {
            if (isFolder)
                return Remember("S|<folder>", () => Query("folder", FileAttributeDirectory, ShgfiSmallIcon | ShgfiUseFileAttributes));

            var extension = Path.GetExtension(path) ?? string.Empty;

            if (PerFile.Contains(extension))
            {
                var key = "S|" + path;

                ImageSource cached;
                if (Cache.TryGetValue(key, out cached)) return cached;

                var image = Query(path, 0, ShgfiSmallIcon);
                if (image != null) Cache[key] = image;
                return image ?? ForExtension(extension);
            }

            return ForExtension(extension);
        }

        private static ImageSource ForExtension(string extension)
        {
            // 拡張子の無いファイルも、一般的なファイルのアイコンで出す。
            var name = "file" + extension;
            return Remember("S|ext" + extension, () => Query(name, FileAttributeNormal, ShgfiSmallIcon | ShgfiUseFileAttributes));
        }

        /// <summary>.dll・.exe・.ico の中の index 番目のアイコン（32px）。取れなければ null。環境変数は展開して読む。</summary>
        public static ImageSource FromResource(string file, int index)
        {
            var path = Environment.ExpandEnvironmentVariables(file ?? string.Empty);
            var key = "R|" + path + "," + index;

            ImageSource cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            ImageSource image = null;

            try
            {
                if (ExtractIconEx(path, index, out large, out small, 1) > 0 && large != IntPtr.Zero)
                    image = ToImage(large);
            }
            catch (Exception)
            {
                // 取れなければ null。呼んだ側が別のアイコンで出す。
            }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }

            if (image != null) Cache[key] = image;
            return image;
        }

        private static ImageSource Remember(string key, Func<ImageSource> make)
        {
            ImageSource cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            var image = make();
            if (image != null) Cache[key] = image;
            return image;
        }

        private static ImageSource Query(string path, uint attributes, uint flags)
        {
            var info = new ShFileInfo();

            try
            {
                SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf(info), ShgfiIcon | flags);
                return info.hIcon == IntPtr.Zero ? null : ToImage(info.hIcon);
            }
            catch (Exception)
            {
                // アイコンが取れなくても、名前だけで使える。
                return null;
            }
            finally
            {
                if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon);
            }
        }

        private static ImageSource ToImage(IntPtr icon)
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
