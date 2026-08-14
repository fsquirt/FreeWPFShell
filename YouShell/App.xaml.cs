using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace YouShell
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>当前主窗口，供终端宿主等需要 Window 引用的组件使用。</summary>
        public static Window? MainWindow { get; private set; }

        // 调试用：为 WinUI(GUI) 进程分配一个控制台窗口，使 Console.WriteLine 的输出可见。
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 调试用：弹出控制台窗口显示 Console.WriteLine 日志（诊断 SFTP/隧道传输用）
            try { AllocConsole(); } catch { }

            // 初始化依赖注入容器（单例服务：设置/主机/密钥库、IP 地理、隧道管理器）
            Core.AppServices.Initialize();

            MainWindow = new MainWindow();
            Core.UiDispatcher.Initialize(MainWindow.DispatcherQueue);
            Services.WindowManager.Track(MainWindow);
            MainWindow.Activate();
        }
    }
}
