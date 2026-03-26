using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreeWPFShell.Share;
using FreeWPFShell.UserForm;
using System;

namespace FreeWPFShell.Pages
{
    public partial class WelcomePage : UserControl
    {
        private SshManager.SshConnectionManager _sshManager = new();

        public WelcomePage()
        {
            InitializeComponent();
            try
            {
                _sshManager = new SshManager.SshConnectionManager();
                LoadHosts();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("初始化主机列表失败: " + ex.Message);
            }
        }

        public void LoadHosts()
        {
            try
            {
                _sshManager = new SshManager.SshConnectionManager(); // Force reload from JSON to sync memory instances
                var hosts = _sshManager.GetAllHosts();
                HostsList.ItemsSource = hosts;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("加载主机列表失败: " + ex.Message);
            }
        }

        private void BtnAddConnection_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AddConnection();
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                LoadHosts();
                if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                {
                    var mainForm = Window.GetWindow(this) as MainForm;
                    mainForm?.OpenSession(dlg.SavedHostInfo);
                }
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow();
            settings.Owner = Window.GetWindow(this);
            settings.ShowDialog();
            LoadHosts(); // Refresh the list in case locks changed visibility
        }

        private void HostsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HostsList.SelectedItem is SshManager.SshConnectionInfo selectedHost)
            {
                ConnectToHost(selectedHost);
            }
        }

        private void CtxConnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SshManager.SshConnectionInfo host)
            {
                ConnectToHost(host);
            }
        }

        private void CtxEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SshManager.SshConnectionInfo host)
            {
                var dlg = new AddConnection(host);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true)
                {
                    LoadHosts();
                    if (dlg.ConnectAfterSave && dlg.SavedHostInfo != null)
                    {
                        var mainForm = Window.GetWindow(this) as MainForm;
                        mainForm?.OpenSession(dlg.SavedHostInfo);
                    }
                }
            }
        }

        private void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SshManager.SshConnectionInfo host)
            {
                var res = ModernMessageBox.Show($"确定要删除连接 {host.HostName} ({host.IpAddress}) 吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    try
                    {
                        _sshManager.DeleteHost(host.Id);
                        LoadHosts();
                    }
                    catch (Exception ex)
                    {
                        ModernMessageBox.Show("删除失败: " + ex.Message);
                    }
                }
            }
        }

        private void ConnectToHost(SshManager.SshConnectionInfo host)
        {
            try
            {
                var hostWithSecret = System.Threading.Tasks.Task.Run(
                    () => _sshManager.GetHostAndDecryptAsync(host.Id)
                ).GetAwaiter().GetResult();

                var mainForm = Window.GetWindow(this) as MainForm;
                mainForm?.OpenSession(hostWithSecret);
            }
            catch (UnauthorizedAccessException)
            {
                // User cancelled authentication — do nothing
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("连接失败: " + ex.Message);
            }
        }
    }
}
