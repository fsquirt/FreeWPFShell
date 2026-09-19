using System.Windows;
using FreeWPFShell.Core;

namespace FreeWPFShell
{

    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {

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
