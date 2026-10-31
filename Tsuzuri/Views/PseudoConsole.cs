using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tsuzuri.Views
{
    /// <summary>
    /// Windows の仮想端末（ConPTY）で1つのプログラムを動かす（SPEC 19章。2026-10-24）。
    ///
    /// 窓は作らない。プログラムの出力は、端末向けの制御コードを含む文字列として Output で受け取り、
    /// 打った文字は Write で渡す。描くのは xterm.js（Views/TerminalView.cs）。
    /// Windows 10 1809 からある仕組みで、Windows Terminal・VS Code の端末も同じものを使っている。
    /// </summary>
    public sealed class PseudoConsole : IDisposable
    {
        private IntPtr _console;
        private SafeFileHandle _input;
        private FileStream _inputStream;
        private IntPtr _processHandle;
        private IntPtr _threadHandle;
        private volatile bool _exited;

        /// <summary>
        /// 中のプログラムを入れるジョブ（2026-10-27）。閉じると、中のプログラムが起動したもの（孫）まで終わる。
        /// 前は PowerShell だけを終わらせていて、そこから起動したもの（claude の下で動く node など）が窓の無いまま残りえた。
        /// 作れなければ IntPtr.Zero（前と同じく、PowerShell だけを終わらせる）。
        /// </summary>
        private IntPtr _job;

        // プロセスのハンドルを閉じる（待つ側）のと、終わらせる（Dispose）のが重ならないようにする（2026-10-24）。
        // 重なると、閉じたハンドル（番号が使い回されていれば別のもの）を TerminateProcess に渡しうる。
        private readonly object _gate = new object();

        // 仮想端末のハンドルを閉じる（待つ側・Dispose）のと、大きさを変える（画面のスレッド）のが重ならないようにする（2026-10-27）。
        // 閉じたあとのハンドルを ResizePseudoConsole に渡すと、解放したメモリを触る。
        private readonly object _consoleGate = new object();

        /// <summary>読む側が読み終わった（出力の口が閉じた）。終わった知らせは、残りの出力を出し切ってから出す（2026-10-27）。</summary>
        private readonly ManualResetEvent _readDone = new ManualResetEvent(false);

        /// <summary>
        /// 同梱の新しい ConPTY（ConPty フォルダの conpty.dll と OpenConsole.exe）で動いているか（2026-10-30）。false なら Windows 標準の conhost。
        /// 標準の conhost は古く、プログラムの出力を一度自分の画面に描いてから出し直す段が遅い。同梱のものは Windows Terminal と同じ新しい版。
        /// 使えない（ファイルが無い・起動に失敗した）ときは、黙って標準のものに戻る。
        /// </summary>
        private bool _bundled;

        /// <summary>出力が届いた（読む側のスレッドから呼ばれる）。</summary>
        public event Action<string> Output;

        /// <summary>プログラムが終わり、最後の出力まで Output で渡し終えた（待つ側のスレッドから呼ばれる）。</summary>
        public event Action Exited;

        public bool HasExited
        {
            get { return _exited; }
        }

        /// <summary>終わったときの終了コード。終わっていなければ null。</summary>
        public int? ExitCode { get; private set; }

        /// <param name="commandLine">exe のパスと引数（パスは引用符で囲む）。</param>
        /// <param name="folder">作業フォルダ。UNC（\\Mac\...）でもよい。</param>
        public void Start(string commandLine, string folder, int columns, int rows)
        {
            SafeFileHandle inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            var started = false;

            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
                if (!CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();

                var size = new COORD { X = (short)Math.Max(columns, 10), Y = (short)Math.Max(rows, 5) };
                var console = IntPtr.Zero;
                var result = -1;
                if (BundledConPty.Load())
                {
                    try
                    {
                        result = BundledConPty.Create(size, inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out console);
                    }
                    catch (Exception)
                    {
                        console = IntPtr.Zero;
                    }

                    _bundled = result == 0;
                }

                if (!_bundled)
                {
                    result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out console);
                    if (result != 0) throw new Win32Exception(result);
                }

                _console = console;

                // 仮想端末が自分の側の端を持ったので、こちらでは閉じる（閉じないと、終わったときに読み終わりが来ない）。
                inputRead.Dispose();
                outputWrite.Dispose();

                _input = inputWrite;
                _inputStream = new FileStream(_input, FileAccess.Write);

                StartProcess(commandLine, folder);
                started = true;
            }
            finally
            {
                // 途中で失敗したら、作ったパイプを閉じる（2026-10-27。前は失敗するたびにハンドルが残った）。
                // 仮想端末とジョブは、呼び出し側の Dispose が閉じる。
                if (!started)
                {
                    if (inputRead != null) inputRead.Dispose();
                    if (outputWrite != null) outputWrite.Dispose();
                    if (outputRead != null) outputRead.Dispose();
                    if (_inputStream == null && inputWrite != null) inputWrite.Dispose();
                }
            }

            var reader = new Thread(() => Read(outputRead)) { IsBackground = true };
            reader.Start();

            var waiter = new Thread(Wait) { IsBackground = true };
            waiter.Start();
        }

        /// <summary>打った文字を渡す。終わっていれば何もしない。</summary>
        public void Write(string text)
        {
            if (_exited || _inputStream == null) return;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                _inputStream.Write(bytes, 0, bytes.Length);
                _inputStream.Flush();
            }
            catch (IOException)
            {
                // 終わる間際。
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>欄の大きさ（桁と行）に合わせる。</summary>
        public void Resize(int columns, int rows)
        {
            if (columns < 1 || rows < 1) return;

            lock (_consoleGate)
            {
                if (_console == IntPtr.Zero) return;

                var size = new COORD { X = (short)columns, Y = (short)rows };
                if (_bundled) BundledConPty.Resize(_console, size);
                else ResizePseudoConsole(_console, size);
            }
        }

        /// <summary>終わらせる。中のプログラムも、そこから起動したもの（ジョブに入っているもの）も終わる。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (!_exited && _processHandle != IntPtr.Zero) TerminateProcess(_processHandle, 1);
            }

            // ジョブを閉じると、孫まで終わる（KILL_ON_JOB_CLOSE）。
            var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero) CloseHandle(job);

            if (_inputStream != null)
            {
                try
                {
                    _inputStream.Dispose();
                }
                catch (Exception)
                {
                }

                _inputStream = null;
            }

            // 仮想端末を閉じるのは裏のスレッドでする（2026-10-27）。古い Windows では、ClosePseudoConsole は
            // 中の conhost が終わるまで戻らず、画面のスレッドで呼ぶとエディタが固まりうる。
            // ハンドルはここで取り上げるので、このあとの Resize は何もしない。
            var console = TakeConsole();
            if (console != IntPtr.Zero) ThreadPool.QueueUserWorkItem(delegate { CloseConsole(console); });
        }

        /// <summary>仮想端末を閉じる。作った方（同梱か標準か）の関数で閉じる。</summary>
        private void CloseConsole(IntPtr console)
        {
            if (_bundled) BundledConPty.Close(console);
            else ClosePseudoConsole(console);
        }

        /// <summary>仮想端末のハンドルを取り上げる（閉じるのは1回だけ）。もう無ければ IntPtr.Zero。</summary>
        private IntPtr TakeConsole()
        {
            lock (_consoleGate)
            {
                var console = _console;
                _console = IntPtr.Zero;
                return console;
            }
        }

        private void StartProcess(string commandLine, string folder)
        {
            // 仮想端末を渡す「属性の一覧」を用意する。
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

            var attributes = Marshal.AllocHGlobal(size);
            var initialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                initialized = true;

                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                var startup = new STARTUPINFOEX();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                startup.lpAttributeList = attributes;

                // 標準入出力は「指定なし」と明示する（2026-10-24）。明示しないと、このアプリが持っている標準入出力
                // （Visual Studio から起動したとき等）を中のプログラムが受け継ぎ、出力が欄に来ない（打った字は返るのに、プロンプトも結果も出なかった）。
                // VS Code の端末の土台（node-pty）も同じようにしている。
                startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdInput = IntPtr.Zero;
                startup.StartupInfo.hStdOutput = IntPtr.Zero;
                startup.StartupInfo.hStdError = IntPtr.Zero;

                // 止めた状態で起動し、ジョブに入れてから動かす（2026-10-27）。動き出してから入れると、その間に起動したものがジョブの外に出る。
                _job = CreateKillOnCloseJob();

                PROCESS_INFORMATION info;
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED, IntPtr.Zero, folder, ref startup, out info))
                    throw new Win32Exception();

                // 入れられなければジョブは使わない（前と同じく、PowerShell だけを終わらせる）。
                if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, info.hProcess))
                {
                    var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
                    if (job != IntPtr.Zero) CloseHandle(job);
                }

                if (ResumeThread(info.hThread) == uint.MaxValue)
                {
                    var error = new Win32Exception();
                    TerminateProcess(info.hProcess, 1);
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                    throw error;
                }

                _processHandle = info.hProcess;
                _threadHandle = info.hThread;
            }
            finally
            {
                // 初期化できたときだけ片付ける（2026-10-27。初期化に失敗した一覧を Delete に渡さない）。
                if (initialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }

        /// <summary>
        /// 閉じると中のプロセスを全部終わらせるジョブを作る（2026-10-27）。作れなければ IntPtr.Zero。
        /// 中のプログラムが「ジョブから抜けて」起動するもの（CREATE_BREAKAWAY_FROM_JOB）は抜けてよい（BREAKAWAY_OK）。
        /// 許さないと、抜けようとしたプログラムの起動そのものが失敗する。抜けたものは残りうるが、それは受け入れる。
        /// </summary>
        private static IntPtr CreateKillOnCloseJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                    Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }

            return job;
        }

        private void Read(SafeFileHandle outputRead)
        {
            try
            {
                // UTF-8 の1文字が読む単位の境目で割れても化けないよう、続きを覚えている復号器で読む。
                var decoder = new UTF8Encoding(false).GetDecoder();
                var bytes = new byte[8192];
                var chars = new char[8192 + 4];

                using (var stream = new FileStream(outputRead, FileAccess.Read))
                {
                    while (true)
                    {
                        int count;
                        try
                        {
                            count = stream.Read(bytes, 0, bytes.Length);
                        }
                        catch (Exception)
                        {
                            break;
                        }

                        if (count <= 0) break;

                        var length = decoder.GetChars(bytes, 0, count, chars, 0);
                        var output = Output;
                        if (length > 0 && output != null) output(new string(chars, 0, length));
                    }
                }
            }
            finally
            {
                _readDone.Set();
            }
        }

        private void Wait()
        {
            WaitForSingleObject(_processHandle, INFINITE);

            lock (_gate)
            {
                uint code;
                if (GetExitCodeProcess(_processHandle, out code)) ExitCode = unchecked((int)code);
                _exited = true;

                CloseHandle(_threadHandle);
                CloseHandle(_processHandle);
                _threadHandle = IntPtr.Zero;
                _processHandle = IntPtr.Zero;
            }

            // 中のプログラムが終わっても、仮想端末を閉じるまで出力の口は閉じない（読む側に終わりが来ない）。
            // ここ（裏のスレッド）で閉じて残りの出力を出し切らせ、読み終わってから終わったと知らせる（2026-10-27）。
            // 前は終わった知らせが最後の出力より先に届くことがあり、最後の出力が欄に出ずに消えた。
            // 読み終わりが来なくても止まったままにはしないよう、3秒で諦める。
            var console = TakeConsole();
            if (console != IntPtr.Zero) CloseConsole(console);
            _readDone.WaitOne(3000);

            var exited = Exited;
            if (exited != null) exited();
        }

        // ---------------------------------------------------------------
        // 同梱の ConPTY（2026-10-30）
        //
        // exe の隣の ConPty\<このプロセスの種類>\conpty.dll を読み、Conpty で始まる関数を使う（中身は kernel32 のものと同じ形）。
        // conpty.dll は、自分の隣か、隣の <Windows の種類>\ から OpenConsole.exe を探し、無ければ黙って Windows 標準の conhost を使う。
        // それだと「同梱で動いている」と思い込むので、OpenConsole.exe があることを先にこちらで確かめる。
        // ---------------------------------------------------------------

        private static class BundledConPty
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate int CreateFunction(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate int ResizeFunction(IntPtr console, COORD size);

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void CloseFunction(IntPtr console);

            private static readonly object Gate = new object();
            private static bool _tried;
            private static CreateFunction _create;
            private static ResizeFunction _resize;
            private static CloseFunction _close;

            /// <summary>読めたか（1回だけ試す。読めなければ、以後は標準のものを使う）。</summary>
            public static bool Load()
            {
                lock (Gate)
                {
                    if (_tried) return _create != null;
                    _tried = true;

                    try
                    {
                        var arch = ArchName(RuntimeInformation.ProcessArchitecture);
                        if (arch == null) return false;

                        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ConPty", arch);
                        var dll = Path.Combine(folder, "conpty.dll");
                        var native = NativeArchName() ?? arch;
                        if (!File.Exists(dll)) return false;
                        if (!File.Exists(Path.Combine(folder, "OpenConsole.exe"))
                            && !File.Exists(Path.Combine(folder, native, "OpenConsole.exe"))) return false;

                        var module = LoadLibraryEx(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                        if (module == IntPtr.Zero) return false;

                        var create = GetProcAddress(module, "ConptyCreatePseudoConsole");
                        var resize = GetProcAddress(module, "ConptyResizePseudoConsole");
                        var close = GetProcAddress(module, "ConptyClosePseudoConsole");
                        if (create == IntPtr.Zero || resize == IntPtr.Zero || close == IntPtr.Zero) return false;

                        _resize = (ResizeFunction)Marshal.GetDelegateForFunctionPointer(resize, typeof(ResizeFunction));
                        _close = (CloseFunction)Marshal.GetDelegateForFunctionPointer(close, typeof(CloseFunction));
                        _create = (CreateFunction)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateFunction));
                        return true;
                    }
                    catch (Exception)
                    {
                        _create = null;
                        return false;
                    }
                }
            }

            public static int Create(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr console)
            {
                return _create(size, input, output, flags, out console);
            }

            public static void Resize(IntPtr console, COORD size)
            {
                _resize(console, size);
            }

            public static void Close(IntPtr console)
            {
                _close(console);
            }

            private static string ArchName(Architecture arch)
            {
                switch (arch)
                {
                    case Architecture.X64: return "x64";
                    case Architecture.Arm64: return "arm64";
                    default: return null;   // 32 ビットでは動かさない（Prefer32Bit は false）
                }
            }

            /// <summary>Windows そのものの種類。x64 のエディタが ARM の Windows の上で動いているときは "arm64"。分からなければ null。</summary>
            private static string NativeArchName()
            {
                try
                {
                    ushort process, native;
                    if (!IsWow64Process2(GetCurrentProcess(), out process, out native)) return null;
                    if (native == IMAGE_FILE_MACHINE_ARM64) return "arm64";
                    if (native == IMAGE_FILE_MACHINE_AMD64) return "x64";
                }
                catch (EntryPointNotFoundException)
                {
                    // Windows 10 1709 より前
                }

                return null;
            }

            private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
            private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
            private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string name);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        }

        // ---------------------------------------------------------------
        // Win32
        // ---------------------------------------------------------------

        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint INFINITE = 0xFFFFFFFF;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        // ジョブの制限（winnt.h のとおり）。SIZE_T・ULONG_PTR は UIntPtr にして、64 ビット（x64・ARM64）でも 32 ビットでも幅が合うようにする。
        // 64 ビットでは JOBOBJECT_EXTENDED_LIMIT_INFORMATION は 144 バイト。
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr console, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr console);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
            [In] ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
    }
}
