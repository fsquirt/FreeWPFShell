using System.Runtime.InteropServices;

namespace YouShell.Terminal
{
    /// <summary>终端光标样式。</summary>
    public enum CursorStyle : uint
    {
        BlinkingBlock = 0,
        BlinkingBlockDefault = 1,
        SteadyBlock = 2,
        BlinkingUnderline = 3,
        SteadyUnderline = 4,
        BlinkingBar = 5,
        SteadyBar = 6,
    }

    /// <summary>
    /// 终端颜色主题结构体。
    /// 与 HwndTerminal.hpp 保持内存布局一致，颜色采用 Win32 COLORREF (BGR) 格式。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TerminalTheme
    {
        public uint DefaultBackground;
        public uint DefaultForeground;
        public uint DefaultSelectionBackground;
        public CursorStyle CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        public uint[] ColorTable;
    }
}
