using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YouShell.Services
{
    /// <summary>
    /// 多窗口管理：为次级窗口统一附加材质、沉浸式标题栏与尺寸，并跟踪激活状态与生命周期。
    /// WinUI 3 中每个 <see cref="Window"/> 都独立，材质与标题栏不会自动继承主窗口，
    /// 因此新窗口必须显式设置 <see cref="Window.SystemBackdrop"/> 与 <see cref="Window.ExtendsContentIntoTitleBar"/>。
    /// </summary>
    public static class WindowManager
    {
        /// <summary>主/次级窗口统一初始尺寸（DIP），偏正方形。</summary>
        public const int WindowWidthDips = 900;
        public const int WindowHeightDips = 820;

        private static readonly List<Window> s_secondaryWindows = new();

        /// <summary>当前拥有输入焦点的窗口，用于解析对话框的 XamlRoot。</summary>
        public static Window? ActiveWindow { get; private set; }

        /// <summary>跟踪窗口激活状态，维护 <see cref="ActiveWindow"/>。</summary>
        public static void Track(Window window)
        {
            window.Activated += (_, e) =>
            {
                if (e.WindowActivationState != WindowActivationState.Deactivated)
                    ActiveWindow = window;
            };
        }

        /// <summary>按 DIP 尺寸调整窗口（内部换算物理像素以适配高 DPI）。</summary>
        public static void ResizeTo(Window window, int widthDips, int heightDips)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                double scale = hwnd != IntPtr.Zero ? YouShell.Terminal.NativeMethods.GetDpiForWindow(hwnd) / 96.0 : 1.0;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(widthDips * scale),
                    (int)Math.Round(heightDips * scale)));
            }
            catch { /* 尺寸设置失败不致命 */ }
        }

        /// <summary>
        /// 以独立窗口打开一个页面：附加与主窗口一致的背景材质、沉浸式标题栏与 900×820 初始尺寸；
        /// 关闭时调用 <paramref name="onClosed"/> 释放页面资源。
        /// </summary>
        public static Window OpenSecondary(string title, FrameworkElement content, string backdropType, Action? onClosed = null)
        {
            var window = new Window { Title = title };
            BackdropService.Apply(window, backdropType);

            // 沉浸式标题栏
            var titleBar = new Grid { Height = 48 };
            titleBar.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            });

            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(titleBar, 0);
            Grid.SetRow(content, 1);
            root.Children.Add(titleBar);
            root.Children.Add(content);

            window.Content = root;
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBar);

            ResizeTo(window, WindowWidthDips, WindowHeightDips);

            Track(window);
            s_secondaryWindows.Add(window);

            window.Closed += (_, _) =>
            {
                s_secondaryWindows.Remove(window);
                onClosed?.Invoke();
            };

            window.Activate();
            return window;
        }

        /// <summary>关闭所有次级窗口（主窗口关闭时调用，确保应用进程正常退出）。</summary>
        public static void CloseAll()
        {
            foreach (var window in s_secondaryWindows.ToList())
                window.Close();
            s_secondaryWindows.Clear();
        }
    }
}
