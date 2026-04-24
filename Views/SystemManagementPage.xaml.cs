using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.Share;

namespace FreeWPFShell.Views
{
    public partial class SystemManagementPage : UserControl, IDisposable
    {
        private readonly SshSessionService _session;
        
        // Login Records
        private readonly ObservableCollection<LoginRecord> _wtmpRecords = new();
        private readonly ObservableCollection<LoginRecord> _btmpRecords = new();
        
        // Services
        private readonly ObservableCollection<ServiceItem> _serviceRecords = new();
        private List<ServiceItem> _allServicesRaw = new();

        // Processes
        private readonly ObservableCollection<ProcessItem> _processes = new();
        private List<ProcessItem> _allProcessesRaw = new();
        private readonly System.Timers.Timer _processRefreshTimer;

        // Network Connections
        private readonly ObservableCollection<NetConnItem> _netConns = new();
        private List<NetConnItem> _allNetConnsRaw = new();

        // Cron Jobs
        private readonly ObservableCollection<CronJobItem> _cronJobs = new();
        private List<CronJobItem> _allCronJobsRaw = new();

        public SystemManagementPage(SshSessionService session)
        {
            InitializeComponent();
            _session = session;
            
            WtmpGrid.ItemsSource = _wtmpRecords;
            BtmpGrid.ItemsSource = _btmpRecords;
            ServiceGrid.ItemsSource = _serviceRecords;
            ProcessGrid.ItemsSource = _processes;
            NetGrid.ItemsSource = _netConns;
            CronGrid.ItemsSource = _cronJobs;

            // Initialize Process Timer
            _processRefreshTimer = new System.Timers.Timer(2000);
            _processRefreshTimer.Elapsed += async (s, e) => await RefreshProcessData();
            _processRefreshTimer.AutoReset = true;

            // Default Tab
            SwitchToTab("Process");

            _ = LoadWtmpAsync();
            _ = LoadBtmpAsync();
            _ = LoadServicesAsync();
            _ = RefreshProcessData();
            _ = LoadNetConnsAsync();
            _ = LoadCronJobsAsync();
        }

        #region Tab Navigation

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

            // Only run process timer when process tab is active
            if (tabName == "Process") _processRefreshTimer.Start();
            else _processRefreshTimer.Stop();
        }

        private void UpdateTabButton(MicaWPF.Controls.Button btn, bool isActive)
        {
            btn.Background = isActive ? new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)) : Brushes.Transparent;
            btn.Foreground = isActive ? Brushes.White : Brushes.Gray;
        }

        #endregion

        #region Login Records

        private async Task LoadWtmpAsync()
        {
            _wtmpRecords.Clear();
            try
            {
                if (!int.TryParse(TxtWtmpCount.Text, out int count) || count <= 0) count = 100;
                var records = await _session.GetLoginRecordsAsync($"/wtmp?count={count}");
                FillGeoAndAdd(records, _wtmpRecords);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取登录记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadBtmpAsync()
        {
            _btmpRecords.Clear();
            try
            {
                if (!int.TryParse(TxtBtmpCount.Text, out int count) || count <= 0) count = 100;
                var records = await _session.GetLoginRecordsAsync($"/btmp?count={count}");
                FillGeoAndAdd(records, _btmpRecords);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取登录失败记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FillGeoAndAdd(List<LoginRecord> records, ObservableCollection<LoginRecord> target)
        {
            var geoService = IpGeoService.Instance;
            foreach (var r in records)
            {
                if (r.Timestamp > 0)
                    r.Time = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                if (!string.IsNullOrEmpty(r.Ip) && r.Ip != "(本地)")
                {
                    try { r.Geo = geoService.Query(r.Ip).SimpleGeo; } catch { r.Geo = ""; }
                }
                else
                {
                    r.Geo = "本地";
                }
                target.Add(r);
            }
        }

        private void BtnRefreshWtmp_Click(object sender, RoutedEventArgs e) => _ = LoadWtmpAsync();
        private void BtnRefreshBtmp_Click(object sender, RoutedEventArgs e) => _ = LoadBtmpAsync();

        private async void BtnExportWtmp_Click(object sender, RoutedEventArgs e) => await ExportCsvAsync("/wtmp", "登录记录");
        private async void BtnExportBtmp_Click(object sender, RoutedEventArgs e) => await ExportCsvAsync("/btmp", "登录失败记录");

        private async Task ExportCsvAsync(string endpoint, string title)
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
                var records = await _session.GetLoginRecordsAsync(endpoint);
                var geoService = IpGeoService.Instance;
                var sb = new StringBuilder();
                sb.AppendLine("登录时间,登录用户,登录来源(IP),IP归属地");
                foreach (var r in records)
                {
                    string time = r.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : r.Time;
                    string geo;
                    if (!string.IsNullOrEmpty(r.Ip) && r.Ip != "(本地)")
                    {
                        try { geo = geoService.Query(r.Ip).SimpleGeo; } catch { geo = ""; }
                    }
                    else
                    {
                        geo = "本地";
                    }
                    sb.AppendLine($"{EscapeCsv(time)},{EscapeCsv(r.User)},{EscapeCsv(r.Ip)},{EscapeCsv(geo)}");
                }
                await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), Encoding.UTF8);
                UserForm.ModernMessageBox.Show($"已导出 {records.Count} 条记录到:\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string? field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }

        #endregion

        #region Services Management

        private async Task LoadServicesAsync()
        {
            try
            {
                _allServicesRaw = await _session.GetServicesAsync();
                ApplyServiceFilter();
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取服务列表失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyServiceFilter()
        {
            string search = TxtServiceSearch.Text.Trim().ToLower();
            bool onlyRunning = TogFilterInactive.IsChecked == true;
            _serviceRecords.Clear();
            foreach (var s in _allServicesRaw.Where(s =>
            {
                if (onlyRunning && (s.ActiveState != "active" || s.SubState != "running")) return false;
                if (string.IsNullOrEmpty(search)) return true;
                return s.Name.ToLower().Contains(search) ||
                       s.Description.ToLower().Contains(search) ||
                       s.ActiveState.ToLower().Contains(search);
            }))
            {
                _serviceRecords.Add(s);
            }
        }

        private void BtnRefreshServices_Click(object sender, RoutedEventArgs e) => _ = LoadServicesAsync();
        private void TogFilterInactive_Click(object sender, RoutedEventArgs e) => ApplyServiceFilter();
        private void TxtServiceSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyServiceFilter();

        private async void CtxServiceStart_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceGrid.SelectedItem is ServiceItem s)
            {
                if (UserForm.ModernMessageBox.Show($"确定要启动服务 {s.Name} 吗？", "启动服务", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    bool ok = await _session.ServiceActionAsync(s.Name, "start");
                    if (ok) UserForm.ModernMessageBox.Show($"服务 {s.Name} 启动命令已发送。");
                    else UserForm.ModernMessageBox.Show($"启动服务 {s.Name} 失败，可能权限不足。");
                    await LoadServicesAsync();
                }
            }
        }

        private async void CtxServiceStop_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceGrid.SelectedItem is ServiceItem s)
            {
                if (UserForm.ModernMessageBox.Show($"确定要停止服务 {s.Name} 吗？", "停止服务", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool ok = await _session.ServiceActionAsync(s.Name, "stop");
                    if (ok) UserForm.ModernMessageBox.Show($"服务 {s.Name} 停止命令已发送。");
                    else UserForm.ModernMessageBox.Show($"停止服务 {s.Name} 失败，可能权限不足。");
                    await LoadServicesAsync();
                }
            }
        }

        private async void CtxServiceRestart_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceGrid.SelectedItem is ServiceItem s)
            {
                if (UserForm.ModernMessageBox.Show($"确定要重启服务 {s.Name} 吗？", "重启服务", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    bool ok = await _session.ServiceActionAsync(s.Name, "restart");
                    if (ok) UserForm.ModernMessageBox.Show($"服务 {s.Name} 重启命令已发送。");
                    else UserForm.ModernMessageBox.Show($"重启服务 {s.Name} 失败，可能权限不足。");
                    await LoadServicesAsync();
                }
            }
        }

        private async void CtxServiceLog_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceGrid.SelectedItem is ServiceItem s)
            {
                string log = await _session.GetServiceLogAsync(s.Name);
                if (string.IsNullOrEmpty(log))
                {
                    UserForm.ModernMessageBox.Show($"无法获取服务 {s.Name} 的日志。", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", _session.SessionId);
                    if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                    string safeName = s.Name.Replace('/', '_').Replace('\\', '_');
                    string localPath = Path.Combine(localDir, $"{safeName}.log");
                    await File.WriteAllTextAsync(localPath, log, Encoding.UTF8);
                    var psi = new System.Diagnostics.ProcessStartInfo("notepad", $"\"{localPath}\"")
                    {
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex)
                {
                    UserForm.ModernMessageBox.Show($"打开日志失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Process Management

        private async Task RefreshProcessData()
        {
            _allProcessesRaw = await _session.GetAllProcessesAsync();
            ApplyProcessFilter();
        }

        private void ApplyProcessFilter()
        {
            Dispatcher.Invoke(() =>
            {
                string search = TxtProcessSearch.Text.Trim().ToLower();
                bool filterEmpty = TogFilterEmpty.IsChecked == true;
                uint? selectedPid = (ProcessGrid.SelectedItem as ProcessItem)?.Pid;

                var filtered = _allProcessesRaw.Where(p =>
                {
                    if (filterEmpty && string.IsNullOrEmpty(p.File)) return false;
                    if (string.IsNullOrEmpty(search)) return true;
                    return p.Pid.ToString().Contains(search) ||
                           p.File.ToLower().Contains(search) ||
                           p.Cmd.ToLower().Contains(search);
                }).ToList();

                _processes.Clear();
                foreach (var p in filtered) _processes.Add(p);

                if (selectedPid.HasValue)
                {
                    var newSelected = _processes.FirstOrDefault(x => x.Pid == selectedPid.Value);
                    if (newSelected != null) ProcessGrid.SelectedItem = newSelected;
                }
            });
        }

        private void TxtProcessSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();
        private void TogFilterEmpty_Click(object sender, RoutedEventArgs e) => ApplyProcessFilter();
        private void BtnRefreshProcess_Click(object sender, RoutedEventArgs e) => _ = RefreshProcessData();

        private async void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p)
            {
                TxtProcessDetail.Text = $"正在获取进程 {p.Pid} 的详细信息...";
                var detail = await _session.GetProcessDetailAsync(p.Pid);
                if (detail != null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"PID (Process ID)      : {detail.pid}");
                    sb.AppendLine($"PPID (Parent PID)     : {detail.ppid}");
                    sb.AppendLine($"UID/GID (用户/组)     : {detail.uid_gid}");
                    sb.AppendLine($"进程状态              : {detail.status}");
                    sb.AppendLine($"优先级与 Nice 值      : {detail.priority_nice}");
                    sb.AppendLine($"CPU 占用时间          : {detail.cpu_time}");
                    sb.AppendLine($"文件描述符 (FD) 数量   : {detail.fd_count}");
                    sb.AppendLine($"内存信息 (statm)       : {detail.mem_info}");
                    sb.AppendLine($"资源限制 (ulimit)      : \n{detail.ulimit}");
                    sb.AppendLine($"CWD (当前工作目录)      : {detail.cwd}");
                    sb.AppendLine($"命令行参数 (argv)      : \n{detail.argv}");
                    sb.AppendLine($"TTY (终端关联)         : {detail.tty}");
                    sb.AppendLine($"\n--- 信号处理 (Status/Sig) ---");
                    sb.AppendLine(detail.signals);
                    sb.AppendLine($"\n--- 寄存器上下文 (Kernel Stack) ---");
                    sb.AppendLine(detail.context);
                    TxtProcessDetail.Text = sb.ToString();
                }
                else TxtProcessDetail.Text = "无法获取详细信息，该进程可能已经结束。";
            }
        }

        private async void CtxKill_Click(object sender, RoutedEventArgs e) => await DoKill(15);
        private async void CtxKillForce_Click(object sender, RoutedEventArgs e) => await DoKill(9);
        private async void CtxKillAll_Click(object sender, RoutedEventArgs e) => await DoKillAll(15);
        private async void CtxKillAllForce_Click(object sender, RoutedEventArgs e) => await DoKillAll(9);

        private async Task DoKill(int sig)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p)
            {
                if (UserForm.ModernMessageBox.Show($"确定要对进程 {p.Pid} ({p.Cmd}) 发送信号 {sig} 吗？", "结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool success = await _session.KillProcessAsync(p.Pid, sig);
                    if (success) UserForm.ModernMessageBox.Show("信号已发送。");
                    else UserForm.ModernMessageBox.Show("操作失败，可能权限不足。");
                }
            }
        }

        private async Task DoKillAll(int sig)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p)
            {
                string procName = !string.IsNullOrEmpty(p.File) ? System.IO.Path.GetFileName(p.File) : p.Cmd.Split(' ')[0];
                if (string.IsNullOrEmpty(procName)) return;

                if (UserForm.ModernMessageBox.Show($"确定要结束所有名为 \"{procName}\" 的进程吗？\n信号: {sig}", "全部结束", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool success = await _session.KillAllProcessesAsync(p.File, sig);
                    if (success) UserForm.ModernMessageBox.Show("批量结束信号已发送。");
                    else UserForm.ModernMessageBox.Show("操作可能部分失败或权限不足。");
                }
            }
        }

        private void CtxCopyPid_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p) Clipboard.SetText(p.Pid.ToString());
        }

        #endregion

        #region Network Connections

        private async Task LoadNetConnsAsync()
        {
            try
            {
                _allNetConnsRaw = await _session.GetNetConnsAsync();
                ApplyNetFilter();
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取网络连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyNetFilter()
        {
            string search = TxtNetSearch.Text.Trim().ToLower();
            bool hideEmpty = TogFilterEmptyNet.IsChecked == true;
            
            _netConns.Clear();
            foreach (var c in _allNetConnsRaw.Where(c =>
            {
                if (hideEmpty && string.IsNullOrEmpty(c.Program)) return false;
                if (string.IsNullOrEmpty(search)) return true;
                return c.Local.ToLower().Contains(search) ||
                       c.Remote.ToLower().Contains(search) ||
                       c.Program.ToLower().Contains(search) ||
                       c.Pid.ToString().Contains(search) ||
                       c.User.ToLower().Contains(search) ||
                       c.Proto.ToLower().Contains(search);
            }))
            {
                _netConns.Add(c);
            }
        }

        private void BtnRefreshNet_Click(object sender, RoutedEventArgs e) => _ = LoadNetConnsAsync();
        private void TxtNetSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyNetFilter();
        private void TogFilterEmptyNet_Click(object sender, RoutedEventArgs e) => ApplyNetFilter();

        private async void CtxKillNet_Click(object sender, RoutedEventArgs e) => await DoKillNet(15);
        private async void CtxKillNetForce_Click(object sender, RoutedEventArgs e) => await DoKillNet(9);

        private async Task DoKillNet(int sig)
        {
            if (NetGrid.SelectedItem is NetConnItem c && c.Pid > 0)
            {
                if (UserForm.ModernMessageBox.Show($"确定要结束进程 {c.Pid} ({c.Program}) 吗？\n信号: {sig}", "结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool success = await _session.KillProcessAsync(c.Pid, sig);
                    if (success) UserForm.ModernMessageBox.Show("信号已发送。");
                    else UserForm.ModernMessageBox.Show("操作失败，可能权限不足。");
                    await LoadNetConnsAsync();
                }
            }
        }

        #endregion

        #region Cron Management

        private async Task LoadCronJobsAsync()
        {
            try
            {
                _allCronJobsRaw = await _session.GetCronJobsAsync();
                ApplyCronFilter();
                string status = await _session.GetCronStatusAsync();
                TxtCronServiceStatus.Text = status;
                TxtCronServiceStatus.Foreground = status == "运行中" ? Brushes.LimeGreen : Brushes.Gray;
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取计划任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyCronFilter()
        {
            string search = TxtCronSearch.Text.Trim().ToLower();
            bool hideDisabled = TogFilterDisabledCron.IsChecked == true;
            _cronJobs.Clear();
            foreach (var c in _allCronJobsRaw.Where(c =>
            {
                if (hideDisabled && !c.Enabled) return false;
                if (string.IsNullOrEmpty(search)) return true;
                return c.Schedule.ToLower().Contains(search) ||
                       c.Command.ToLower().Contains(search) ||
                       c.Raw.ToLower().Contains(search);
            }))
            {
                _cronJobs.Add(c);
            }
        }

        private void BtnRefreshCron_Click(object sender, RoutedEventArgs e) => _ = LoadCronJobsAsync();
        private void TxtCronSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyCronFilter();
        private void TogFilterDisabledCron_Click(object sender, RoutedEventArgs e) => ApplyCronFilter();

        private async void CtxCronEnable_Click(object sender, RoutedEventArgs e) => await DoToggleCron(true);
        private async void CtxCronDisable_Click(object sender, RoutedEventArgs e) => await DoToggleCron(false);

        private async Task DoToggleCron(bool enabled)
        {
            if (CronGrid.SelectedItem is CronJobItem c)
            {
                string action = enabled ? "启用" : "禁用";
                if (UserForm.ModernMessageBox.Show($"确定要{action}该计划任务吗？\n\n{c.Schedule}\n{c.Command}", $"{action}任务", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    bool ok = await _session.ToggleCronJobAsync(c.LineIndex, enabled);
                    if (ok) UserForm.ModernMessageBox.Show($"计划任务已{action}。");
                    else UserForm.ModernMessageBox.Show("操作失败，可能权限不足。");
                    await LoadCronJobsAsync();
                }
            }
        }

        private async void CtxCronDelete_Click(object sender, RoutedEventArgs e)
        {
            if (CronGrid.SelectedItem is CronJobItem c)
            {
                if (UserForm.ModernMessageBox.Show($"确定要删除该计划任务吗？\n\n{c.Schedule}\n{c.Command}", "删除任务", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool ok = await _session.RemoveCronJobAsync(c.LineIndex);
                    if (ok) UserForm.ModernMessageBox.Show("计划任务已删除。");
                    else UserForm.ModernMessageBox.Show("删除失败，可能权限不足。");
                    await LoadCronJobsAsync();
                }
            }
        }

        private void CmbCronPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCronPreset.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString()!;
                if (tag != "custom")
                {
                    var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 5)
                    {
                        TxtCronMin.Text = parts[0];
                        TxtCronHour.Text = parts[1];
                        TxtCronDom.Text = parts[2];
                        TxtCronMonth.Text = parts[3];
                        TxtCronDow.Text = parts[4];
                    }
                }
            }
        }

        private async void BtnAddCron_Click(object sender, RoutedEventArgs e)
        {
            string cmd = TxtCronCommand.Text.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                UserForm.ModernMessageBox.Show("命令不能为空。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string min = TxtCronMin.Text.Trim();
            string hour = TxtCronHour.Text.Trim();
            string dom = TxtCronDom.Text.Trim();
            string month = TxtCronMonth.Text.Trim();
            string dow = TxtCronDow.Text.Trim();
            string schedule = $"{min} {hour} {dom} {month} {dow}";

            string raw = $"{schedule} {cmd}";
            bool ok = await _session.AddCronJobAsync(raw);
            if (ok)
            {
                UserForm.ModernMessageBox.Show("计划任务已添加。");
                TxtCronCommand.Clear();
                await LoadCronJobsAsync();
            }
            else
            {
                UserForm.ModernMessageBox.Show("添加失败，请检查 crontab 格式或权限。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        public void Dispose()
        {
            _processRefreshTimer?.Stop();
            _processRefreshTimer?.Dispose();
        }
    }
}
