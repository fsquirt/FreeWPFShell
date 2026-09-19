using System.Windows;
using FreeWPFShell.Core;

namespace FreeWPFShell
{

    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {

            Services.DebugConsoleService.Instance.Install();
            Services.DebugConsoleService.Log($"[启动] 运行时 {Environment.Version}，工作目录 {Environment.CurrentDirectory}");

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
