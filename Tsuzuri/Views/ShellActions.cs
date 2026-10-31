using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tsuzuri.Views
{
    /// <summary>
    /// ランチャー（SPEC 4章）とファイラー（SPEC 9章）の右クリックで共通に使う操作。
    /// 同じ名前の項目は、どちらで押しても同じ動きにする。
    /// </summary>
    public static class ShellActions
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

        private const uint FileShareAll = 0x1 | 0x2 | 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;   // フォルダも開けるように

        /// <summary>
        /// そのファイル（フォルダ）の本当のパス。割り当てたドライブ（Z:\…）やリンクを通したパスも、実体のパス（\\Mac\… など）に直す。
        /// 濁点の表し方（Mac の分解した形と Windows の合成した形）も合成した形にそろえる。
        /// 同じものを別の書き方で指しているかを比べるときに使う（開いているファイルを守る判定など）。
        /// 尋ねられなければ（無いファイル等）、GetFullPath したものを返す。
        /// </summary>
        public static string CanonicalPath(string path)
        {
            string result;
            if (TryCanonicalPath(path, out result)) return result;

            try
            {
                result = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                result = path ?? string.Empty;
            }

            return Tidy(result);
        }

        /// <summary>
        /// Windows に本当のパスを尋ねられたときだけ true（2026-10-27）。無いファイル・共有が一瞬見えない等で尋ねられなければ false。
        /// CanonicalPath は尋ねられなくても書かれたとおりのパスを返すので、「同じファイルか」を確かめ直すときはこちらを使う
        /// （尋ねられなかった結果を本当のパスと思い込まないように）。
        /// </summary>
        public static bool TryCanonicalPath(string path, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                using (var handle = CreateFile(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
                {
                    if (!handle.IsInvalid)
                    {
                        var buffer = new StringBuilder(1024);
                        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                        if (length > 0 && length < buffer.Capacity) result = buffer.ToString();
                    }
                }
            }
            catch (Exception)
            {
                result = null;
            }

            if (result == null) return false;

            result = Tidy(result);
            return true;
        }

        private static string Tidy(string result)
        {
            // \\?\UNC\server\share → \\server\share、\\?\C:\ → C:\
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) result = @"\\" + result.Substring(8);
            else if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result.Substring(4);

            return result.TrimEnd('\\', '/').Normalize(NormalizationForm.FormC);
        }

        /// <summary>中身が文字でないもの。メモ帳で開いても読めないので、項目を出さない。</summary>
        private static readonly HashSet<string> NotText = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".lnk", ".exe", ".dll", ".com", ".msi", ".url",
        };

        /// <summary>メモ帳で開く項目を出すか。ファイルだけで、中身が文字でないものとフォルダには出さない。</summary>
        public static bool CanOpenInNotepad(string path)
        {
            try
            {
                return File.Exists(path) && !NotText.Contains(Path.GetExtension(path));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <param name="asAdmin">管理者として開く（hosts など、管理者でないと保存できないファイル向け）。Windows の確認（UAC）が出る。</param>
        public static void OpenInNotepad(string path, bool asAdmin, Action<string> status)
        {
            try
            {
                var start = new ProcessStartInfo("notepad.exe", "\"" + path + "\"");
                start.UseShellExecute = true;
                if (asAdmin) start.Verb = "runas";

                Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 確認で「いいえ」を選んだ。やめただけなので知らせない。
            }
            catch (Exception ex)
            {
                status("メモ帳で開けませんでした: " + ex.Message);
            }
        }

        /// <summary>
        /// エクスプローラーで、その項目を選んだ状態で、入っているフォルダを開く。
        /// Windows のエクスプローラーを名指しで、関連付けを通さずに起動する。関連付け（ShellExecute）を通すと、
        /// Directory Opus の「エクスプローラーの置き換え」に横取りされて Opus で開いてしまうため。
        /// </summary>
        public static void RevealInExplorer(string path, Action<string> status)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    status("見つかりません: " + path);
                    return;
                }

                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var start = new ProcessStartInfo(explorer, "/select,\"" + path + "\"");
                start.UseShellExecute = false;
                Process.Start(start);
            }
            catch (Exception ex)
            {
                status("エクスプローラーで開けませんでした: " + ex.Message);
            }
        }
    }
}
