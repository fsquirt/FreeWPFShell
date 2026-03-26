using Microsoft.Terminal.Wpf;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

public class ConPtyConnection : ITerminalConnection, IDisposable
{
    private readonly string _commandLine;
    private uint _cols;
    private uint _rows;

    public ConPtyConnection(string commandLine = "cmd.exe", uint cols = 120, uint rows = 30)
    {
        _commandLine = commandLine;
        _cols = cols;
        _rows = rows;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out IntPtr hRead, out IntPtr hWrite,
        ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int CreatePseudoConsole(COORD size, IntPtr hInput,
        IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(
    string lpApplicationName, string lpCommandLine,
    ref SECURITY_ATTRIBUTES lpProcessAttributes,
    ref SECURITY_ATTRIBUTES lpThreadAttributes,
    bool bInheritHandles, uint dwCreationFlags,
    IntPtr lpEnvironment, string lpCurrentDirectory,
    ref STARTUPINFOEX lpStartupInfo,
    out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer,
        int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer,
        int nNumberOfBytesToRead, out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new IntPtr(0x00020016);

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
    IntPtr _hPC = IntPtr.Zero;
    IntPtr _hPipeIn = IntPtr.Zero;   // 写给子进程
    IntPtr _hPipeOut = IntPtr.Zero;  // 从子进程读
    IntPtr _hPipeInRead = IntPtr.Zero;
    IntPtr _hPipeOutWrite = IntPtr.Zero;
    PROCESS_INFORMATION _pi;
    CancellationTokenSource _cts = new();

    public void Start()
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        // 创建两对管道
        CreatePipe(out _hPipeInRead, out _hPipeIn, ref sa, 0);
        CreatePipe(out _hPipeOut, out _hPipeOutWrite, ref sa, 0);

        // 创建 ConPTY
        var size = new COORD { X = (short)_cols, Y = (short)_rows };
        CreatePseudoConsole(size, _hPipeInRead, _hPipeOutWrite, 0, out _hPC);

        // 设置扩展启动信息（把 ConPTY 塞进去）
        var siEx = new STARTUPINFOEX();
        siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr size2 = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size2);
        siEx.lpAttributeList = Marshal.AllocHGlobal(size2);
        InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref size2);
        UpdateProcThreadAttribute(siEx.lpAttributeList, 0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            _hPC, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero);

        var psa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        var tsa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };

        CreateProcess(null!, _commandLine, ref psa, ref tsa, false,
            EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null!,
            ref siEx, out _pi);

        DeleteProcThreadAttributeList(siEx.lpAttributeList);
        Marshal.FreeHGlobal(siEx.lpAttributeList);

        // 启动读取线程，把子进程输出源源不断发给控件
        Task.Run(() => ReadLoop(_cts.Token));
    }

    void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            if (!ReadFile(_hPipeOut, buf, buf.Length, out int bytesRead, IntPtr.Zero) || bytesRead == 0)
                break;
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, bytesRead);
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text));
        }
    }

    public void WriteInput(string data)
    {
        if (_hPipeIn == IntPtr.Zero) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        WriteFile(_hPipeIn, bytes, bytes.Length, out _, IntPtr.Zero);
    }

    public void Resize(uint rows, uint columns)
    {
        _cols = columns;
        _rows = rows;
        if (_hPC != IntPtr.Zero)
            ResizePseudoConsole(_hPC, new COORD { X = (short)columns, Y = (short)rows });
    }

    public void Close()
    {
        _cts.Cancel();
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        if (_hPipeIn != IntPtr.Zero) { CloseHandle(_hPipeIn); _hPipeIn = IntPtr.Zero; }
        if (_hPipeOut != IntPtr.Zero) { CloseHandle(_hPipeOut); _hPipeOut = IntPtr.Zero; }
        if (_hPipeInRead != IntPtr.Zero) { CloseHandle(_hPipeInRead); _hPipeInRead = IntPtr.Zero; }
        if (_hPipeOutWrite != IntPtr.Zero) { CloseHandle(_hPipeOutWrite); _hPipeOutWrite = IntPtr.Zero; }
    }

    public void Dispose() => Close();
}
