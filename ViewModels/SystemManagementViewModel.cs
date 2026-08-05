using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Models.Dto;
using FreeWPFShell.Services;
using FreeWPFShell.Share;

namespace FreeWPFShell.ViewModels
{
    /// <summary>
    /// 系统管理页 ViewModel。管理进程/服务/登录记录/网络连接/Cron 五大功能区的
    /// 数据集合、过滤逻辑与操作命令，通过 SshSessionService 调用远端系统信息。
    /// </summary>
    public partial class SystemManagementViewModel : ObservableObject
    {
        private readonly SshSessionService _session;
        private readonly System.Timers.Timer _processTimer;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private bool _isRefreshingProcess;

        /// <summary>确保在 UI 线程上执行集合修改，避免 ObservableCollection 跨线程异常。</summary>
        private void RunOnUiThread(Action action)
        {
            // 测试环境（无 WPF Application）直接执行，避免等待不存在的 UI 消息循环导致死锁
            if (System.Windows.Application.Current == null)
            {
                action();
                return;
            }
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.Invoke(action);
        }

        // 原始数据（用于过滤）
        private List<LoginRecord> _wtmpRaw = new();
        private List<LoginRecord> _btmpRaw = new();
        private List<ServiceItem> _allServicesRaw = new();
        private List<ProcessItem> _allProcessesRaw = new();
        private List<NetConnItem> _allNetConnsRaw = new();
        private List<CronJobItem> _allCronJobsRaw = new();

        // 展示集合
        public ObservableCollection<LoginRecord> WtmpRecords { get; } = new();
        public ObservableCollection<LoginRecord> BtmpRecords { get; } = new();
        public ObservableCollection<ServiceItem> ServiceRecords { get; } = new();
        public ObservableCollection<ProcessItem> ProcessRecords { get; } = new();
        public ObservableCollection<NetConnItem> NetConns { get; } = new();
        public ObservableCollection<CronJobItem> CronJobs { get; } = new();

        // 搜索/过滤
        [ObservableProperty] private string _serviceSearch = string.Empty;
        [ObservableProperty] private string _processSearch = string.Empty;
        [ObservableProperty] private string _netSearch = string.Empty;
        [ObservableProperty] private string _cronSearch = string.Empty;
        // 初始值与 UI 开关默认状态保持一致（默认勾选），确保页面加载时过滤即生效
        [ObservableProperty] private bool _filterInactive = true;
        [ObservableProperty] private bool _filterEmpty = true;
        [ObservableProperty] private bool _filterEmptyNet = true;
        [ObservableProperty] private bool _filterDisabledCron;

        // 选中项
        [ObservableProperty] private ProcessItem? _selectedProcess;
        [ObservableProperty] private NetConnItem? _selectedNetConn;
        [ObservableProperty] private ServiceItem? _selectedService;
        [ObservableProperty] private CronJobItem? _selectedCronJob;

        // Cron 表单
        [ObservableProperty] private string _cronMin = "*";
        [ObservableProperty] private string _cronHour = "*";
        [ObservableProperty] private string _cronDom = "*";
        [ObservableProperty] private string _cronMonth = "*";
        [ObservableProperty] private string _cronDow = "*";
        [ObservableProperty] private string _cronCommand = string.Empty;
        [ObservableProperty] private string _cronServiceStatus = string.Empty;
        [ObservableProperty] private bool _cronStatusRunning;

        // 进程详情文本
        [ObservableProperty] private string _processDetailText = string.Empty;

        // UI 提示
        public Action<string, string>? ShowMessage { get; set; }
        public Action<string, string>? ShowError { get; set; }
        public Func<string, string, bool>? Confirm { get; set; }

        public SystemManagementViewModel(SshSessionService session)
        {
            _session = session;
            _dispatcher = System.Windows.Application.Current?.Dispatcher
                ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _processTimer = new System.Timers.Timer(2000) { AutoReset = true };
            _processTimer.Elapsed += async (s, e) =>
            {
                if (_isRefreshingProcess) return;
                _isRefreshingProcess = true;
                try { await RefreshProcessData(); }
                finally { _isRefreshingProcess = false; }
            };
            _processTimer.Start();
            _ = RefreshProcessData();
        }

        public void Stop() => _processTimer?.Stop();

        // ── 登录记录 ─────────────────────────────────────────────

        public async Task LoadWtmpAsync(int count = 100)
        {
            try
            {
                _wtmpRaw = await _session.GetLoginRecordsAsync($"/wtmp?count={count}");
                ApplyWtmpFilter();
            }
            catch (Exception ex) { ShowError?.Invoke("读取登录记录失败", ex.Message); }
        }

        public async Task LoadBtmpAsync(int count = 100)
        {
            try
            {
                _btmpRaw = await _session.GetLoginRecordsAsync($"/btmp?count={count}");
                ApplyBtmpFilter();
            }
            catch (Exception ex) { ShowError?.Invoke("读取登录失败记录失败", ex.Message); }
        }

        private void ApplyWtmpFilter() => FillGeo(_wtmpRaw, WtmpRecords);
        private void ApplyBtmpFilter() => FillGeo(_btmpRaw, BtmpRecords);

        private void FillGeo(List<LoginRecord> records, ObservableCollection<LoginRecord> target)
        {
            var geoService = IpGeoService.Instance;
            RunOnUiThread(() =>
            {
                target.Clear();
                foreach (var r in records)
                {
                    if (r.Timestamp > 0)
                        r.Time = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    if (!string.IsNullOrEmpty(r.Ip) && r.Ip != "(本地)")
                    {
                        try { r.Geo = geoService.Query(r.Ip).SimpleGeo; } catch { r.Geo = ""; }
                    }
                    else r.Geo = "本地";
                    target.Add(r);
                }
            });
        }

        public static string EscapeCsv(string? field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }

        public async Task<string> BuildWtmpCsvAsync()
        {
            var records = await _session.GetLoginRecordsAsync("/wtmp");
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
                else geo = "本地";
                sb.AppendLine($"{EscapeCsv(time)},{EscapeCsv(r.User)},{EscapeCsv(r.Ip)},{EscapeCsv(geo)}");
            }
            return sb.ToString();
        }

        public async Task<string> BuildBtmpCsvAsync()
        {
            var records = await _session.GetLoginRecordsAsync("/btmp");
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
                else geo = "本地";
                sb.AppendLine($"{EscapeCsv(time)},{EscapeCsv(r.User)},{EscapeCsv(r.Ip)},{EscapeCsv(geo)}");
            }
            return sb.ToString();
        }

        // ── 服务 ─────────────────────────────────────────────────

        public async Task LoadServicesAsync()
        {
            try
            {
                _allServicesRaw = await _session.GetServicesAsync();
                ApplyServiceFilter();
            }
            catch (Exception ex) { ShowError?.Invoke("读取服务列表失败", ex.Message); }
        }

        partial void OnServiceSearchChanged(string value) => ApplyServiceFilter();
        partial void OnFilterInactiveChanged(bool value) => ApplyServiceFilter();

        private void ApplyServiceFilter()
        {
            string search = ServiceSearch.Trim().ToLower();
            bool onlyRunning = FilterInactive;
            RunOnUiThread(() =>
            {
                ServiceRecords.Clear();
                foreach (var s in _allServicesRaw)
                {
                    if (onlyRunning && (s.ActiveState != "active" || s.SubState != "running")) continue;
                    if (!string.IsNullOrEmpty(search) &&
                        !s.Name.ToLower().Contains(search) &&
                        !s.Description.ToLower().Contains(search) &&
                        !s.ActiveState.ToLower().Contains(search)) continue;
                    ServiceRecords.Add(s);
                }
            });
        }

        [RelayCommand]
        private async Task ServiceActionAsync(string? action)
        {
            if (SelectedService == null) return;
            var s = SelectedService;
            string actionName = action ?? "";
            if (actionName is "start" or "stop" or "restart")
            {
                string verb = actionName switch { "start" => "启动", "stop" => "停止", _ => "重启" };
                if (Confirm?.Invoke($"确定要{verb}服务 {s.Name} 吗？", $"{verb}服务") != true) return;
            }
            bool ok = await _session.ServiceActionAsync(s.Name, actionName);
            if (ok) ShowMessage?.Invoke($"服务 {s.Name} {GetActionText(actionName)}命令已发送。", "成功");
            else ShowMessage?.Invoke($"{GetActionText(actionName)}服务 {s.Name} 失败，可能权限不足。", "失败");
            await LoadServicesAsync();
        }

        private static string GetActionText(string action) => action switch
        {
            "start" => "启动",
            "stop" => "停止",
            "restart" => "重启",
            _ => action
        };

        [RelayCommand]
        private async Task ViewServiceLogAsync()
        {
            if (SelectedService == null) return;
            string log = await _session.GetServiceLogAsync(SelectedService.Name);
            if (string.IsNullOrEmpty(log))
            {
                ShowMessage?.Invoke($"无法获取服务 {SelectedService.Name} 的日志。", "日志");
                return;
            }
            LogContent = log;
            LogFileName = $"{SelectedService.Name.Replace('/', '_').Replace('\\', '_')}.log";
        }

        [ObservableProperty] private string _logContent = string.Empty;
        [ObservableProperty] private string _logFileName = string.Empty;

        // ── 进程 ─────────────────────────────────────────────────

        public async Task RefreshProcessData()
        {
            _allProcessesRaw = await _session.GetAllProcessesAsync();
            ApplyProcessFilter();
        }

        partial void OnProcessSearchChanged(string value) => ApplyProcessFilter();
        partial void OnFilterEmptyChanged(bool value) => ApplyProcessFilter();

        private void ApplyProcessFilter()
        {
            string search = ProcessSearch.Trim().ToLower();
            bool filterEmpty = FilterEmpty;
            uint? selectedPid = SelectedProcess?.Pid;
            RunOnUiThread(() =>
            {
                ProcessRecords.Clear();
                foreach (var p in _allProcessesRaw)
                {
                    if (filterEmpty && string.IsNullOrEmpty(p.File)) continue;
                    if (!string.IsNullOrEmpty(search) &&
                        !p.Pid.ToString().Contains(search) &&
                        !p.File.ToLower().Contains(search) &&
                        !p.Cmd.ToLower().Contains(search)) continue;
                    ProcessRecords.Add(p);
                }
            });
            if (selectedPid.HasValue)
            {
                var match = ProcessRecords.FirstOrDefault(x => x.Pid == selectedPid.Value);
                if (match != null) SelectedProcess = match;
            }
        }

        partial void OnSelectedProcessChanged(ProcessItem? value) => _ = ShowProcessDetailAsync();

        private async Task ShowProcessDetailAsync()
        {
            var p = SelectedProcess;
            if (p == null) return;
            ProcessDetailText = $"正在获取进程 {p.Pid} 的详细信息...";
            var detail = await _session.GetProcessDetailAsync(p.Pid);
            if (detail != null)
            {
                var sb = new StringBuilder(1024);
                sb.Append("PID (Process ID)      : ").AppendLine(detail.pid.ToString());
                sb.Append("PPID (Parent PID)     : ").AppendLine(detail.ppid.ToString());
                sb.Append("UID/GID (用户/组)     : ").AppendLine(detail.uid_gid);
                sb.Append("进程状态              : ").AppendLine(detail.status);
                sb.Append("优先级与 Nice 值      : ").AppendLine(detail.priority_nice);
                sb.Append("CPU 占用时间          : ").AppendLine(detail.cpu_time);
                sb.Append("文件描述符 (FD) 数量   : ").AppendLine(detail.fd_count.ToString());
                sb.Append("内存信息 (statm)       : ").AppendLine(detail.mem_info);
                sb.Append("资源限制 (ulimit)      : \n").AppendLine(detail.ulimit);
                sb.Append("CWD (当前工作目录)      : ").AppendLine(detail.cwd);
                sb.Append("命令行参数 (argv)      : \n").AppendLine(detail.argv);
                sb.Append("TTY (终端关联)         : ").AppendLine(detail.tty);
                sb.AppendLine("\n--- 信号处理 (Status/Sig) ---");
                sb.AppendLine(detail.signals);
                sb.AppendLine("\n--- 寄存器上下文 (Kernel Stack) ---");
                sb.AppendLine(detail.context);
                if (SelectedProcess?.Pid == p.Pid) ProcessDetailText = sb.ToString();
            }
            else if (SelectedProcess?.Pid == p.Pid)
            {
                ProcessDetailText = "无法获取详细信息，该进程可能已经结束。";
            }
        }

        [RelayCommand]
        private async Task KillProcessAsync(int signal)
        {
            if (SelectedProcess == null) return;
            if (Confirm?.Invoke($"确定要对进程 {SelectedProcess.Pid} ({SelectedProcess.Cmd}) 发送信号 {signal} 吗？", "结束进程") != true) return;
            bool success = await _session.KillProcessAsync(SelectedProcess.Pid, signal);
            ShowMessage?.Invoke(success ? "信号已发送。" : "操作失败，可能权限不足。", "结束进程");
        }

        [RelayCommand]
        private async Task KillAllAsync(int signal)
        {
            if (SelectedProcess == null) return;
            var p = SelectedProcess;
            string procName = !string.IsNullOrEmpty(p.File) ? Path.GetFileName(p.File) : p.Cmd.Split(' ')[0];
            if (string.IsNullOrEmpty(procName)) return;
            if (Confirm?.Invoke($"确定要结束所有名为 \"{procName}\" 的进程吗？\n信号: {signal}", "全部结束") != true) return;
            bool success = await _session.KillAllProcessesAsync(p.File, signal);
            ShowMessage?.Invoke(success ? "批量结束信号已发送。" : "操作可能部分失败或权限不足。", "全部结束");
        }

        [RelayCommand]
        private void CopyPid()
        {
            if (SelectedProcess == null) return;
            CopyToClipboard(SelectedProcess.Pid.ToString());
        }

        // ── 网络连接 ─────────────────────────────────────────────

        public async Task LoadNetConnsAsync()
        {
            try
            {
                _allNetConnsRaw = await _session.GetNetConnsAsync();
                ApplyNetFilter();
            }
            catch (Exception ex) { ShowError?.Invoke("读取网络连接失败", ex.Message); }
        }

        partial void OnNetSearchChanged(string value) => ApplyNetFilter();
        partial void OnFilterEmptyNetChanged(bool value) => ApplyNetFilter();

        private void ApplyNetFilter()
        {
            string search = NetSearch.Trim().ToLower();
            bool hideEmpty = FilterEmptyNet;
            RunOnUiThread(() =>
            {
                NetConns.Clear();
                foreach (var c in _allNetConnsRaw)
                {
                    if (hideEmpty && string.IsNullOrEmpty(c.Program)) continue;
                    if (!string.IsNullOrEmpty(search) &&
                        !c.Local.ToLower().Contains(search) &&
                        !c.Remote.ToLower().Contains(search) &&
                        !c.Program.ToLower().Contains(search) &&
                        !c.Pid.ToString().Contains(search) &&
                        !c.User.ToLower().Contains(search) &&
                        !c.Proto.ToLower().Contains(search)) continue;
                    NetConns.Add(c);
                }
            });
        }

        [RelayCommand]
        private async Task KillNetAsync(int signal)
        {
            var c = SelectedNetConn;
            if (c == null || c.Pid <= 0) return;
            if (Confirm?.Invoke($"确定要结束进程 {c.Pid} ({c.Program}) 吗？\n信号: {signal}", "结束进程") != true) return;
            bool success = await _session.KillProcessAsync(c.Pid, signal);
            ShowMessage?.Invoke(success ? "信号已发送。" : "操作失败，可能权限不足。", "结束进程");
            await LoadNetConnsAsync();
        }

        // ── Cron ─────────────────────────────────────────────────

        public async Task LoadCronJobsAsync()
        {
            try
            {
                var jobsTask = _session.GetCronJobsAsync();
                var statusTask = _session.GetCronStatusAsync();
                _allCronJobsRaw = await jobsTask;
                ApplyCronFilter();
                CronServiceStatus = await statusTask;
                CronStatusRunning = CronServiceStatus == "运行中";
            }
            catch (Exception ex) { ShowError?.Invoke("读取计划任务失败", ex.Message); }
        }

        partial void OnCronSearchChanged(string value) => ApplyCronFilter();
        partial void OnFilterDisabledCronChanged(bool value) => ApplyCronFilter();

        private void ApplyCronFilter()
        {
            string search = CronSearch.Trim().ToLower();
            bool hideDisabled = FilterDisabledCron;
            RunOnUiThread(() =>
            {
                CronJobs.Clear();
                foreach (var c in _allCronJobsRaw)
                {
                    if (hideDisabled && !c.Enabled) continue;
                    if (!string.IsNullOrEmpty(search) &&
                        !c.Schedule.ToLower().Contains(search) &&
                        !c.Command.ToLower().Contains(search) &&
                        !c.Raw.ToLower().Contains(search)) continue;
                    CronJobs.Add(c);
                }
            });
        }

        public void ApplyCronPreset(string tag)
        {
            if (tag == "custom") return;
            var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5)
            {
                CronMin = parts[0];
                CronHour = parts[1];
                CronDom = parts[2];
                CronMonth = parts[3];
                CronDow = parts[4];
            }
        }

        [RelayCommand]
        private async Task ToggleCronAsync(bool enabled)
        {
            if (SelectedCronJob == null) return;
            string action = enabled ? "启用" : "禁用";
            if (Confirm?.Invoke($"确定要{action}该计划任务吗？\n\n{SelectedCronJob.Schedule}\n{SelectedCronJob.Command}", $"{action}任务") != true) return;
            bool ok = await _session.ToggleCronJobAsync(SelectedCronJob.LineIndex, enabled);
            ShowMessage?.Invoke(ok ? $"计划任务已{action}。" : "操作失败，可能权限不足。", $"{action}任务");
            await LoadCronJobsAsync();
        }

        [RelayCommand]
        private async Task DeleteCronAsync()
        {
            if (SelectedCronJob == null) return;
            if (Confirm?.Invoke($"确定要删除该计划任务吗？\n\n{SelectedCronJob.Schedule}\n{SelectedCronJob.Command}", "删除任务") != true) return;
            bool ok = await _session.RemoveCronJobAsync(SelectedCronJob.LineIndex);
            ShowMessage?.Invoke(ok ? "计划任务已删除。" : "删除失败，可能权限不足。", "删除任务");
            await LoadCronJobsAsync();
        }

        [RelayCommand]
        private async Task AddCronAsync()
        {
            string cmd = CronCommand.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                ShowMessage?.Invoke("命令不能为空。", "输入错误");
                return;
            }
            string schedule = $"{CronMin.Trim()} {CronHour.Trim()} {CronDom.Trim()} {CronMonth.Trim()} {CronDow.Trim()}";
            string raw = $"{schedule} {cmd}";
            bool ok = await _session.AddCronJobAsync(raw);
            if (ok)
            {
                ShowMessage?.Invoke("计划任务已添加。", "成功");
                CronCommand = string.Empty;
                await LoadCronJobsAsync();
            }
            else
            {
                ShowMessage?.Invoke("添加失败，请检查 crontab 格式或权限。", "错误");
            }
        }

        public Action<string>? CopyToClipboardAction { get; set; }
        private void CopyToClipboard(string text) => CopyToClipboardAction?.Invoke(text);
    }
}
