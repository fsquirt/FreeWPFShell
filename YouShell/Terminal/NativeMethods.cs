using System;
using System.Runtime.InteropServices;

namespace YouShell.Terminal
{
    /// <summary>
    /// 对 Microsoft.Terminal.Control.dll（flat C ABI）及 Win32 的 P/Invoke 声明。
    /// 与 Microsoft.Terminal.Wpf.NativeMethods 保持一致，另补充 WinUI 3 宿主所需的
    /// SetWindowPos / ShowWindow 等 Win32 API。
    /// </summary>
    internal static class NativeMethods
    {
        public const int USER_DEFAULT_SCREEN_DPI = 96;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void ScrollCallback(int viewTop, int viewHeight, int bufferSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void WriteCallback([In, MarshalAs(UnmanagedType.LPWStr)] string data);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        public static extern void AvoidBuggyTSFConsoleFlags();

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = false)]
        public static extern void CreateTerminal(IntPtr parent, out IntPtr hwnd, out IntPtr terminal);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSendOutput(IntPtr terminal, string lpdata);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = false)]
        public static extern void TerminalTriggerResize(IntPtr terminal, int width, int height, out TilSize dimensions);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = false)]
        public static extern void TerminalTriggerResizeWithDimension(IntPtr terminal, TilSize dimensions, out TilSize dimensionsInPixels);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = false)]
        public static extern void TerminalCalculateResize(IntPtr terminal, int width, int height, out TilSize dimensions);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalDpiChanged(IntPtr terminal, int newDpi);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalRegisterScrollCallback(IntPtr terminal, [MarshalAs(UnmanagedType.FunctionPtr)] ScrollCallback callback);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalRegisterWriteCallback(IntPtr terminal, [MarshalAs(UnmanagedType.FunctionPtr)] WriteCallback callback);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalUserScroll(IntPtr terminal, int viewTop);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        [return: MarshalAs(UnmanagedType.LPWStr)]
        public static extern string TerminalGetSelection(IntPtr terminal);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool TerminalIsSelectionActive(IntPtr terminal);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern void DestroyTerminal(IntPtr terminal);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSendKeyEvent(IntPtr terminal, ushort vkey, ushort scanCode, ushort flags, bool keyDown);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSendCharEvent(IntPtr terminal, char ch, ushort scanCode, ushort flags);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSetTheme(IntPtr terminal, [MarshalAs(UnmanagedType.Struct)] TerminalTheme theme, string fontFamily, short fontSize, int newDpi);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSetFocused(IntPtr terminal, bool focused);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSetPixelShaderPath(IntPtr terminal, string path);

        [DllImport("Microsoft.Terminal.Control.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSetPixelShaderImagePath(IntPtr terminal, string path);

        [DllImport("Microsoft.Terminal.Control.dll", CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        public static extern void TerminalSetPixelShaderParams(IntPtr terminal, float p1, float p2, float p3, float p4);

        // ── Win32（WinUI 3 宿主定位/焦点所需） ──────────────────

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, SetWindowPosFlags flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int SW_SHOW = 5;
        public const int SW_HIDE = 0;

        [Flags]
        public enum SetWindowPosFlags : uint
        {
            SWP_NOSIZE = 0x0001,
            SWP_NOMOVE = 0x0002,
            SWP_NOZORDER = 0x0004,
            SWP_NOACTIVATE = 0x0010,
            SWP_SHOWWINDOW = 0x0040,
        }

        // ── 窗口消息常量 ────────────────────────────────────────
        public const uint WM_SETFOCUS = 0x0007;
        public const uint WM_KILLFOCUS = 0x0008;
        public const uint WM_MOUSEACTIVATE = 0x0021;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_CHAR = 0x0102;
        public const uint WM_SYSKEYDOWN = 0x0104;
        public const uint WM_SYSKEYUP = 0x0105;
        public const uint WM_LBUTTONDOWN = 0x0201;

        // ── 窗口子类化（转发键盘输入，等价 WPF HwndHost.MessageHook） ──
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct TilPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TilSize
        {
            public int X;
            public int Y;
        }
    }
}
