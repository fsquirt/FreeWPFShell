using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Share;
using FreeWPFShell.UserForm;
using SshTunnelInfo = FreeWPFShell.Models.SshTunnelInfo;
using Renci.SshNet;
using Timer = System.Timers.Timer;

namespace FreeWPFShell.Services
{
    public class SshSessionService : IDisposable, INotifyPropertyChanged
    {
        private static int _sessionCounter = 0;

        // 全局复用 Ping 实例（线程安全）
        private static readonly System.Net.NetworkInformation.Ping s_sharedPing = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string SessionId { get; }
        public int SessionIndex { get; }
        public SshConnectionInfo HostInfo { get; }
        public string DisplayName { get; }

        public SshClient? MasterClient { get; private set; }
        public SftpClient? SftpClient { get; private set; }
        public SshTerminalConnection? TerminalConnection { get; private set; }

        public bool IsConnected { get; private set; }

        private string _connectionStatus = "准备连接...";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { if (_connectionStatus != value) { _connectionStatus = value; OnPropertyChanged(); } }
        }

        private bool _isAppCursorMode;
        public bool IsAppCursorMode
        {
            get => _isAppCursorMode;
            set { if (_isAppCursorMode != value) { _isAppCursorMode = value; OnPropertyChanged(); } }
        }

        public uint LinuxMonitorLocalPort { get; private set; } = 0;
        private readonly List<SshTunnelInfo> _associatedTunnels = new();

        public MonitorData Monitor { get; } = new();

        public event EventHandler<MonitorData>? MonitorUpdated;

        private CancellationTokenSource? _monitorCts;
        private Timer? _monitorTimer;
        private ulong _lastCpuTotal, _lastCpuIdle;
        private ulong _lastRx, _lastTx;
        private DateTime _lastNetTime = DateTime.MinValue;
        private int _tickCount;

        private readonly SettingsRepository _settingsRepo;
        private readonly Dictionary<string, FileSystemWatcher> _activeWatchers = new();
        private readonly object _sftpLock = new();
        public object SftpLock => _sftpLock;

        // 复用的 HttpClient（整个 Session 生命周期一个实例）
        private System.Net.Http.HttpClient? _monitorHttpClient;

        // 复用的对象池
        private static readonly Regex s_doubleRegex = new(@"[\d\.]+", RegexOptions.Compiled);

        public SshSessionService(SshConnectionInfo hostInfo, SettingsRepository? settingsRepo = null)
        {
            HostInfo = hostInfo;
            _settingsRepo = settingsRepo ?? new SettingsRepository();
            SessionId = Guid.NewGuid().ToString("N");
            SessionIndex = Interlocked.Increment(ref _sessionCounter) - 1;
            string baseName = string.IsNullOrEmpty(hostInfo.HostName) ? hostInfo.IpAddress : hostInfo.HostName;
            DisplayName = $"{baseName} #{SessionIndex}";
        }

        public async Task EditRemoteFileAsync(string remotePath, string editorCommand)
        {
            if (SftpClient == null || !SftpClient.IsConnected)
            {
                ModernMessageBox.Show("SFTP 未连接，无法编辑文件", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            try
            {
                string fileName = Path.GetFileName(remotePath);
                string pathHash = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(remotePath))).Replace("-", "").Substring(0, 8);
                string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", SessionId, pathHash);
                if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                string localPath = Path.Combine(localDir, fileName);

                StopFileWatcher(localPath);

                using (var fs = File.Create(localPath))
                {
                    await Task.Run(() => {
                        lock (_sftpLock)
                        {
                            SftpClient.DownloadFile(remotePath, fs);
                        }
                    });
                }

                StartFileWatcher(localPath, remotePath);

                try
                {
                    var psi = new ProcessStartInfo(editorCommand, $"\"{localPath}\"")
                    {
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show($"无法启动编辑器 '{editorCommand}':\n{ex.Message}\n\n请检查该程序是否已安装并已添加到系统环境变量 PATH 中。", "启动失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"下载文件失败: {ex.Message}", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void StopFileWatcher(string localPath)
        {
            lock (_activeWatchers)
            {
                if (_activeWatchers.TryGetValue(localPath, out var watcher))
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    _activeWatchers.Remove(localPath);
                }
            }
        }

        private void StartFileWatcher(string localPath, string remotePath)
        {
            string localDir = Path.GetDirectoryName(localPath)!;
            string fileName = Path.GetFileName(localPath);

            StopFileWatcher(localPath);

            var watcher = new FileSystemWatcher(localDir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            DateTime lastUploadTime = DateTime.MinValue;

            FileSystemEventHandler handler = (s, e) =>
            {
                if ((DateTime.Now - lastUploadTime).TotalMilliseconds < 500) return;
                lastUploadTime = DateTime.Now;

                Task.Run(async () =>
                {
                    try
                    {
                        int retry = 10;
                        while (retry-- > 0)
                        {
                            try
                            {
                                using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    lock (_sftpLock)
                                    {
                                        if (SftpClient != null && SftpClient.IsConnected)
                                        {
                                            SftpClient.UploadFile(fs, remotePath, true);
                                        }
                                    }
                                }
                                break;
                            }
                            catch { await Task.Delay(300); }
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"[Editor] Upload Error: {ex.Message}"); }
                });
            };

            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += (s, e) => handler(s, e);

            lock (_activeWatchers)
            {
                _activeWatchers[localPath] = watcher;
            }
        }


        public async Task ConnectAsync()
        {
            PrivateKeyFile? preloadedKey = null;
            if (HostInfo.AuthMethod == SshAuthMethod.PrivateKey)
            {
                if (string.IsNullOrEmpty(HostInfo.SshKeyId))
                    throw new Exception("未配置 SSH 密钥，请在连接设置中选择一个已导入的密钥。");

                var keyRepo = new Repositories.KeyRepository();
                preloadedKey = await keyRepo.LoadPrivateKeyFileAsync(HostInfo.SshKeyId);
            }

            try
            {
                await Task.Run(() =>
                    {
                        ConnectionStatus = "SSH.NET 建立连接...";
                        MasterClient = BuildSshClient(preloadedKey);
                        MasterClient.Connect();

                        ConnectionStatus = "SFTP 建立连接...";
                        SftpClient = BuildSftpClient(preloadedKey);
                        SftpClient.Connect();

                        DeployLinuxMonitor();

                        if (_settingsRepo.Load().InjectChineseLocale)
                        {
                            ConnectionStatus = "设置中文环境变量...";
                            using var cmd = MasterClient.CreateCommand("export LANG=zh_CN.UTF-8; export LC_ALL=zh_CN.UTF-8");
                            cmd.Execute();
                        }
                    }
                );
                IsConnected = true;

                TerminalConnection = new SshTerminalConnection(MasterClient, 120, 30);

                TerminalConnection.AppCursorModeChanged += (isApp) =>
                {
                    IsAppCursorMode = isApp;
                };

                _monitorHttpClient = CreateMonitorHttpClient();
                _monitorHttpClient.Timeout = TimeSpan.FromSeconds(10); // 固定超时，避免多线程竞态
                _monitorCts = new CancellationTokenSource();
                _monitorTimer = new Timer(2000) { AutoReset = true, Enabled = true };
                _monitorTimer.Elapsed += OnMonitorTick;

                ConnectionStatus = "已连接";

            }
            catch (Exception ex)
            {
                ConnectionStatus = "连接失败: " + ex.Message;
            }
        }

        private ConnectionInfo BuildConnectionInfo(PrivateKeyFile? preloadedKey = null)
        {
            var authMethods = new List<AuthenticationMethod>(2);

            if (HostInfo.AuthMethod == SshAuthMethod.Password)
            {
                authMethods.Add(new PasswordAuthenticationMethod(
                    HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? ""));
            }
            else
            {
                if (preloadedKey == null)
                    throw new Exception("密钥未预加载，请确保在连接前已加载密钥。");
                authMethods.Add(new PrivateKeyAuthenticationMethod(HostInfo.SshUser, preloadedKey));
            }

            if (HostInfo.UseProxy && HostInfo.Proxy != null)
            {
                ProxyTypes proxyType = HostInfo.Proxy.Type switch
                {
                    ProxyType.Http => ProxyTypes.Http,
                    ProxyType.Socks4 => ProxyTypes.Socks4,
                    ProxyType.Socks5 => ProxyTypes.Socks5,
                    _ => ProxyTypes.None
                };
                return new ConnectionInfo(
                    HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser,
                    proxyType, HostInfo.Proxy.ServerAddress, HostInfo.Proxy.Port,
                    HostInfo.Proxy.Username, HostInfo.Proxy.Password,
                    authMethods.ToArray());
            }

            var conn = new ConnectionInfo(
                HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser,
                authMethods.ToArray());
            conn.Encoding = Encoding.UTF8;
            return conn;
        }

        private SshClient BuildSshClient(PrivateKeyFile? preloadedKey = null)
        {
            return new SshClient(BuildConnectionInfo(preloadedKey));
        }

        private SftpClient BuildSftpClient(PrivateKeyFile? preloadedKey = null)
        {
            return new SftpClient(BuildConnectionInfo(preloadedKey));
        }

        // 复用 StringBuilder 避免频繁分配（仅在监控线程使用，无需加锁）
        private readonly StringBuilder _cmdBuilder = new StringBuilder(512);

        private async void OnMonitorTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!IsConnected || MasterClient == null || !MasterClient.IsConnected) return;
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

                // 复用 StringBuilder 构建命令
                _cmdBuilder.Clear();
                _cmdBuilder.Append("echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 11; echo \"==NET==\"; cat /proc/net/dev");
                if (_tickCount % 60 == 1)
                    _cmdBuilder.Append("; echo \"==DISK==\"; df -h --output=target,avail,size");
                var cmd = MasterClient.CreateCommand(_cmdBuilder.ToString());
                var result = await Task.Run(() => cmd.Execute());
                ParseTopOutput(result);
            }
            catch { }
        }

        private async Task PingCheckAsync()
        {
            try
            {
                var reply = await s_sharedPing.SendPingAsync(HostInfo.IpAddress, 2000);
                Monitor.Ping = reply.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? $"{reply.RoundtripTime}ms" : "超时";
            }
            catch { Monitor.Ping = "错误"; }
        }

        private void ParseLinuxMonitorJson(string json)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var stats = JsonSerializer.Deserialize<SysStats>(json, options);
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

        private void ParseTopOutput(string output)
        {
            var sections = output.Split(new[] { "==STAT==", "==TOP==", "==PROC==", "==NET==", "==DISK==" }, StringSplitOptions.None);
            if (sections.Length == 0) return;
            try
            {
                if (sections.Length > 1)
                {
                    var statLines = sections[1].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (statLines.Length > 0 && statLines[0].StartsWith("cpu "))
                    {
                        var parts = statLines[0].Substring(4).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
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
                    var lines = sections[2].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        var topStr = lines[0];
                        var upMatch = Regex.Match(topStr, @"up\s+(.*?),?\s+\d+\s+user");
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
                    var procLines = sections[3].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var procs = new List<ProcessItem>(procLines.Length - 1);
                    for (int i = 1; i < procLines.Length; i++)
                    {
                        var p = procLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3)
                        {
                            string cmd = string.Join(" ", p.Skip(2));
                            if (cmd.Length > 30) cmd = string.Concat(cmd.AsSpan(0, 30), "...");
                            procs.Add(new ProcessItem { Mem = p[0] + "%", Cpu = p[1] + "%", Cmd = cmd });
                        }
                    }
                    if (procs.Count > 0) Monitor.UpdateProcesses(procs);
                }
                if (sections.Length > 4)
                {
                    var netLines = sections[4].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var nl in netLines)
                    {
                        if (nl.Contains(":") && !nl.Contains("lo:"))
                        {
                            var p = nl.Trim().Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
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
                    var diskLines = sections[5].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var disks = new List<DiskItem>(diskLines.Length - 1);
                    for (int i = 1; i < diskLines.Length; i++)
                    {
                        var p = diskLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3) disks.Add(new DiskItem { Path = p[0], Avail = p[1], Size = p[2] });
                    }
                    if (disks.Count > 0) Monitor.UpdateDisks(disks);
                }
                MonitorUpdated?.Invoke(this, Monitor);
            }
            catch { }
        }

        private double _memMultiplier = 1.0, _swapMultiplier = 1.0;
        private void ParseMemLine(string? line, ref double multiplier, out double total, out double used)
        {
            total = 0; used = 0;
            if (line == null) return;
            if (line.StartsWith("MiB")) multiplier = 1024.0;
            else if (line.StartsWith("GiB")) multiplier = 1024.0 * 1024.0;
            var parts = line.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Contains("total")) total = ExtractDouble(part);
                if (part.Contains("used")) used = ExtractDouble(part);
            }
            total *= multiplier; used *= multiplier;
        }

        private string _monitorToken = "";
        private void DeployLinuxMonitor()
        {
            var settings = _settingsRepo.Load();
            if (!settings.UseLinuxMonitor) return;
            if (MasterClient == null || SftpClient == null) return;
            try
            {
                ConnectionStatus = "建立 ssh 隧道...";
                LinuxMonitorLocalPort = (uint)(System.Security.Cryptography.RandomNumberGenerator.GetInt32(40000, 60001));
                var port = new ForwardedPortLocal("127.0.0.1", LinuxMonitorLocalPort, "127.0.0.1", LinuxMonitorLocalPort);
                MasterClient.AddForwardedPort(port);
                port.Start();

                var tunnelInfo = new SshTunnelInfo
                {
                    Id = $"Mon_{SessionId}", HostId = HostInfo.Id,
                    HostName = HostInfo.HostName ?? HostInfo.IpAddress,
                    Type = "本地(监控)", BindAddress = "127.0.0.1",
                    BindPort = LinuxMonitorLocalPort, DestAddress = "127.0.0.1",
                    DestPort = LinuxMonitorLocalPort, Remark = "自动创建 - Linux Monitor探针",
                    PortConfig = port
                };

                RegisterTunnel(tunnelInfo);

                string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linux-monitor", "linux-monitor");
                if (File.Exists(binPath))
                {
                    ConnectionStatus = "上传 Linux_Monitor...";
                    string remotePath = $"/tmp/linux-monitor_{LinuxMonitorLocalPort}";
                    string tokenPath = $"/tmp/.mon_token_{LinuxMonitorLocalPort}";
                    _monitorToken = Guid.NewGuid().ToString("N");

                    MasterClient.CreateCommand($"pkill -9 -f {remotePath}").Execute();
                    lock (_sftpLock)
                    {
                        using (var fs = File.OpenRead(binPath)) SftpClient.UploadFile(fs, remotePath, true);

                        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(_monitorToken)))
                            SftpClient.UploadFile(ms, tokenPath, true);
                    }

                    MasterClient.CreateCommand($"chmod 600 {tokenPath}").Execute();
                    MasterClient.CreateCommand($"chmod +x {remotePath}").Execute();
                    MasterClient.CreateCommand($"nohup {remotePath} {LinuxMonitorLocalPort} {tokenPath} >/dev/null 2>&1 &").Execute();
                }
            }
            catch (Exception ex) { Debug.WriteLine("Monitor Deploy Failed: " + ex.Message); }
        }

        public void RegisterTunnel(SshTunnelInfo tunnel)
        {
            lock (_associatedTunnels) { _associatedTunnels.Add(tunnel); }
            SshTunnelManager.Instance.RegisterTunnel(tunnel);
        }

        private System.Net.Http.HttpClient CreateMonitorHttpClient()
        {
            var hc = new System.Net.Http.HttpClient();
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
                return JsonSerializer.Deserialize<ProcessDetail>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
                return JsonSerializer.Deserialize<List<ProcessItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ProcessItem>();
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
                return JsonSerializer.Deserialize<List<LoginRecord>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<LoginRecord>();
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
                return JsonSerializer.Deserialize<List<ServiceItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ServiceItem>();
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
                return JsonSerializer.Deserialize<List<NetConnItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<NetConnItem>();
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
                return JsonSerializer.Deserialize<List<CronJobItem>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CronJobItem>();
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

        public void Disconnect()
        {
            _monitorCts?.Cancel();
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            _monitorTimer = null;

            _monitorHttpClient?.Dispose();
            _monitorHttpClient = null;

            lock (_activeWatchers)
            {
                foreach (var w in _activeWatchers.Values) w.Dispose();
                _activeWatchers.Clear();
            }

            try
            {
                string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", SessionId);
                if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
            }
            catch { }

            IsConnected = false;

            Task.Run(() =>
            {
                try
                {
                    lock (_associatedTunnels)
                    {
                        foreach (var tunnel in _associatedTunnels)
                        {
                            try
                            {
                                if (tunnel.PortConfig != null && tunnel.PortConfig.IsStarted) tunnel.PortConfig.Stop();
                                SshTunnelManager.Instance.UnregisterTunnel(tunnel.Id);
                            }
                            catch { }
                        }
                        _associatedTunnels.Clear();
                    }

                    if (LinuxMonitorLocalPort > 0 && MasterClient != null && MasterClient.IsConnected)
                    {
                        try { MasterClient.CreateCommand($"pkill -9 -f linux-monitor_{LinuxMonitorLocalPort}")?.Execute(); } catch { }
                    }

                    lock (_sftpLock)
                    {
                        SftpClient?.Disconnect();
                        SftpClient?.Dispose();
                        SftpClient = null;
                    }
                    MasterClient?.Disconnect(); MasterClient?.Dispose();
                }
                catch { }
            });

            TerminalConnection?.Close();
            TerminalConnection = null;
        }

        public void Dispose() => Disconnect();

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
