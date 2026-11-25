using System;
using System.Runtime.InteropServices;

namespace AiLink.Views
{
    /// <summary>
    /// プロセスの親子を見る（2026-11-25）。タブの PowerShell の下で AI（codex）が動いているかを確かめるのに使う
    /// （AI が終わったあとの PowerShell に伝言を打ち込むと、コマンドとして動いてしまうため）。
    /// </summary>
    public static class ProcessTree
    {
        /// <summary>parent の子プロセスが1つでも動いているか（conhost.exe・OpenConsole.exe は数えない）。調べられなければ false（打たない側に倒す）。</summary>
        public static bool HasChild(int parent)
        {
            if (parent <= 0) return false;
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return false;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snapshot, ref entry)) return false;
                do
                {
                    if (entry.th32ParentProcessID == (uint)parent
                        && !string.Equals(entry.szExeFile, "conhost.exe", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(entry.szExeFile, "OpenConsole.exe", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                while (Process32Next(snapshot, ref entry));
                return false;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
