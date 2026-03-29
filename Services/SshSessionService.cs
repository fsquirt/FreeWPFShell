using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Timers;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Share;
using SshTunnelInfo = FreeWPFShell.Models.SshTunnelInfo;
using Renci.SshNet;
using Timer = System.Timers.Timer;

namespace FreeWPFShell.Services
{
    public class SshSessionService : IDisposable
    {
        private static int _sessionCounter = 0;

        public string SessionId { get; }
        public int SessionIndex { get; }
        public SshConnectionInfo HostInfo { get; }
        public string DisplayName { get; }

        public SshClient? MasterClient { get; private set; }
        public SftpClient? SftpClient { get; private set; }
        public ConPtyConnection? TerminalConnection { get; private set; }

        public bool IsConnected { get; private set; }
        public uint LinuxMonitorLocalPort { get; private set; } = 0;

        public ViewModels.MonitorData Monitor { get; } = new();

        public event EventHandler<ViewModels.MonitorData>? MonitorUpdated;

        private CancellationTokenSource _cts = new();
        private Timer? _monitorTimer;
        private ulong _lastCpuTotal, _lastCpuIdle;
        private ulong _lastRx, _lastTx;
        private DateTime _lastNetTime = DateTime.MinValue;
        private int _tickCount;
        private readonly SettingsRepository _settingsRepo;

        public SshSessionService(SshConnectionInfo hostInfo, SettingsRepository? settingsRepo = null)
        {
            HostInfo = hostInfo;
            _settingsRepo = settingsRepo ?? new SettingsRepository();
            SessionId = Guid.NewGuid().ToString("N");
            SessionIndex = Interlocked.Increment(ref _sessionCounter) - 1;
            string baseName = string.IsNullOrEmpty(hostInfo.HostName) ? hostInfo.IpAddress : hostInfo.HostName;
            DisplayName = $"{baseName} #{SessionIndex}";
        }

        public async Task ConnectAsync()
        {
            await Task.Run(() =>
            {
                // SSH 客户端连接
                MasterClient = new SshClient(HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? "");
                MasterClient.Connect();
                if (_cts.IsCancellationRequested) return;

                // SFTP 客户端连接
                SftpClient = new SftpClient(HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? "");
                SftpClient.Connect();

                // 部署监控
                try { DeployLinuxMonitor(); } catch { /* 监控部署失败不应中断会话 */ }

                IsConnected = true;
            }, _cts.Token);

            if (_cts.IsCancellationRequested) return;

            TerminalConnection = new ConPtyConnection($"ssh {HostInfo.SshUser}@{HostInfo.IpAddress} -p {HostInfo.SshPort}", 120, 40);

            string outputBuffer = "";
            EventHandler<Microsoft.Terminal.Wpf.TerminalOutputEventArgs>? onOutput = null;
            onOutput = (s, args) =>
            {
                if (args.Data != null)
                {
                    outputBuffer += args.Data;
                    if (outputBuffer.ToLower().Contains("password:"))
                    {
                        TerminalConnection.TerminalOutput -= onOutput;
                        Task.Delay(50).ContinueWith(_ => TerminalConnection.WriteInput($"{HostInfo.DecryptedSshSecret}\n"));
                    }
                    if (outputBuffer.Length > 2048) outputBuffer = "";
                }
            };
            TerminalConnection.TerminalOutput += onOutput;

            _monitorTimer = new Timer(2000) { AutoReset = true, Enabled = true };
            _monitorTimer.Elapsed += OnMonitorTick;
        }

        private async void OnMonitorTick(object? sender, ElapsedEventArgs e)
        {
            if (!IsConnected || MasterClient == null || !MasterClient.IsConnected) return;
            _tickCount++;
            PingCheckAsync();
            try
            {
                if (LinuxMonitorLocalPort > 0)
                {
                    using var hc = new System.Net.Http.HttpClient();
                    hc.Timeout = TimeSpan.FromSeconds(1);
                    string json = await hc.GetStringAsync($"http://127.0.0.1:{LinuxMonitorLocalPort}/stats");
                    ParseLinuxMonitorJson(json);
                    return;
                }
                string cmdStr = "echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 11; echo \"==NET==\"; cat /proc/net/dev";
                if (_tickCount % 60 == 1)
                    cmdStr += "; echo \"==DISK==\"; df -h --output=target,avail,size";
                var cmd = MasterClient.CreateCommand(cmdStr);
                var result = await Task.Run(() => cmd.Execute());
                ParseTopOutput(result);
            }
            catch { }
        }

        private async void PingCheckAsync()
        {
            try
            {
                using var p = new System.Net.NetworkInformation.Ping();
                var reply = await p.SendPingAsync(HostInfo.IpAddress, 2000);
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

            var history = new List<(double, double)>(Monitor.NetHistory) { (stats.rx_speed, stats.tx_speed) };
            if (history.Count > 50) history.RemoveAt(0);
            double maxVal = history.Max(x => Math.Max(x.Item1, x.Item2));
            if (maxVal < 1024) maxVal = 1024;
            Monitor.NetMax = FormatNetSpeed(maxVal);
            Monitor.NetMid = FormatNetSpeed(maxVal / 2);
            Monitor.NetHistory = history;

            if (stats.processes != null && stats.processes.Count > 0)
                Monitor.Processes = stats.processes.ToList();
            if (stats.disks != null && stats.disks.Count > 0)
                Monitor.Disks = stats.disks.ToList();

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
                    var procs = new List<ProcessItem>();
                    var procLines = sections[3].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < procLines.Length; i++)
                    {
                        var p = procLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3)
                        {
                            string cmd = string.Join(" ", p.Skip(2));
                            if (cmd.Length > 30) cmd = cmd.Substring(0, 30) + "...";
                            procs.Add(new ProcessItem { Mem = p[0] + "%", Cpu = p[1] + "%", Cmd = cmd });
                        }
                    }
                    if (procs.Count > 0) Monitor.Processes = procs;
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
                                            var history = new List<(double, double)>(Monitor.NetHistory) { (rxSpeed, txSpeed) };
                                            if (history.Count > 50) history.RemoveAt(0);
                                            double maxVal = history.Max(x => Math.Max(x.Item1, x.Item2));
                                            if (maxVal < 1024) maxVal = 1024;
                                            Monitor.NetMax = FormatNetSpeed(maxVal); Monitor.NetMid = FormatNetSpeed(maxVal / 2); Monitor.NetHistory = history;
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
                    var disks = new List<DiskItem>();
                    var diskLines = sections[5].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < diskLines.Length; i++)
                    {
                        var p = diskLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3) disks.Add(new DiskItem { Path = p[0], Avail = p[1], Size = p[2] });
                    }
                    if (disks.Count > 0) Monitor.Disks = disks;
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

        private void DeployLinuxMonitor()
        {
            var settings = _settingsRepo.Load();
            if (!settings.UseLinuxMonitor) return;
            if (MasterClient == null || SftpClient == null) return;
            try
            {
                LinuxMonitorLocalPort = (uint)(new Random().Next(40000, 60000));
                var port = new ForwardedPortLocal("127.0.0.1", LinuxMonitorLocalPort, "127.0.0.1", LinuxMonitorLocalPort);
                MasterClient.AddForwardedPort(port);
                port.Start();

                SshTunnelManager.Instance.RegisterTunnel(new SshTunnelInfo
                {
                    Id = $"Mon_{SessionId}", HostId = HostInfo.Id,
                    HostName = HostInfo.HostName ?? HostInfo.IpAddress,
                    Type = "Local", BindAddress = "127.0.0.1",
                    BindPort = LinuxMonitorLocalPort, DestAddress = "127.0.0.1",
                    DestPort = LinuxMonitorLocalPort, Remark = "自动创建 - Linux Monitor",
                    PortConfig = port
                });

                string binPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linux-monitor");
                if (System.IO.File.Exists(binPath))
                {
                    string remotePath = $"/tmp/linux-monitor_{LinuxMonitorLocalPort}";
                    MasterClient.CreateCommand($"pkill -9 -f {remotePath}").Execute();
                    using (var fs = System.IO.File.OpenRead(binPath)) SftpClient.UploadFile(fs, remotePath, true);
                    MasterClient.CreateCommand($"chmod +x {remotePath}").Execute();
                    MasterClient.CreateCommand($"nohup {remotePath} {LinuxMonitorLocalPort} >/dev/null 2>&1 &").Execute();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Monitor Deploy Failed: " + ex.Message); }
        }

        public void Disconnect()
        {
            _cts.Cancel();
            _monitorTimer?.Stop();
            _monitorTimer?.Dispose();
            _monitorTimer = null;
            if (LinuxMonitorLocalPort > 0)
            {
                SshTunnelManager.Instance.UnregisterTunnel($"Mon_{SessionId}");
                try { MasterClient?.CreateCommand($"pkill -9 -f linux-monitor_{LinuxMonitorLocalPort}")?.Execute(); } catch { }
            }
            SftpClient?.Disconnect(); SftpClient?.Dispose();
            MasterClient?.Disconnect(); MasterClient?.Dispose();
            TerminalConnection?.Close();
        }

        public void Dispose() => Disconnect();

        private static string FormatNetSpeed(double bps)
        {
            if (bps > 1024 * 1024) return (bps / 1024 / 1024).ToString("0.0") + "M";
            if (bps > 1024) return (bps / 1024).ToString("0") + "K";
            return bps.ToString("0") + "B";
        }
        private static double ExtractDouble(string s) { var m = Regex.Match(s, @"[\d\.]+"); return m.Success && double.TryParse(m.Value, out double v) ? v : 0; }
        private static string FormatMemSize(double kb) { return kb > 1024 * 1024 ? (kb / 1024 / 1024).ToString("0.0") + "G" : (kb / 1024).ToString("0") + "M"; }
    }
}
