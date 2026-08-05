using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.UserForm;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{
    /// <summary>
    /// 系统管理页。数据与命令逻辑已迁移到 SystemManagementViewModel，
    /// Code-behind 负责视图控件与 VM 的对接（面板切换、数据加载触发、UI 回调注入）。
    /// </summary>
    public partial class SystemManagementPage : UserControl, IDisposable
    {
        public SystemManagementViewModel ViewModel { get; }

        private readonly SystemManagementViewModel _vm;
        private bool _loginLoaded, _servicesLoaded, _netLoaded, _cronLoaded;

        public SystemManagementPage(SshSessionService session)
        {
            InitializeComponent();
            _vm = new SystemManagementViewModel(session);
            ViewModel = _vm;
            DataContext = _vm;

            // 绑定数据网格到 VM 集合
            WtmpGrid.ItemsSource = _vm.WtmpRecords;
            BtmpGrid.ItemsSource = _vm.BtmpRecords;
            ServiceGrid.ItemsSource = _vm.ServiceRecords;
            ProcessGrid.ItemsSource = _vm.ProcessRecords;
            NetGrid.ItemsSource = _vm.NetConns;
            CronGrid.ItemsSource = _vm.CronJobs;

            // 注入 UI 回调
            _vm.ShowMessage = (msg, title) => ModernMessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
            _vm.ShowError = (msg, ex) => ModernMessageBox.Show(msg + "\n" + ex, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _vm.Confirm = (msg, title) => ModernMessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _vm.CopyToClipboardAction = t => { try { Clipboard.SetText(t); } catch { } };

            SwitchToTab("Process");
            _ = _vm.RefreshProcessData();
        }

        // ── Tab 导航 ─────────────────────────────────────────────

        private void BtnTabProcess_Click(object sender, RoutedEventArgs e) => SwitchToTab("Process");
        private void BtnTabNet_Click(object sender, RoutedEventArgs e) => SwitchToTab("Net");
        private void BtnTabLogin_Click(object sender, RoutedEventArgs e) => SwitchToTab("Login");
        private void BtnTabService_Click(object sender, RoutedEventArgs e) => SwitchToTab("Service");
        private void BtnTabCron_Click(object sender, RoutedEventArgs e) => SwitchToTab("Cron");

        private static readonly System.Windows.Media.SolidColorBrush s_activeTabBg = new(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30));

        private void SwitchToTab(string tabName)
        {
            PanelProcess.Visibility = tabName == "Process" ? Visibility.Visible : Visibility.Collapsed;
            PanelNet.Visibility = tabName == "Net" ? Visibility.Visible : Visibility.Collapsed;
            PanelLogin.Visibility = tabName == "Login" ? Visibility.Visible : Visibility.Collapsed;
            PanelService.Visibility = tabName == "Service" ? Visibility.Visible : Visibility.Collapsed;
            PanelCron.Visibility = tabName == "Cron" ? Visibility.Visible : Visibility.Collapsed;

            UpdateTabButton(BtnTabProcess, tabName == "Process");
            UpdateTabButton(BtnTabNet, tabName == "Net");
            UpdateTabButton(BtnTabLogin, tabName == "Login");
            UpdateTabButton(BtnTabService, tabName == "Service");
            UpdateTabButton(BtnTabCron, tabName == "Cron");

            if (tabName == "Login" && !_loginLoaded) { _loginLoaded = true; _ = _vm.LoadWtmpAsync(GetWtmpCount()); _ = _vm.LoadBtmpAsync(GetBtmpCount()); }
            if (tabName == "Service" && !_servicesLoaded) { _servicesLoaded = true; _ = _vm.LoadServicesAsync(); }
            if (tabName == "Net" && !_netLoaded) { _netLoaded = true; _ = _vm.LoadNetConnsAsync(); }
            if (tabName == "Cron" && !_cronLoaded) { _cronLoaded = true; _ = _vm.LoadCronJobsAsync(); }
        }

        private static void UpdateTabButton(MicaWPF.Controls.Button btn, bool isActive)
        {
            btn.Background = isActive ? s_activeTabBg : System.Windows.Media.Brushes.Transparent;
            btn.Foreground = isActive ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray;
        }

        // ── 登录记录 ─────────────────────────────────────────────

        private void BtnRefreshWtmp_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadWtmpAsync(GetWtmpCount());
        private void BtnRefreshBtmp_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadBtmpAsync(GetBtmpCount());

        /// <summary>解析"成功登录条数"输入框，无效则用默认 100。</summary>
        private int GetWtmpCount()
            => int.TryParse(TxtWtmpCount.Text, out int c) && c > 0 ? c : 100;

        /// <summary>解析"失败登录条数"输入框，无效则用默认 100。</summary>
        private int GetBtmpCount()
            => int.TryParse(TxtBtmpCount.Text, out int c) && c > 0 ? c : 100;
        private void BtnExportWtmp_Click(object sender, RoutedEventArgs e) => ExportCsv(_vm.BuildWtmpCsvAsync, "登录记录");
        private void BtnExportBtmp_Click(object sender, RoutedEventArgs e) => ExportCsv(_vm.BuildBtmpCsvAsync, "登录失败记录");

        private async void ExportCsv(Func<Task<string>> buildCsv, string title)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}",
                Title = $"导出{title}"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string content = await buildCsv();
                await File.WriteAllTextAsync(dlg.FileName, content, System.Text.Encoding.UTF8);
                ModernMessageBox.Show($"已导出到:\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 服务管理 ─────────────────────────────────────────────

        private void BtnRefreshServices_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadServicesAsync();
        private void TxtServiceSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.ServiceSearch = TxtServiceSearch.Text;

        private void CtxServiceStart_Click(object sender, RoutedEventArgs e) => _ = _vm.ServiceActionCommand.ExecuteAsync("start");
        private void CtxServiceStop_Click(object sender, RoutedEventArgs e) => _ = _vm.ServiceActionCommand.ExecuteAsync("stop");
        private void CtxServiceRestart_Click(object sender, RoutedEventArgs e) => _ = _vm.ServiceActionCommand.ExecuteAsync("restart");
        private void CtxServiceLog_Click(object sender, RoutedEventArgs e) => OpenServiceLog();

        private async void OpenServiceLog()
        {
            if (_vm.SelectedService == null) return;
            await _vm.ViewServiceLogCommand.ExecuteAsync(null);
            if (string.IsNullOrEmpty(_vm.LogContent)) return;
            try
            {
                string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", "logs");
                if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                string localPath = Path.Combine(localDir, _vm.LogFileName);
                await File.WriteAllTextAsync(localPath, _vm.LogContent, System.Text.Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad", $"\"{localPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"打开日志失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 进程管理 ─────────────────────────────────────────────

        private void BtnRefreshProcess_Click(object sender, RoutedEventArgs e) => _ = _vm.RefreshProcessData();
        private void TxtProcessSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.ProcessSearch = TxtProcessSearch.Text;

        private void CtxKill_Click(object sender, RoutedEventArgs e) => _ = _vm.KillProcessCommand.ExecuteAsync(15);
        private void CtxKillForce_Click(object sender, RoutedEventArgs e) => _ = _vm.KillProcessCommand.ExecuteAsync(9);
        private void CtxKillAll_Click(object sender, RoutedEventArgs e) => _ = _vm.KillAllCommand.ExecuteAsync(15);
        private void CtxKillAllForce_Click(object sender, RoutedEventArgs e) => _ = _vm.KillAllCommand.ExecuteAsync(9);
        private void CtxCopyPid_Click(object sender, RoutedEventArgs e) => _vm.CopyPidCommand.Execute(null);

        // ── 网络连接 ─────────────────────────────────────────────

        private void BtnRefreshNet_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadNetConnsAsync();
        private void TxtNetSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.NetSearch = TxtNetSearch.Text;
        private void CtxKillNet_Click(object sender, RoutedEventArgs e) => _ = _vm.KillNetCommand.ExecuteAsync(15);
        private void CtxKillNetForce_Click(object sender, RoutedEventArgs e) => _ = _vm.KillNetCommand.ExecuteAsync(9);

        // ── Cron ─────────────────────────────────────────────────

        private void BtnRefreshCron_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadCronJobsAsync();
        private void TxtCronSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.CronSearch = TxtCronSearch.Text;

        private void CtxCronEnable_Click(object sender, RoutedEventArgs e) => _ = _vm.ToggleCronCommand.ExecuteAsync(true);
        private void CtxCronDisable_Click(object sender, RoutedEventArgs e) => _ = _vm.ToggleCronCommand.ExecuteAsync(false);
        private void CtxCronDelete_Click(object sender, RoutedEventArgs e) => _ = _vm.DeleteCronCommand.ExecuteAsync(null);

        private void CmbCronPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCronPreset.SelectedItem is ComboBoxItem item && item.Tag != null)
                _vm.ApplyCronPreset(item.Tag.ToString()!);
        }

        private void BtnAddCron_Click(object sender, RoutedEventArgs e) => _ = _vm.AddCronCommand.ExecuteAsync(null);

        public void Dispose() => _vm.Stop();
    }
}
