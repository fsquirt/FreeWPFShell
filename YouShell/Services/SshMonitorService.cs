using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YouShell.Models;
using YouShell.Models.Dto;
using YouShell.Repositories;
using YouShell.Share;
using Renci.SshNet;
using Timer = System.Timers.Timer;

namespace YouShell.Services
{
    public class SshMonitorService : IDisposable
    {
        private readonly SshClient _sshClient;
        private readonly SftpClient _sftpClient;
        private readonly SshConnectionInfo _hostInfo;
        private readonly string _sessionId;
        private readonly SettingsRepository _settingsRepo;
        private readonly object _sftpLock;

        public MonitorData Monitor { get; }
        public event EventHandler<MonitorData>? MonitorUpdated;

        public uint LinuxMonitorLocalPort { get; private set; } = 0;
        private string _monitorToken = "";
        private HttpClient? _monitorHttpClient;
        private CancellationTokenSource? _monitorCts;
        private Timer? _monitorTimer;
        private int _tickCount;

        private ulong _lastCpuTotal, _lastCpuIdle;
        private ulong _lastRx, _lastTx;
        private DateTime _lastNetTime = DateTime.MinValue;
        private double _memMultiplier = 1.0, _swapMultiplier = 1.0;

        private readonly System.Net.NetworkInformation.Ping _ping = new();
        private static readonly Regex s_doubleRegex = new(@"[\d\.]+", RegexOptions.Compiled);
        private static readonly Regex s_uptimeRegex = new(@"up\s+(.*?),?\s+\d+\s+user", RegexOptions.Compiled);
        private readonly StringBuilder _cmdBuilder = new StringBuilder(512);

        // 静态复用的分割字符数组，避免每次 Split 分配新数组
        private static readonly char[] s_newlineChars = { '\n', '\r' };
        private static readonly char[] s_spaceChars = { ' ' };
        private static readonly char[] s_semicolonComma = { ':', ',' };
        private static readonly string[] s_topSections = { "==STAT==", "==TOP==", "==PROC==", "==NET==", "==DISK==" };
        private static readonly char[] s_colonSpace = { ':', ' ' };
        private static readonly char[] s_spaceTab = { ' ', '\t' };

        // 复用的 Json 选项，避免每次反序列化创建
        private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

        // 复用列表，避免每 tick 分配新 List
        private readonly List<ProcessItem> _reusableProcs = new(16);
        private readonly List<DiskItem> _reusableDisks = new(8);

        public Action<string>? ConnectionStatusCallback { get; set; }
        public Action<SshTunnelInfo>? RegisterTunnelCallback { get; set; }
        // 当监控轮询检测到 SSH 连接断开时触发，用于清理会话相关资源（如隧道）
        public Action? ConnectionLostCallback { get; set; }

        private void NotifyStatus(string status)
        {
            ConnectionStatusCallback?.Invoke(status);
        }

        public SshMonitorService(SshClient sshClient, SftpClient sftpClient, SshConnectionInfo hostInfo, string sessionId, SettingsRepository settingsRepo, object sftpLock, MonitorData monitor)
        {
            _sshClient = sshClient;
            _sftpClient = sftpClient;
            _hostInfo = hostInfo;
            _sessionId = sessionId;
            _settingsRepo = settingsRepo;
            _sftpLock = sftpLock;
            Monitor = monitor;
        }

        /// <summary>
        /// 仅供单元测试使用：构造一个不依赖真实 SSH 客户端的实例，
        /// 用于直接测试 top/proc 文本解析逻辑。
        /// </summary>
        internal SshMonitorService(MonitorData monitor)
        {
            _sshClient = null!;
            _sftpClient = null!;
            _hostInfo = new SshConnectionInfo();
            _sessionId = "test";
            _settingsRepo = new SettingsRepository();
            _sftpLock = new object();
            Monitor = monitor;
        }

        public async Task StartAsync()
        {
            try
            {
                await DeployLinuxMonitorAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Deploy Linux Monitor Failed: " + ex.Message);
            }

            try
            {
                _monitorHttpClient = CreateMonitorHttpClient();
                _monitorHttpClient.Timeout = TimeSpan.FromSeconds(10);
                _monitorCts = new CancellationTokenSource();
                _monitorTimer = new Timer(2000) { AutoReset = true, Enabled = true };
                _monitorTimer.Elapsed += OnMonitorTick;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Monitor Init Failed: " + ex.Message);
            }
        }

        private async void OnMonitorTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_sshClient.IsConnected)
            {
                // 连接已断开：通知上层清理资源（隧道等），并停止轮询避免空转
                ConnectionLostCallback?.Invoke();
                _monitorTimer?.Stop();
                return;
            }
            _tickCount++;
            await PingCheckAsync();
            try
            {
                if (LinuxMonitorLocalPort > 0)
                {
                    var hc = _monitorHttpClient;
                    if (hc == null) return;
                    string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/stats");
                    ParseLinuxMonitorJson(json);
                    return;
                }

                _cmdBuilder.Clear();
                _cmdBuilder.Append("echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 11; echo \"==NET==\"; cat /proc/net/dev");
                if (_tickCount % 60 == 1)
                    _cmdBuilder.Append("; echo \"==DISK==\"; df -h --output=target,avail,size");
                var cmd = _sshClient.CreateCommand(_cmdBuilder.ToString());
                var result = await Task.Run(() => cmd.Execute());
                ParseTopOutput(result);
            }
            catch { }
        }

        private async Task PingCheckAsync()
        {
            try
            {
                var reply = await _ping.SendPingAsync(_hostInfo.IpAddress, 2000);
                Monitor.Ping = reply.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? $"{reply.RoundtripTime}ms" : "超时";
            }
            catch { Monitor.Ping = "错误"; }
        }

        internal void ParseLinuxMonitorJson(string json)
        {
            var stats = JsonSerializer.Deserialize<SysStats>(json, s_jsonOptions);
            if (stats == null) return;

            Monitor.CpuPct = stats.cpu_pct;
            if (stats.mem_total > 0)
            {
                Monitor.MemPct = (stats.mem_used * 100.0) / stats.mem_total;
                Monitor.MemText = $"{stats.mem_used / 1024.0 / 1024.0 / 1024.0:F1}G / {stats.mem_total / 1024.0 / 1024.0 / 1024.0:F1}G";
            }
            if (stats.swap_total > 0)
            {
                Monitor.SwapPct = (stats.swap_used * 100.0) / stats.swap_total;
                Monitor.SwapText = $"{stats.swap_used / 1024.0 / 1024.0 / 1024.0:F1}G / {stats.swap_total / 1024.0 / 1024.0 / 1024.0:F1}G";
            }
            Monitor.Uptime = $"运行 {stats.uptime}";
            Monitor.Load = $"负载 {stats.load}";
            Monitor.NetRxSpeed = stats.rx_speed;
            Monitor.NetTxSpeed = stats.tx_speed;
            Monitor.NetUp = FormatNetSpeed(stats.tx_speed) + "/s";
            Monitor.NetDown = FormatNetSpeed(stats.rx_speed) + "/s";
            Monitor.NetIface = stats.iface;

            Monitor.AddNetHistoryEntry(stats.rx_speed, stats.tx_speed);
            double maxVal = Monitor.GetNetHistoryMax();
            Monitor.NetMax = FormatNetSpeed(maxVal);
            Monitor.NetMid = FormatNetSpeed(maxVal / 2);

            if (stats.processes != null && stats.processes.Count > 0)
                Monitor.UpdateProcesses(stats.processes);
            if (stats.disks != null && stats.disks.Count > 0)
                Monitor.UpdateDisks(stats.disks);

            MonitorUpdated?.Invoke(this, Monitor);
        }

        internal void ParseTopOutput(string output)
        {
            var sections = output.Split(s_topSections, StringSplitOptions.None);
            if (sections.Length == 0) return;
            try
            {
                if (sections.Length > 1)
                {
                    var statLines = sections[1].Split(s_newlineChars, StringSplitOptions.RemoveEmptyEntries);
                    if (statLines.Length > 0 && statLines[0].StartsWith("cpu "))
                    {
                        var parts = statLines[0].AsSpan(4).Trim().ToString().Split(s_spaceChars, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            ulong total = 0;
                            foreach (var p in parts) if (ulong.TryParse(p, out ulong v)) total += v;
                            ulong.TryParse(parts[3], out ulong idle);
                            if (parts.Length > 4 && ulong.TryParse(parts[4], out ulong iowait)) idle += iowait;
                            if (_lastCpuTotal > 0 && total > _lastCpuTotal)
                            {
                                double usage = 100.0 * (1.0 - (double)(idle - _lastCpuIdle) / (total - _lastCpuTotal));
                                Monitor.CpuPct = Math.Clamp(usage, 0, 100);
                            }
                            _lastCpuTotal = total;
                            _lastCpuIdle = idle;
                        }
                    }
                }
                if (sections.Length > 2)
                {
                    var lines = sections[2].Split(s_newlineChars, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        var topStr = lines[0];
                        var upMatch = s_uptimeRegex.Match(topStr);
                        if (upMatch.Success) Monitor.Uptime = "运行 " + upMatch.Groups[1].Value.Trim();
                        int loadIdx = topStr.IndexOf("average:");
                        if (loadIdx > 0) Monitor.Load = "负载 " + topStr.Substring(loadIdx + 8).Trim();

                        ParseMemLine(lines.FirstOrDefault(l => l.Contains(" Mem")), ref _memMultiplier, out double memTotal, out double memUsed);
                        if (memTotal > 0) { Monitor.MemPct = memUsed / memTotal * 100.0; Monitor.MemText = $"{FormatMemSize(memUsed)}/{FormatMemSize(memTotal)}"; }
                        ParseMemLine(lines.FirstOrDefault(l => l.Contains(" Swap")), ref _swapMultiplier, out double swapTotal, out double swapUsed);
                        if (swapTotal > 0) { Monitor.SwapPct = swapUsed / swapTotal * 100.0; Monitor.SwapText = $"{FormatMemSize(swapUsed)}/{FormatMemSize(swapTotal)}"; }
                    }
                }
                if (sections.Length > 3)
                {
                    var procLines = sections[3].Split(s_newlineChars, StringSplitOptions.RemoveEmptyEntries);
                    _reusableProcs.Clear();
                    for (int i = 1; i < procLines.Length; i++)
                    {
                        var p = procLines[i].Trim().Split(s_spaceChars, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3)
                        {
                            string cmd = string.Join(" ", p.Skip(2));
                            if (cmd.Length > 30) cmd = string.Concat(cmd.AsSpan(0, 30), "...");
                            _reusableProcs.Add(new ProcessItem { Mem = p[0] + "%", Cpu = p[1] + "%", Cmd = cmd });
                        }
                    }
                    if (_reusableProcs.Count > 0) Monitor.UpdateProcesses(_reusableProcs);
                }
                if (sections.Length > 4)
                {
                    var netLines = sections[4].Split(s_newlineChars, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var nl in netLines)
                    {
                        if (nl.Contains(':') && !nl.Contains("lo:"))
                        {
                            var p = nl.Trim().Split(s_colonSpace, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length >= 10 && ulong.TryParse(p[1], out ulong rx) && ulong.TryParse(p[9], out ulong tx))
                            {
                                Monitor.NetIface = p[0];
                                var now = DateTime.Now;
                                if (_lastNetTime != DateTime.MinValue)
                                {
                                    double secs = (now - _lastNetTime).TotalSeconds;
                                    if (secs > 0)
                                    {
                                        double rxSpeed = (_lastRx > 0 && rx >= _lastRx) ? (rx - _lastRx) / secs : 0;
                                        double txSpeed = (_lastTx > 0 && tx >= _lastTx) ? (tx - _lastTx) / secs : 0;
                                        if (_lastRx > 0)
                                        {
                                            Monitor.NetRxSpeed = rxSpeed; Monitor.NetTxSpeed = txSpeed;
                                            Monitor.NetDown = FormatNetSpeed(rxSpeed) + "/s"; Monitor.NetUp = FormatNetSpeed(txSpeed) + "/s";
                                            Monitor.AddNetHistoryEntry(rxSpeed, txSpeed);
                                            double maxVal = Monitor.GetNetHistoryMax();
                                            Monitor.NetMax = FormatNetSpeed(maxVal); Monitor.NetMid = FormatNetSpeed(maxVal / 2);
                                        }
                                    }
                                }
                                _lastRx = rx; _lastTx = tx; _lastNetTime = now;
                            }
                            break;
                        }
                    }
                }
                if (sections.Length > 5)
                {
                    var diskLines = sections[5].Split(s_newlineChars, StringSplitOptions.RemoveEmptyEntries);
                    _reusableDisks.Clear();
                    for (int i = 1; i < diskLines.Length; i++)
                    {
                        var p = diskLines[i].Trim().Split(s_spaceChars, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3) _reusableDisks.Add(new DiskItem { Path = p[0], Avail = p[1], Size = p[2] });
                    }
                    if (_reusableDisks.Count > 0) Monitor.UpdateDisks(_reusableDisks);
                }
                MonitorUpdated?.Invoke(this, Monitor);
            }
            catch { }
        }

        private void ParseMemLine(string? line, ref double multiplier, out double total, out double used)
        {
            total = 0; used = 0;
            if (line == null) return;
            if (line.StartsWith("MiB")) multiplier = 1024.0;
            else if (line.StartsWith("GiB")) multiplier = 1024.0 * 1024.0;
            var parts = line.Split(s_semicolonComma, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Contains("total")) total = ExtractDouble(part);
                if (part.Contains("used")) used = ExtractDouble(part);
            }
            total *= multiplier; used *= multiplier;
        }

        private async Task DeployLinuxMonitorAsync()
        {
            var settings = _settingsRepo.Load();
            if (!settings.UseLinuxMonitor) return;
            if (!_sshClient.IsConnected || !_sftpClient.IsConnected) return;

            NotifyStatus("建立 ssh 隧道...");
            LinuxMonitorLocalPort = (uint)(System.Security.Cryptography.RandomNumberGenerator.GetInt32(40000, 60001));
            // 通过跳板机连接时，remoteHost 用 "0.0.0.0" 确保目标 SSH 服务器在所有接口监听
            // 直连时 "127.0.0.1" 和 "0.0.0.0" 效果相同（SSH.NET 内部会转换）
            string monitorRemoteHost = _hostInfo.UseProxy && _hostInfo.Proxy?.Type == ProxyType.Ssh ? "0.0.0.0" : "127.0.0.1";
            var port = new ForwardedPortLocal("127.0.0.1", LinuxMonitorLocalPort, monitorRemoteHost, LinuxMonitorLocalPort);
            port.Exception += (sender, e) =>
            {
                try { Debug.WriteLine($"[ForwardedPort Exception] {e.Exception?.Message}"); } catch { }
            };
            _sshClient.AddForwardedPort(port);

            // 异步启动端口转发，绝不阻塞 UI
            await Task.Run(() => port.Start());

            var tunnelInfo = new SshTunnelInfo
            {
                Id = $"Mon_{_sessionId}", HostId = _hostInfo.Id,
                HostName = _hostInfo.HostName ?? _hostInfo.IpAddress,
                Type = "本地(监控)", BindAddress = "127.0.0.1",
                BindPort = LinuxMonitorLocalPort, DestAddress = "127.0.0.1",
                DestPort = LinuxMonitorLocalPort, Remark = "自动创建 - Linux Monitor探针",
                PortConfig = port
            };

            RegisterTunnelCallback?.Invoke(tunnelInfo);

            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linux-monitor", "linux-monitor");
            if (!File.Exists(binPath)) return;

            // 异步计算 MD5，避免阻塞
            string localHash = "";
            await Task.Run(() =>
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(binPath))
                {
                    var hashBytes = md5.ComputeHash(stream);
                    localHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            });

            // 异步执行远程命令
            await Task.Run(() => _sshClient.CreateCommand("mkdir -p /tmp/YouShell").Execute());

            bool needsUpload = true;
            try
            {
                string hashResult = "";
                await Task.Run(() =>
                {
                    using (var hashCmd = _sshClient.CreateCommand("md5sum /tmp/YouShell/linux-monitor"))
                        hashResult = hashCmd.Execute()?.Trim() ?? "";
                });
                if (!string.IsNullOrEmpty(hashResult))
                {
                    var parts = hashResult.Split(s_spaceTab, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && parts[0].ToLowerInvariant() == localHash)
                        needsUpload = false;
                }
            }
            catch { needsUpload = true; }

            if (needsUpload)
            {
                NotifyStatus("上传 Linux_Monitor...");
                // 在后台读文件到内存，减小锁内操作
                byte[] fileBytes = await Task.Run(() => File.ReadAllBytes(binPath));
                await Task.Run(() =>
                {
                    lock (_sftpLock)
                    {
                        using (var ms = new MemoryStream(fileBytes))
                            _sftpClient.UploadFile(ms, "/tmp/YouShell/linux-monitor", true);
                    }
                });
            }

            string tokenPath = $"/tmp/YouShell/.mon_token_{LinuxMonitorLocalPort}";
            _monitorToken = Guid.NewGuid().ToString("N");

            byte[] tokenBytes = Encoding.UTF8.GetBytes(_monitorToken);
            await Task.Run(() =>
            {
                lock (_sftpLock)
                {
                    using (var ms = new MemoryStream(tokenBytes))
                        _sftpClient.UploadFile(ms, tokenPath, true);
                }
            });

            // 异步执行远程命令链
            await Task.Run(() =>
            {
                _sshClient.CreateCommand($"chmod 600 {tokenPath}").Execute();
                _sshClient.CreateCommand("chmod +x /tmp/YouShell/linux-monitor").Execute();
                _sshClient.CreateCommand($"pkill -9 -f \"linux-monitor {LinuxMonitorLocalPort}\"").Execute();
                _sshClient.CreateCommand($"nohup /tmp/YouShell/linux-monitor {LinuxMonitorLocalPort} {tokenPath} >/dev/null 2>&1 &").Execute();
            });
        }

        private HttpClient CreateMonitorHttpClient()
        {
            var hc = new HttpClient();
            if (!string.IsNullOrEmpty(_monitorToken))
                hc.DefaultRequestHeaders.Add("X-Monitor-Token", _monitorToken);
            return hc;
        }

        public async Task<ProcessDetail?> GetProcessDetailAsync(uint pid)
        {
            if (LinuxMonitorLocalPort == 0) return null;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return null;
                string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/process_detail?pid={pid}");
                return JsonSerializer.Deserialize<ProcessDetail>(json, s_jsonOptions);
            }
            catch { return null; }
        }

        public async Task<bool> KillProcessAsync(uint pid, int signal)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return false;
                string result = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/kill?pid={pid}&sig={signal}");
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<List<ProcessItem>> GetAllProcessesAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<ProcessItem>();
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return new List<ProcessItem>();
                string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/all_processes");
                return JsonSerializer.Deserialize<List<ProcessItem>>(json, s_jsonOptions) ?? new List<ProcessItem>();
            }
            catch { return new List<ProcessItem>(); }
        }

        public async Task<List<LoginRecord>> GetLoginRecordsAsync(string endpoint)
        {
            if (LinuxMonitorLocalPort == 0) return new List<LoginRecord>();
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return new List<LoginRecord>();
                string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}{endpoint}");
                return JsonSerializer.Deserialize<List<LoginRecord>>(json, s_jsonOptions) ?? new List<LoginRecord>();
            }
            catch { return new List<LoginRecord>(); }
        }

        public async Task<List<ServiceItem>> GetServicesAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<ServiceItem>();
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return new List<ServiceItem>();
                string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/services");
                return JsonSerializer.Deserialize<List<ServiceItem>>(json, s_jsonOptions) ?? new List<ServiceItem>();
            }
            catch { return new List<ServiceItem>(); }
        }

        public async Task<bool> ServiceActionAsync(string serviceName, string action)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return false;
                string encodedName = System.Net.WebUtility.UrlEncode(serviceName);
                string result = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/service_{action}?name={encodedName}");
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<string> GetServiceLogAsync(string serviceName)
        {
            if (LinuxMonitorLocalPort == 0) return "";
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return "";
                string encodedName = System.Net.WebUtility.UrlEncode(serviceName);
                return await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/service_log?name={encodedName}");
            }
            catch { return ""; }
        }

        public async Task<bool> KillAllProcessesAsync(string fullPath, int signal)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return false;
                string encodedPath = System.Net.WebUtility.UrlEncode(fullPath);
                string result = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/killall?path={encodedPath}&sig={signal}");
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<List<NetConnItem>> GetNetConnsAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<NetConnItem>();
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return new List<NetConnItem>();
                string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/net_conns");
                return JsonSerializer.Deserialize<List<NetConnItem>>(json, s_jsonOptions) ?? new List<NetConnItem>();
            }
            catch { return new List<NetConnItem>(); }
        }

        public async Task<List<CronJobItem>> GetCronJobsAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<CronJobItem>();
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return new List<CronJobItem>();
                string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/cron_list");
                return JsonSerializer.Deserialize<List<CronJobItem>>(json, s_jsonOptions) ?? new List<CronJobItem>();
            }
            catch { return new List<CronJobItem>(); }
        }

        public async Task<bool> AddCronJobAsync(string rawLine)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return false;
                string encoded = System.Net.WebUtility.UrlEncode(rawLine);
                string result = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/cron_add?raw={encoded}");
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<bool> RemoveCronJobAsync(int lineIndex)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return false;
                string result = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/cron_remove?line={lineIndex}");
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<bool> ToggleCronJobAsync(int lineIndex, bool enabled)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return false;
                string result = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/cron_toggle?line={lineIndex}&enabled={enabled.ToString().ToLowerInvariant()}");
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<string> GetCronStatusAsync()
        {
            if (LinuxMonitorLocalPort == 0) return "未连接";
            try
            {
                var hc = _monitorHttpClient;
                if (hc == null) return "未知";
                return await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/cron_status");
            }
            catch { return "未知"; }
        }

        public void Stop()
        {
            _monitorCts?.Cancel();
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            _monitorTimer = null;

            _monitorHttpClient?.Dispose();
            try { _ping.Dispose(); } catch { }
            _monitorHttpClient = null;

            if (LinuxMonitorLocalPort > 0 && _sshClient != null && _sshClient.IsConnected)
            {
                try { _sshClient.CreateCommand($"pkill -9 -f \"linux-monitor {LinuxMonitorLocalPort}\"")?.Execute(); } catch { }
                try { _sshClient.CreateCommand($"rm -f /tmp/YouShell/.mon_token_{LinuxMonitorLocalPort}")?.Execute(); } catch { }
            }
        }

        public void Dispose() => Stop();

        private static string FormatNetSpeed(double bps)
        {
            if (bps > 1024 * 1024) return $"{(bps / 1024 / 1024):0.0}M";
            if (bps > 1024) return $"{(bps / 1024):0}K";
            return $"{bps:0}B";
        }
        private static double ExtractDouble(string s) { var m = s_doubleRegex.Match(s); return m.Success && double.TryParse(m.Value, out double v) ? v : 0; }
        private static string FormatMemSize(double kb) { return kb > 1024 * 1024 ? (kb / 1024 / 1024).ToString("0.0") + "G" : (kb / 1024).ToString("0") + "M"; }
    }
}
