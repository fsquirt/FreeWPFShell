using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Share;
using FreeWPFShell.UserForm;

namespace FreeWPFShell.Views
{
    public partial class WelcomePage : UserControl
    {
        private readonly HostRepository _hostRepo;

        public WelcomePage()
        {
            InitializeComponent();
            _hostRepo = new HostRepository(new SettingsRepository());
            LoadHosts();
        }

        public void LoadHosts()
        {
            try
            {
                var hosts = _hostRepo.GetAll();
                foreach (var host in hosts)
                    try { host.SimpleIpGEO = IpGeoService.Instance.Query(host.IpAddress).SimpleGeo; } catch { }
                HostsList.ItemsSource = hosts;
            }
            catch (Exception ex) { ModernMessageBox.Show("加载主机列表失败: " + ex.Message); }
        }

        private void BtnAddConnection_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new UserForm.AddConnection();
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                LoadHosts();
                if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                    (Window.GetWindow(this) as MainForm)?.OpenSession(dlg.SavedHostInfo);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow();
            settings.Owner = Window.GetWindow(this);
            settings.ShowDialog();
            LoadHosts();
        }

        private void HostsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HostsList.SelectedItem is SshConnectionInfo host) ConnectToHost(host);
        }

        private void CtxConnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SshConnectionInfo host) ConnectToHost(host);
        }

        private void CtxEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SshConnectionInfo host)
            {
                var dlg = new UserForm.AddConnection(host);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true)
                {
                    LoadHosts();
                    if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                        (Window.GetWindow(this) as MainForm)?.OpenSession(dlg.SavedHostInfo);
                }
            }
        }

        private void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SshConnectionInfo host)
            {
                if (ModernMessageBox.Show($"确定要删除连接 {host.HostName} ({host.IpAddress}) 吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    try { _hostRepo.Delete(host.Id); LoadHosts(); }
                    catch (Exception ex) { ModernMessageBox.Show("删除失败: " + ex.Message); }
                }
            }
        }

        private void ConnectToHost(SshConnectionInfo host)
        {
            try
            {
                var hostWithSecret = Task.Run(() => _hostRepo.GetAndDecryptAsync(host.Id)).GetAwaiter().GetResult();
                (Window.GetWindow(this) as MainForm)?.OpenSession(hostWithSecret);
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { ModernMessageBox.Show("连接失败: " + ex.Message); }
        }
    }
}
