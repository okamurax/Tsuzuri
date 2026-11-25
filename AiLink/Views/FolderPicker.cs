using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AiLink.Views
{
    /// <summary>
    /// フォルダを選ぶ画面（Tsuzuri の LauncherView の PickFolder を写したもの）。.NET Framework の FolderBrowserDialog は古いツリー型なので、
    /// エクスプローラーと同じ形の Windows のファイル選択画面（IFileOpenDialog）を、フォルダを選ぶ設定で呼び出す。
    /// </summary>
    public static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCom
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        private const uint FosPickFolders = 0x20;
        private const uint FosForceFileSystem = 0x40;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int ErrorCancelled = unchecked((int)0x800704C7);

        /// <summary>フォルダを選ぶ。やめたら null。選べなかったら problem にわけを入れて null。</summary>
        public static string Pick(Window owner, string title, out string problem)
        {
            problem = null;
            IFileDialog dialog = null;
            try
            {
                dialog = (IFileDialog)new FileOpenDialogCom();

                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem);
                dialog.SetTitle(title);

                var handle = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                var result = dialog.Show(handle);
                if (result == ErrorCancelled) return null;
                if (result != 0) Marshal.ThrowExceptionForHR(result);

                IShellItem item;
                dialog.GetResult(out item);

                string path;
                item.GetDisplayName(SigdnFileSysPath, out path);
                Marshal.ReleaseComObject(item);

                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }
    }
}
