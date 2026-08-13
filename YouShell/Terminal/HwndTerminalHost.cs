using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace YouShell.Terminal
{
    /// <summary>
    /// WinUI 3 下的原生 HWND 终端宿主。等价于 WPF 中的 HwndHost + TerminalContainer：
    /// 通过 flat C ABI 调用 Microsoft.Terminal.Control.dll 创建一个 Win32 子窗口，
    /// 并手动用 SetWindowPos 将其定位到占位元素之上（WinUI 3 无 HwndHost，存在 airspace 限制）。
    /// </summary>
    public sealed class HwndTerminalHost : IDisposable
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _terminal = IntPtr.Zero;
        private IntPtr _parentHwnd = IntPtr.Zero;
        private FrameworkElement? _placeholder;
        private Window? _window;
        private int _lastDpi;

        private NativeMethods.ScrollCallback _scrollCallback = null!;
        private NativeMethods.WriteCallback _writeCallback = null!;
        private NativeMethods.SubclassProc _subclassProc = null!;
        private static readonly UIntPtr SubclassId = new(1);

        private ITerminalConnection? _connection;

        /// <summary>终端缓冲区因文本输出滚动时触发。</summary>
        public event EventHandler<(int viewTop, int viewHeight, int bufferSize)>? TerminalScrolled;

        public int Rows { get; private set; }
        public int Columns { get; private set; }

        /// <summary>渲染器是否随控件尺寸自动调整。</summary>
        public bool AutoResize { get; set; } = true;

        public bool IsCreated => _terminal != IntPtr.Zero;

        /// <summary>将宿主挂到指定窗口，并让终端原生窗口覆盖 <paramref name="placeholder"/>。已创建时仅重新绑定占位元素。</summary>
        public void Attach(Window window, FrameworkElement placeholder)
        {
            _window = window;
            _placeholder = placeholder;
            _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            if (!IsCreated)
            {
                NativeMethods.AvoidBuggyTSFConsoleFlags();
                NativeMethods.CreateTerminal(_parentHwnd, out _hwnd, out _terminal);

                _scrollCallback = OnScroll;
                _writeCallback = OnWrite;
                NativeMethods.TerminalRegisterScrollCallback(_terminal, _scrollCallback);
                NativeMethods.TerminalRegisterWriteCallback(_terminal, _writeCallback);

                // 子类化终端 HWND，转发键盘输入并抢占焦点（WinUI 3 无 HwndHost.MessageHook）。
                _subclassProc = SubclassProc;
                NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, SubclassId, UIntPtr.Zero);
            }

            placeholder.SizeChanged += OnPlaceholderSizeChanged;
            if (window.AppWindow is not null)
            {
                window.AppWindow.Changed += OnAppWindowChanged;
            }

            UpdateLayout();
        }

        /// <summary>解除与占位元素的绑定并隐藏原生窗口，但保留终端实例（切 Tab 不销毁，避免缓冲丢失）。</summary>
        public void Detach()
        {
            if (_window?.AppWindow is not null)
            {
                _window.AppWindow.Changed -= OnAppWindowChanged;
            }

            if (_placeholder != null)
            {
                _placeholder.SizeChanged -= OnPlaceholderSizeChanged;
            }

            _placeholder = null;
            _window = null;

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            }
        }

        /// <summary>设置终端后端连接。</summary>
        public ITerminalConnection? Connection
        {
            get => _connection;
            set
            {
                if (_connection != null)
                {
                    _connection.TerminalOutput -= OnConnectionOutput;
                }

                // 重置控制台/清屏，见 microsoft/terminal#15062。
                OnConnectionOutput(this, new TerminalOutputEventArgs("\x001bc\x1b]104\x1b\\"));

                bool wasNull = _connection == null;
                _connection = value;

                if (_connection != null)
                {
                    if (wasNull)
                    {
                        OnConnectionOutput(this, new TerminalOutputEventArgs("\x1b[?25h")); // 显示光标
                    }

                    _connection.TerminalOutput += OnConnectionOutput;
                    _connection.Start();
                }
                else
                {
                    OnConnectionOutput(this, new TerminalOutputEventArgs("\x1b[?25l")); // 隐藏光标
                }
            }
        }

        public void SetTheme(TerminalTheme theme, string fontFamily, short fontSize)
        {
            if (_terminal == IntPtr.Zero) return;
            NativeMethods.TerminalSetTheme(_terminal, theme, fontFamily, fontSize, CurrentDpi);
        }

        public void SetPixelShaderPath(string? path)
        {
            if (_terminal != IntPtr.Zero) NativeMethods.TerminalSetPixelShaderPath(_terminal, path ?? "");
        }

        public void SetPixelShaderImagePath(string? path)
        {
            if (_terminal != IntPtr.Zero) NativeMethods.TerminalSetPixelShaderImagePath(_terminal, path ?? "");
        }

        public void SetPixelShaderParams(float p1, float p2, float p3, float p4)
        {
            if (_terminal != IntPtr.Zero) NativeMethods.TerminalSetPixelShaderParams(_terminal, p1, p2, p3, p4);
        }

        public string GetSelectedText()
        {
            if (_terminal != IntPtr.Zero && NativeMethods.TerminalIsSelectionActive(_terminal))
            {
                return NativeMethods.TerminalGetSelection(_terminal);
            }

            return string.Empty;
        }

        public void UserScroll(int viewTop)
        {
            if (_terminal != IntPtr.Zero) NativeMethods.TerminalUserScroll(_terminal, viewTop);
        }

        public void FocusTerminal()
        {
            if (_hwnd != IntPtr.Zero) NativeMethods.SetFocus(_hwnd);
        }

        // ── 键盘输入转发（等价 WPF TerminalContainer_MessageHook） ──

        private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (_terminal != IntPtr.Zero && hWnd == _hwnd)
            {
                switch (uMsg)
                {
                    case NativeMethods.WM_SETFOCUS:
                        NativeMethods.TerminalSetFocused(_terminal, true);
                        break;
                    case NativeMethods.WM_KILLFOCUS:
                        NativeMethods.TerminalSetFocused(_terminal, false);
                        break;
                    case NativeMethods.WM_MOUSEACTIVATE:
                        NativeMethods.SetFocus(_hwnd);
                        break;
                    case NativeMethods.WM_LBUTTONDOWN:
                        // 点击终端抢占键盘焦点（原生 WndProc 继续处理选区）
                        NativeMethods.SetFocus(_hwnd);
                        break;
                    case NativeMethods.WM_SYSKEYDOWN:
                    case NativeMethods.WM_KEYDOWN:
                        UnpackKeyMessage(wParam, lParam, out ushort vkey, out ushort scanCode, out ushort flags);
                        NativeMethods.TerminalSendKeyEvent(_terminal, vkey, scanCode, flags, true);
                        break;
                    case NativeMethods.WM_SYSKEYUP:
                    case NativeMethods.WM_KEYUP:
                        UnpackKeyMessage(wParam, lParam, out ushort vkey2, out ushort scanCode2, out ushort flags2);
                        NativeMethods.TerminalSendKeyEvent(_terminal, vkey2, scanCode2, flags2, false);
                        break;
                    case NativeMethods.WM_CHAR:
                        UnpackCharMessage(wParam, lParam, out char ch, out ushort scanCode3, out ushort flags3);
                        NativeMethods.TerminalSendCharEvent(_terminal, ch, scanCode3, flags3);
                        break;
                }
            }

            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private static void UnpackKeyMessage(IntPtr wParam, IntPtr lParam, out ushort vkey, out ushort scanCode, out ushort flags)
        {
            ulong scanCodeAndFlags = ((ulong)lParam.ToInt64() >> 16) & 0xFFFF;
            scanCode = (ushort)(scanCodeAndFlags & 0x00FF);
            flags = (ushort)(scanCodeAndFlags & 0xFF00);
            vkey = (ushort)wParam.ToInt64();
        }

        private static void UnpackCharMessage(IntPtr wParam, IntPtr lParam, out char character, out ushort scanCode, out ushort flags)
        {
            UnpackKeyMessage(wParam, lParam, out ushort vKey, out scanCode, out flags);
            character = (char)vKey;
        }

        // ── 内部 ────────────────────────────────────────────────

        private double RasterizationScale => _placeholder?.XamlRoot?.RasterizationScale ?? 1.0;

        private int CurrentDpi => (int)Math.Round(RasterizationScale * NativeMethods.USER_DEFAULT_SCREEN_DPI);

        private void OnPlaceholderSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLayout();
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            UpdateLayout();
        }

        private void UpdateLayout()
        {
            if (_terminal == IntPtr.Zero || _placeholder == null || _placeholder.XamlRoot == null) return;

            int dpi = CurrentDpi;
            if (_lastDpi != 0 && dpi != _lastDpi)
            {
                NativeMethods.TerminalDpiChanged(_terminal, dpi);
            }
            _lastDpi = dpi;

            double scale = RasterizationScale;
            var transform = _placeholder.TransformToVisual(null);
            var topLeft = transform.TransformPoint(new Point(0, 0));
            var bottomRight = transform.TransformPoint(new Point(_placeholder.ActualWidth, _placeholder.ActualHeight));

            int x = (int)Math.Round(topLeft.X * scale);
            int y = (int)Math.Round(topLeft.Y * scale);
            int w = (int)Math.Round((bottomRight.X - topLeft.X) * scale);
            int h = (int)Math.Round((bottomRight.Y - topLeft.Y) * scale);

            if (w <= 0 || h <= 0) return;

            // 先驱动渲染器 resize（TerminalTriggerResize 内部会 SetWindowPos 到 (0,0,w,h) 并返回行列数）。
            NativeMethods.TerminalTriggerResize(_terminal, w, h, out var dims);
            Rows = dims.Y;
            Columns = dims.X;

            // 再把原生 HWND 覆盖回正确位置 (x,y)，并置于 XAML 合成层之上（airspace）。
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h,
                NativeMethods.SetWindowPosFlags.SWP_SHOWWINDOW);

            _connection?.Resize((uint)dims.Y, (uint)dims.X);
        }

        private void OnConnectionOutput(object? sender, TerminalOutputEventArgs e)
        {
            if (_terminal == IntPtr.Zero || string.IsNullOrEmpty(e.Data)) return;
            NativeMethods.TerminalSendOutput(_terminal, e.Data);
        }

        private void OnScroll(int viewTop, int viewHeight, int bufferSize)
        {
            TerminalScrolled?.Invoke(this, (viewTop, viewHeight, bufferSize));
        }

        private void OnWrite(string data)
        {
            _connection?.WriteInput(data);
        }

        public void Dispose()
        {
            Detach();

            if (_terminal != IntPtr.Zero)
            {
                if (_hwnd != IntPtr.Zero && _subclassProc != null)
                {
                    NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
                }

                NativeMethods.DestroyTerminal(_terminal);
                _terminal = IntPtr.Zero;
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
