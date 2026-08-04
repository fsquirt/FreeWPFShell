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
            _hostRepo = Core.AppServices.GetService<HostRepository>();
            LoadHosts();
        }

        public async void LoadHosts()
        {
            try
            {
                // 强制同步内存和磁盘数据
                _hostRepo.Reload();

                // 先获取基础列表
                var hosts = _hostRepo.GetAll();

                // 强制刷新：先断开连接，再重新绑定
                HostsList.ItemsSource = null;
                HostsList.ItemsSource = hosts;

                // 异步获取地理位置，不阻塞 UI 渲染，也不引发 COM 异常冲突
                await Task.Run(() =>
                {
                    foreach (var host in hosts)
                    {
                        try 
                        { 
                            var geo = IpGeoService.Instance.Query(host.IpAddress);
                            // 由于 SshConnectionInfo 不是 ObservableObject，我们在这里静默更新
                            // 但后续如果需要实时变动，建议也将 SshConnectionInfo 改为 ObservableObject
                            host.SimpleIpGEO = geo.SimpleGeo; 
                        } 
                        catch { }
                    }
                });

                // 再次刷新以显示获取到的地理位置
                HostsList.Items.Refresh();
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine("LoadHosts Error: " + ex.Message);
            }
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

        private void BtnKeyManager_Click(object sender, RoutedEventArgs e)
        {
            var keyMgr = new UserForm.KeyManagerWindow();
            keyMgr.Owner = Window.GetWindow(this);
            keyMgr.ShowDialog();
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
