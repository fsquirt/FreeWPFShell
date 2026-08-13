using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using YouShell.Models;
using YouShell.Services;
using YouShell.UserForm;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// 系统管理页。数据与命令逻辑在 SystemManagementViewModel，Code-behind 负责视图对接。
    /// </summary>
    public sealed partial class SystemManagementPage : UserControl, IDisposable
    {
        private readonly SystemManagementViewModel _vm;
        private bool _loginLoaded, _servicesLoaded, _netLoaded, _cronLoaded;

        public SystemManagementViewModel ViewModel => _vm;

        private static readonly SolidColorBrush s_activeTabBg = new(Windows.UI.Color.FromArgb(255, 0x2D, 0x2D, 0x30));
        private static readonly SolidColorBrush s_transparent = new(Microsoft.UI.Colors.Transparent);
        private static readonly SolidColorBrush s_white = new(Microsoft.UI.Colors.White);
        private static readonly SolidColorBrush s_gray = new(Microsoft.UI.Colors.Gray);

        public SystemManagementPage(SshSessionService session)
        {
            InitializeComponent();
            _vm = new SystemManagementViewModel(session);
            DataContext = _vm;

            _vm.ShowMessage = (msg, title) => _ = ModernMessageBox.ShowAsync(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
            _vm.ShowError = (msg, ex) => _ = ModernMessageBox.ShowAsync(msg + "\n" + ex, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _vm.Confirm = (msg, title) => ConfirmAsync(msg, title);
            _vm.CopyToClipboardAction = t => { try { var dp = new DataPackage(); dp.SetText(t); Clipboard.SetContent(dp); } catch { } };

            SwitchToTab("Process");
            _ = _vm.RefreshProcessData();
        }

        private async Task<bool> ConfirmAsync(string msg, string title)
            => await ModernMessageBox.ShowAsync(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        private static object? DataOf(object sender) => (sender as FrameworkElement)?.DataContext;

        // ── Tab 导航 ─────────────────────────────────────────────

        private void BtnTabProcess_Click(object sender, RoutedEventArgs e) => SwitchToTab("Process");
        private void BtnTabNet_Click(object sender, RoutedEventArgs e) => SwitchToTab("Net");
        private void BtnTabLogin_Click(object sender, RoutedEventArgs e) => SwitchToTab("Login");
        private void BtnTabService_Click(object sender, RoutedEventArgs e) => SwitchToTab("Service");
        private void BtnTabCron_Click(object sender, RoutedEventArgs e) => SwitchToTab("Cron");

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

        private static void UpdateTabButton(Button btn, bool isActive)
        {
            btn.Background = isActive ? s_activeTabBg : s_transparent;
            btn.Foreground = isActive ? s_white : s_gray;
        }

        // ── 登录记录 ─────────────────────────────────────────────

        private void BtnRefreshWtmp_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadWtmpAsync(GetWtmpCount());
        private void BtnRefreshBtmp_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadBtmpAsync(GetBtmpCount());

        private int GetWtmpCount() => int.TryParse(TxtWtmpCount.Text, out int c) && c > 0 ? c : 100;
        private int GetBtmpCount() => int.TryParse(TxtBtmpCount.Text, out int c) && c > 0 ? c : 100;

        private void BtnExportWtmp_Click(object sender, RoutedEventArgs e) => ExportCsv(_vm.BuildWtmpCsvAsync, "登录记录");
        private void BtnExportBtmp_Click(object sender, RoutedEventArgs e) => ExportCsv(_vm.BuildBtmpCsvAsync, "登录失败记录");

        private async void ExportCsv(Func<Task<string>> buildCsv, string title)
        {
            string? path = await PickerHelper.PickSaveFileAsync($"{title}_{DateTime.Now:yyyyMMdd_HHmmss}", ".csv");
            if (path == null) return;
            try
            {
                string content = await buildCsv();
                await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
                await ModernMessageBox.ShowAsync($"已导出到:\n{path}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                string localDir = Path.Combine(Path.GetTempPath(), "YouShell", "logs");
                if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                string localPath = Path.Combine(localDir, _vm.LogFileName);
                await File.WriteAllTextAsync(localPath, _vm.LogContent, System.Text.Encoding.UTF8);
                Process.Start(new ProcessStartInfo("notepad", $"\"{localPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync($"打开日志失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 进程管理 ─────────────────────────────────────────────

        private void BtnRefreshProcess_Click(object sender, RoutedEventArgs e) => _ = _vm.RefreshProcessData();
        private void TxtProcessSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.ProcessSearch = TxtProcessSearch.Text;

        private void CtxKill_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is ProcessItem p) _vm.SelectedProcess = p;
            _ = _vm.KillProcessCommand.ExecuteAsync(15);
        }
        private void CtxKillForce_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is ProcessItem p) _vm.SelectedProcess = p;
            _ = _vm.KillProcessCommand.ExecuteAsync(9);
        }
        private void CtxKillAll_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is ProcessItem p) _vm.SelectedProcess = p;
            _ = _vm.KillAllCommand.ExecuteAsync(15);
        }
        private void CtxKillAllForce_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is ProcessItem p) _vm.SelectedProcess = p;
            _ = _vm.KillAllCommand.ExecuteAsync(9);
        }
        private void CtxCopyPid_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is ProcessItem p) _vm.SelectedProcess = p;
            _vm.CopyPidCommand.Execute(null);
        }

        // ── 网络连接 ─────────────────────────────────────────────

        private void BtnRefreshNet_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadNetConnsAsync();
        private void TxtNetSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.NetSearch = TxtNetSearch.Text;

        private void CtxKillNet_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is NetConnItem c) _vm.SelectedNetConn = c;
            _ = _vm.KillNetCommand.ExecuteAsync(15);
        }
        private void CtxKillNetForce_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is NetConnItem c) _vm.SelectedNetConn = c;
            _ = _vm.KillNetCommand.ExecuteAsync(9);
        }

        // ── Cron ─────────────────────────────────────────────────

        private void BtnRefreshCron_Click(object sender, RoutedEventArgs e) => _ = _vm.LoadCronJobsAsync();
        private void TxtCronSearch_TextChanged(object sender, TextChangedEventArgs e) => _vm.CronSearch = TxtCronSearch.Text;

        private void CtxCronEnable_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is CronJobItem c) _vm.SelectedCronJob = c;
            _ = _vm.ToggleCronCommand.ExecuteAsync(true);
        }
        private void CtxCronDisable_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is CronJobItem c) _vm.SelectedCronJob = c;
            _ = _vm.ToggleCronCommand.ExecuteAsync(false);
        }
        private void CtxCronDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataOf(sender) is CronJobItem c) _vm.SelectedCronJob = c;
            _ = _vm.DeleteCronCommand.ExecuteAsync(null);
        }

        private void CmbCronPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string tag = CmbCronPreset.SelectedIndex switch
            {
                0 => "* * * * *",
                1 => "*/5 * * * *",
                2 => "0 * * * *",
                3 => "0 */5 * * *",
                4 => "0 0 * * *",
                5 => "0 0 */5 * *",
                6 => "0 0 * * 5",
                7 => "0 0 5 * *",
                8 => "0 0 5 5 *",
                _ => "custom",
            };
            _vm.ApplyCronPreset(tag);
        }

        private void BtnAddCron_Click(object sender, RoutedEventArgs e) => _ = _vm.AddCronCommand.ExecuteAsync(null);

        public void Dispose() => _vm.Stop();
    }
}
