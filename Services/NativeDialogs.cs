using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FreeWPFShell.Services
{
    internal static class NativeDialogs
    {
        #region 颜色对话框

        [StructLayout(LayoutKind.Sequential)]
        private struct CHOOSECOLOR
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public int rgbResult;      // COLORREF：0x00bbggrr
            public IntPtr lpCustColors;
            public int Flags;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ChooseColorW(ref CHOOSECOLOR lpcc);

        private const int CC_RGBINIT = 0x00000001;
        private const int CC_FULLOPEN = 0x00000002;
        private const int CC_ANYCOLOR = 0x00000100;

        private static readonly int[] CustomColors = new int[16];

        public static bool TryChooseColor(Window owner, Color initial, out Color result)
        {
            result = initial;

            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = new WindowInteropHelper(owner).Handle,
                rgbResult = ToColorRef(initial),
                Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
            };

            var pinned = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
            try
            {
                cc.lpCustColors = pinned.AddrOfPinnedObject();
                if (!ChooseColorW(ref cc))
                {
                    return false;
                }
            }
            finally
            {
                pinned.Free();
            }

            result = FromColorRef(cc.rgbResult);
            return true;
        }

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        private static Color FromColorRef(int value)
            => Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

        #endregion

        #region 字体对话框

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOGFONT
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CHOOSEFONT
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hDC;
            public IntPtr lpLogFont;
            public int iPointSize;     
            public int Flags;
            public int rgbColors;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr hInstance;
            public IntPtr lpszStyle;
            public short nFontType;
            public short missingAlignment; 
            public int nSizeMin;
            public int nSizeMax;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ChooseFontW(ref CHOOSEFONT lpcf);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int LOGPIXELSY = 90;
        private const int FW_NORMAL = 400;
        private const byte DEFAULT_CHARSET = 1;

        private const int CF_SCREENFONTS = 0x00000001;
        private const int CF_INITTOLOGFONTSTRUCT = 0x00000040;
        private const int CF_LIMITSIZE = 0x00002000;
        private const int MinPointSize = 5;
        private const int MaxPointSize = 100;
        public static bool TryChooseFont(Window owner, ref string familyName, ref int pointSize)
        {
            pointSize = Math.Clamp(pointSize, MinPointSize, MaxPointSize);

            var logFont = new LOGFONT
            {
                lfFaceName = familyName ?? string.Empty,
                lfWeight = FW_NORMAL,
                lfCharSet = DEFAULT_CHARSET,
                lfHeight = -MulDiv(pointSize, GetScreenDpiY(), 72),
            };

            IntPtr logFontPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LOGFONT>());
            try
            {
                Marshal.StructureToPtr(logFont, logFontPtr, false);

                var cf = new CHOOSEFONT
                {
                    lStructSize = Marshal.SizeOf<CHOOSEFONT>(),
                    hwndOwner = new WindowInteropHelper(owner).Handle,
                    lpLogFont = logFontPtr,
                    iPointSize = pointSize * 10,      
                    Flags = CF_SCREENFONTS | CF_INITTOLOGFONTSTRUCT | CF_LIMITSIZE,
                    nSizeMin = MinPointSize,
                    nSizeMax = MaxPointSize,
                };

                if (!ChooseFontW(ref cf))
                {
                    return false;
                }

                logFont = Marshal.PtrToStructure<LOGFONT>(logFontPtr);

                int picked = cf.iPointSize > 0
                    ? (int)Math.Round(cf.iPointSize / 10.0)
                    : Math.Max(1, MulDiv(-logFont.lfHeight, 72, GetScreenDpiY()));

                familyName = logFont.lfFaceName;
                pointSize = Math.Clamp(picked, MinPointSize, MaxPointSize);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(logFontPtr);
            }
        }

        private static int GetScreenDpiY()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                return 96;
            }

            try
            {
                int dpi = GetDeviceCaps(dc, LOGPIXELSY);
                return dpi > 0 ? dpi : 96;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }

        private static int MulDiv(int number, int numerator, int denominator)
            => (int)Math.Round((double)number * numerator / denominator);

        #endregion
    }
}
