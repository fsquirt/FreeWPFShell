using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.UserForm;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{

    public partial class WelcomePage : UserControl
    {
        public WelcomePageViewModel ViewModel { get; }

        public WelcomePage()
        {
            InitializeComponent();
            ViewModel = new WelcomePageViewModel(Core.AppServices.GetService<HostRepository>());
            DataContext = ViewModel;


            ViewModel.AddConnectionRequested = ShowAddConnection;
            ViewModel.EditRequested = ShowEditConnection;
            ViewModel.DeleteConfirm = ConfirmDelete;
            ViewModel.OpenSettingsRequested = OpenSettings;
            ViewModel.OpenKeyManagerRequested = OpenKeyManager;
            ViewModel.ConnectRequested = ConnectToHost;
        }

        private MainForm? MainForm => Window.GetWindow(this) as MainForm;

        private void ShowAddConnection()
        {
            var dlg = new AddConnection { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                ViewModel.LoadHosts();
                if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                    MainForm?.OpenSession(dlg.SavedHostInfo);
            }
        }

        private void ShowEditConnection(SshConnectionInfo host)
        {
            var dlg = new AddConnection(host) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                ViewModel.LoadHosts();
                if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                    MainForm?.OpenSession(dlg.SavedHostInfo);
            }
        }

        private bool ConfirmDelete(SshConnectionInfo host)
        {
            return ModernMessageBox.Show(
                $"确定要删除连接 {host.HostName} ({host.IpAddress}) 吗？",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void OpenSettings()
        {
            var settings = new SettingsWindow { Owner = Window.GetWindow(this) };
            settings.ShowDialog();
            ViewModel.LoadHosts();
        }

        private void OpenKeyManager()
        {
            var keyMgr = new KeyManagerWindow { Owner = Window.GetWindow(this) };
            keyMgr.ShowDialog();
        }

        private void ConnectToHost(SshConnectionInfo host)
        {
            try
            {
                var hostWithSecret = Task.Run(() => ViewModel.GetAndDecryptAsync(host.Id)).GetAwaiter().GetResult();
                MainForm?.OpenSession(hostWithSecret);
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { ModernMessageBox.Show("连接失败: " + ex.Message); }
        }

        private void HostsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HostsList.SelectedItem is SshConnectionInfo host)
                ViewModel.ConnectCommand.Execute(host);
        }
    }
}
