using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using YouShell.Models;
using YouShell.Repositories;
using YouShell.UserForm;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// 首页（主机列表）。业务逻辑在 WelcomePageViewModel，Code-behind 仅注入 UI 回调。
    /// </summary>
    public sealed partial class WelcomePage : UserControl
    {
        public WelcomePageViewModel ViewModel { get; }

        /// <summary>由 MainWindow 注入：请求打开一个 SSH 会话。</summary>
        public Action<SshConnectionInfo>? OpenSessionRequested { get; set; }

        /// <summary>由 MainWindow 注入：请求打开 SSH 密钥管理标签页。</summary>
        public Action? OpenKeyManagerRequested { get; set; }

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

        private async void ShowAddConnection()
        {
            var dlg = new AddConnection();
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.None) return;
            ViewModel.LoadHosts();
            if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                OpenSessionRequested?.Invoke(dlg.SavedHostInfo);
        }

        private async void ShowEditConnection(SshConnectionInfo host)
        {
            var dlg = new AddConnection(host);
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.None) return;
            ViewModel.LoadHosts();
            if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                OpenSessionRequested?.Invoke(dlg.SavedHostInfo);
        }

        private async Task<bool> ConfirmDelete(SshConnectionInfo host)
        {
            var r = await ModernMessageBox.ShowAsync(
                $"确定要删除连接 {host.HostName} ({host.IpAddress}) 吗？",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            return r == MessageBoxResult.Yes;
        }

        private async void OpenSettings()
        {
            var settings = new SettingsWindow();
            await settings.ShowAsync();
            ViewModel.LoadHosts();
        }

        private void OpenKeyManager() => OpenKeyManagerRequested?.Invoke();

        private async void ConnectToHost(SshConnectionInfo host)
        {
            try
            {
                var hostWithSecret = await ViewModel.GetAndDecryptAsync(host.Id);
                OpenSessionRequested?.Invoke(hostWithSecret);
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync("连接失败: " + ex.Message);
            }
        }

        private void HostsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (HostsList.SelectedItem is SshConnectionInfo host)
                ViewModel.ConnectCommand.Execute(host);
        }

        private void MnuConnect_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is SshConnectionInfo host) ViewModel.ConnectCommand.Execute(host);
        }

        private void MnuEdit_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is SshConnectionInfo host) ViewModel.EditCommand.Execute(host);
        }

        private void MnuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is SshConnectionInfo host) ViewModel.DeleteCommand.Execute(host);
        }

        private static SshConnectionInfo? DataOf(object sender)
            => (sender as FrameworkElement)?.DataContext as SshConnectionInfo;
    }
}
