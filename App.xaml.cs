using System.Windows;
using FreeWPFShell.Core;

namespace FreeWPFShell
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 初始化依赖注入容器，再启动主窗口
            AppServices.Initialize();

            base.OnStartup(e);

            var mainWindow = new Views.MainForm();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppServices.Shutdown();
            base.OnExit(e);
        }
    }
}
