using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeWPFShell.Models;
using FreeWPFShell.Models.Dto;
using FreeWPFShell.Repositories;
using FreeWPFShell.Share;
using Renci.SshNet;
using Timer = System.Timers.Timer;

namespace FreeWPFShell.Services
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
        private ForwardedPortLocal? _monitorPort;
        private string _monitorToken = "";
        private CancellationTokenSource? _monitorCts;
        private Timer? _monitorTimer;
        private int _tickCount;

        private int _probeFailStreak;
        private int _probeDialogPending;
        private const int ProbeFailThreshold = 3;

        private ulong _lastCpuTotal, _lastCpuIdle;
        private ulong _lastRx, _lastTx;
        private DateTime _lastNetTime = DateTime.MinValue;
        private double _memMultiplier = 1.0, _swapMultiplier = 1.0;

        private readonly System.Net.NetworkInformation.Ping _ping = new();
        private static readonly Regex s_doubleRegex = new(@"[\d\.]+", RegexOptions.Compiled);
        private static readonly Regex s_uptimeRegex = new(@"up\s+(.*?),?\s+\d+\s+user", RegexOptions.Compiled);
        private readonly StringBuilder _cmdBuilder = new StringBuilder(512);


        private static readonly char[] s_newlineChars = { '\n', '\r' };
        private static readonly char[] s_spaceChars = { ' ' };
        private static readonly char[] s_semicolonComma = { ':', ',' };
        private static readonly string[] s_topSections = { "==STAT==", "==TOP==", "==PROC==", "==NET==", "==DISK==" };
        private static readonly char[] s_colonSpace = { ':', ' ' };
        private static readonly char[] s_spaceTab = { ' ', '\t' };


        private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };


        private readonly List<ProcessItem> _reusableProcs = new(16);
        private readonly List<DiskItem> _reusableDisks = new(8);

        public Action<string>? ConnectionStatusCallback { get; set; }
        public Action<SshTunnelInfo>? RegisterTunnelCallback { get; set; }
        public Action<string>? UnregisterTunnelCallback { get; set; }

        public Action? ConnectionLostCallback { get; set; }

        public Action<string>? DistroDetectedCallback { get; set; }
        private string _lastDistro = "";

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
                DebugConsoleService.Log("[Monitor] 开始部署探针");
                await DeployLinuxMonitorAsync();
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Monitor] 部署探针失败: " + ex.Message);
            }

            try
            {
                _monitorCts = new CancellationTokenSource();
                _monitorTimer = new Timer(2000) { AutoReset = true, Enabled = true };
                _monitorTimer.Elapsed += OnMonitorTick;
                DebugConsoleService.Log("[Monitor] 采样定时器已启动 2000ms");
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Monitor] 启动采样定时器失败: " + ex.Message);
            }
        }


        private static IReadOnlyDictionary<string, object?> Args(params (string key, object? value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (key, value) in pairs) d[key] = value;
            return d;
        }


        private Task<string> SendAsync(string op, IReadOnlyDictionary<string, object?>? args = null, int timeoutMs = MonitorProtocol.DefaultTimeoutMs)
            => MonitorProtocol.SendRequestAsync("127.0.0.1", (int)LinuxMonitorLocalPort, _monitorToken, op, args, timeoutMs);


        public void SendExitCommand()
        {
            try
            {
                if (LinuxMonitorLocalPort == 0 || !_sshClient.IsConnected) return;
                SendAsync("exit", timeoutMs: 2000).GetAwaiter().GetResult();
                DebugConsoleService.Log("[Monitor] 已向探针发送退出指令");
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Monitor] 探针退出指令发送失败: " + ex.Message);
            }
        }

        private async void OnMonitorTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_sshClient.IsConnected)
            {
                ConnectionLostCallback?.Invoke();
                _monitorTimer?.Stop();
                return;
            }
            _tickCount++;
            await PingCheckAsync();

            if (LinuxMonitorLocalPort > 0)
            {
                try
                {
                    string json = await SendAsync("stats");
                    ParseLinuxMonitorJson(json);
                    _probeFailStreak = 0;
                    return;
                }
                catch (Exception ex)
                {
                    _probeFailStreak++;
                    DebugConsoleService.Log($"[Monitor] 探针采样失败({_probeFailStreak}/{ProbeFailThreshold}): {ex.Message}");
                    if (_probeFailStreak >= ProbeFailThreshold)
                    {
                        _probeFailStreak = 0;
                        await AskProbeFallbackAsync(ex.Message);
                        return;
                    }
                }
            }

            try
            {
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

        private void ReleaseProbePort()
        {
            var port = _monitorPort;
            _monitorPort = null;
            LinuxMonitorLocalPort = 0;
            _monitorToken = "";
            _probeFailStreak = 0;

            try { port?.Stop(); } catch { }
            try { if (port != null) _sshClient.RemoveForwardedPort(port); } catch { }
            try { UnregisterTunnelCallback?.Invoke($"Mon_{_sessionId}"); } catch { }
        }

        private void TearDownProbe(string reason)
        {
            DebugConsoleService.Log("[Monitor] 探针不可用，回退 Shell 解析模式: " + reason);
            ReleaseProbePort();
            NotifyStatus("探针不可用，已回退 Shell 解析模式");
        }

        private void DisableMonitor(string reason)
        {
            DebugConsoleService.Log("[Monitor] 用户选择仅使用 SSH + SFTP，停止监控采集: " + reason);
            ReleaseProbePort();
            try { _monitorCts?.Cancel(); } catch { }

            Monitor.Reset();
            MonitorUpdated?.Invoke(this, Monitor);

            NotifyStatus("监控已关闭（仅使用 SSH + SFTP）");
        }

        private async Task AskProbeFallbackAsync(string reason)
        {
            if (Interlocked.CompareExchange(ref _probeDialogPending, 1, 0) != 0) return;

            bool resumeTimer = true;
            try
            {
                _monitorTimer?.Stop();

                int choice = await Task.Run(() => UserForm.ModernMessageBox.ShowOptions(
                    $"Linux 监控探针连续 {ProbeFailThreshold} 次无响应，已无法获取实时监控数据。\n\n错误信息：{reason}\n\n请选择后续的监控方式：\n\n重新部署探针：重新上传并启动探针，保留完整的监控能力\n改用 Shell 解析：停用探针，改用 SSH 命令解析，数据粒度较低\n仅使用 SSH + SFTP：关闭监控采集，只保留终端与文件传输",
                    "监控探针无响应",
                    "重新部署探针",
                    "改用 Shell 解析",
                    "仅使用 SSH + SFTP",
                    System.Windows.MessageBoxImage.Warning));

                switch (choice)
                {
                    case 1:
                        NotifyStatus("探针无响应，正在重新部署...");
                        if (await RedeployProbeAsync()) return;
                        NotifyStatus("探针重新部署失败，已回退 Shell 解析模式");
                        TearDownProbe(reason);
                        break;

                    case 3:
                        resumeTimer = false;
                        DisableMonitor(reason);
                        break;

                    default:
                        TearDownProbe(reason);
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Monitor] 处理探针失败时出错: " + ex.Message);
                TearDownProbe(reason);
            }
            finally
            {
                Interlocked.Exchange(ref _probeDialogPending, 0);
                if (resumeTimer) _monitorTimer?.Start();
            }
        }

        private async Task<bool> RedeployProbeAsync()
        {
            try
            {
                ReleaseProbePort();

                await DeployLinuxMonitorAsync();
                if (LinuxMonitorLocalPort == 0) return false;

                string json = await SendAsync("stats", timeoutMs: 3000);
                ParseLinuxMonitorJson(json);
                NotifyStatus("探针已重新部署");
                return true;
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Monitor] 重新部署探针失败: " + ex.Message);
                return false;
            }
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

            if (!string.IsNullOrEmpty(stats.os_id) && stats.os_id != _lastDistro)
            {
                _lastDistro = stats.os_id;
                DistroDetectedCallback?.Invoke(stats.os_id);
            }

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
            if (!settings.UseLinuxMonitor)
            {
                DebugConsoleService.Log("[Monitor] 设置中未启用 Linux 监控探针，跳过部署");
                return;
            }
            if (!_sshClient.IsConnected || !_sftpClient.IsConnected)
            {
                DebugConsoleService.Log("[Monitor] SSH/SFTP 尚未就绪，跳过探针部署");
                return;
            }

            NotifyStatus("建立 ssh 隧道...");
            LinuxMonitorLocalPort = (uint)(System.Security.Cryptography.RandomNumberGenerator.GetInt32(40000, 60001));

            string monitorRemoteHost = _hostInfo.UseProxy && _hostInfo.Proxy?.Type == ProxyType.Ssh ? "0.0.0.0" : "127.0.0.1";
            var port = new ForwardedPortLocal("127.0.0.1", LinuxMonitorLocalPort, monitorRemoteHost, LinuxMonitorLocalPort);
            port.Exception += (sender, e) =>
            {
                try { DebugConsoleService.Log($"[ForwardedPort Exception] {e.Exception?.Message}"); } catch { }
            };
            _sshClient.AddForwardedPort(port);


            await Task.Run(() => port.Start());
            _monitorPort = port;
            DebugConsoleService.Log($"[Monitor] 转发已建立 127.0.0.1:{LinuxMonitorLocalPort} → {monitorRemoteHost}:{LinuxMonitorLocalPort}");

            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linux-monitor", "linux-monitor");
            if (!File.Exists(binPath))
            {
                DebugConsoleService.Log("[Monitor] 未找到本地探针二进制，回退 Shell 解析: " + binPath);
                TearDownProbe("未找到本地探针二进制");
                return;
            }

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


            await Task.Run(() => _sshClient.CreateCommand("mkdir -p /tmp/FreeWPFShell").Execute());

            bool needsUpload = true;
            try
            {
                string hashResult = "";
                await Task.Run(() =>
                {
                    using (var hashCmd = _sshClient.CreateCommand("md5sum /tmp/FreeWPFShell/linux-monitor"))
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

                byte[] fileBytes = await Task.Run(() => File.ReadAllBytes(binPath));
                await Task.Run(() =>
                {
                    lock (_sftpLock)
                    {
                        using (var ms = new MemoryStream(fileBytes))
                            _sftpClient.UploadFile(ms, "/tmp/FreeWPFShell/linux-monitor", true);
                    }
                });
            }

            string tokenPath = $"/tmp/FreeWPFShell/.mon_token_{LinuxMonitorLocalPort}";
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


            await Task.Run(() =>
            {
                _sshClient.CreateCommand($"chmod 600 {tokenPath}").Execute();
                _sshClient.CreateCommand("chmod +x /tmp/FreeWPFShell/linux-monitor").Execute();
                _sshClient.CreateCommand($"pkill -9 -f \"linux-monitor {LinuxMonitorLocalPort}\"").Execute();
                _sshClient.CreateCommand($"nohup /tmp/FreeWPFShell/linux-monitor {LinuxMonitorLocalPort} {tokenPath} >/dev/null 2>&1 &").Execute();
            });

            DebugConsoleService.Log(needsUpload
                ? $"[Monitor] 探针已上传并启动，端口 {LinuxMonitorLocalPort}"
                : $"[Monitor] 远端探针哈希一致，直接启动，端口 {LinuxMonitorLocalPort}");

            _probeFailStreak = 0;
        }

        public async Task<ProcessDetail?> GetProcessDetailAsync(uint pid)
        {
            if (LinuxMonitorLocalPort == 0) return null;
            try
            {
                string json = await SendAsync("process_detail", Args(("pid", pid)));
                return JsonSerializer.Deserialize<ProcessDetail>(json, s_jsonOptions);
            }
            catch { return null; }
        }

        public async Task<bool> KillProcessAsync(uint pid, int signal)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                string result = await SendAsync("kill", Args(("pid", pid), ("sig", signal)));
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<List<ProcessItem>> GetAllProcessesAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<ProcessItem>();
            try
            {
                string json = await SendAsync("all_processes");
                return JsonSerializer.Deserialize<List<ProcessItem>>(json, s_jsonOptions) ?? new List<ProcessItem>();
            }
            catch { return new List<ProcessItem>(); }
        }

        public async Task<List<LoginRecord>> GetLoginRecordsAsync(string kind, int count)
        {
            if (LinuxMonitorLocalPort == 0) return new List<LoginRecord>();
            try
            {
                string json = await SendAsync("login_records", Args(("kind", kind), ("count", count)));
                return JsonSerializer.Deserialize<List<LoginRecord>>(json, s_jsonOptions) ?? new List<LoginRecord>();
            }
            catch { return new List<LoginRecord>(); }
        }

        public async Task<List<ServiceItem>> GetServicesAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<ServiceItem>();
            try
            {
                string json = await SendAsync("services");
                return JsonSerializer.Deserialize<List<ServiceItem>>(json, s_jsonOptions) ?? new List<ServiceItem>();
            }
            catch { return new List<ServiceItem>(); }
        }

        public async Task<bool> ServiceActionAsync(string serviceName, string action)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                string result = await SendAsync("service_action", Args(("name", serviceName), ("action", action)));
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<string> GetServiceLogAsync(string serviceName)
        {
            if (LinuxMonitorLocalPort == 0) return "";
            try
            {
                return await SendAsync("service_log", Args(("name", serviceName)));
            }
            catch { return ""; }
        }

        public async Task<bool> KillAllProcessesAsync(string fullPath, int signal)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                string result = await SendAsync("killall", Args(("path", fullPath), ("sig", signal)));
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<List<NetConnItem>> GetNetConnsAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<NetConnItem>();
            try
            {
                string json = await SendAsync("net_conns");
                return JsonSerializer.Deserialize<List<NetConnItem>>(json, s_jsonOptions) ?? new List<NetConnItem>();
            }
            catch { return new List<NetConnItem>(); }
        }

        public async Task<List<CronJobItem>> GetCronJobsAsync()
        {
            if (LinuxMonitorLocalPort == 0) return new List<CronJobItem>();
            try
            {
                string json = await SendAsync("cron_list");
                return JsonSerializer.Deserialize<List<CronJobItem>>(json, s_jsonOptions) ?? new List<CronJobItem>();
            }
            catch { return new List<CronJobItem>(); }
        }

        public async Task<bool> AddCronJobAsync(string rawLine)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                string result = await SendAsync("cron_add", Args(("raw", rawLine)));
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<bool> RemoveCronJobAsync(int lineIndex)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                string result = await SendAsync("cron_remove", Args(("line", lineIndex)));
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<bool> ToggleCronJobAsync(int lineIndex, bool enabled)
        {
            if (LinuxMonitorLocalPort == 0) return false;
            try
            {
                string result = await SendAsync("cron_toggle", Args(("line", lineIndex), ("enabled", enabled)));
                return result.ToLower() == "true";
            }
            catch { return false; }
        }

        public async Task<string> GetCronStatusAsync()
        {
            if (LinuxMonitorLocalPort == 0) return "未连接";
            try
            {
                return await SendAsync("cron_status");
            }
            catch { return "未知"; }
        }

        public void Stop()
        {

            SendExitCommand();

            _monitorCts?.Cancel();
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            _monitorTimer = null;

            try { _ping.Dispose(); } catch { }

            var monitorPort = _monitorPort;
            _monitorPort = null;

            if (LinuxMonitorLocalPort > 0 && _sshClient != null && _sshClient.IsConnected)
            {
                try { _sshClient.CreateCommand($"pkill -9 -f \"linux-monitor {LinuxMonitorLocalPort}\"")?.Execute(); } catch { }
                try { _sshClient.CreateCommand($"rm -f /tmp/FreeWPFShell/.mon_token_{LinuxMonitorLocalPort}")?.Execute(); } catch { }
            }

            try { monitorPort?.Stop(); } catch { }
            try { if (monitorPort != null) _sshClient?.RemoveForwardedPort(monitorPort); } catch { }
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
