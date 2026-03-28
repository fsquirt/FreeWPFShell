using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Timers;
using Renci.SshNet;
using Timer = System.Timers.Timer;

namespace FreeWPFShell.Share
{
    public class ProcessItem
    {
        public string Mem { get; set; } = string.Empty;
        public string Cpu { get; set; } = string.Empty;
        public string Cmd { get; set; } = string.Empty;
    }

    public class DiskItem
    {
        public string Path { get; set; } = string.Empty;
        public string Avail { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
    }

    public class SysStats
    {
        public float cpu_pct { get; set; }
        public ulong mem_used { get; set; }
        public ulong mem_total { get; set; }
        public ulong swap_used { get; set; }
        public ulong swap_total { get; set; }
        public string uptime { get; set; } = string.Empty;
        public string load { get; set; } = string.Empty;
        public ulong rx_speed { get; set; }
        public ulong tx_speed { get; set; }
        public string iface { get; set; } = string.Empty;
        public List<ProcessItem> processes { get; set; } = new();
        public List<DiskItem> disks { get; set; } = new();
    }

    public class MonitorData : INotifyPropertyChanged
    {
        private double _cpuPct;
        private double _memPct;
        private string _memText = "0M/0M";
        private double _swapPct;
        private string _swapText = "0M/0M";
        private string _uptime = "运行 -- 天...";
        private string _load = "负载 --, --, --";
        private string _ping = "--ms";
        private string _netUp = "0K/s";
        private string _netDown = "0K/s";
        private string _netIface = "--";
        private string _netMax = "100K";
        private string _netMid = "50K";
        private double _netRxSpeed;
        private double _netTxSpeed;
        private List<ValueTuple<double, double>> _netHistory = new();
        private List<ProcessItem> _processes = new();
        private List<DiskItem> _disks = new();

        public double CpuPct { get => _cpuPct; set { _cpuPct = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuText)); } }
        public string CpuText => $"{_cpuPct:F1}%";
        public double MemPct { get => _memPct; set { _memPct = value; OnPropertyChanged(); } }
        public string MemText { get => _memText; set { _memText = value; OnPropertyChanged(); } }
        public double SwapPct { get => _swapPct; set { _swapPct = value; OnPropertyChanged(); } }
        public string SwapText { get => _swapText; set { _swapText = value; OnPropertyChanged(); } }
        public string Uptime { get => _uptime; set { _uptime = value; OnPropertyChanged(); } }
        public string Load { get => _load; set { _load = value; OnPropertyChanged(); } }
        public string Ping { get => _ping; set { _ping = value; OnPropertyChanged(); } }
        public string NetUp { get => _netUp; set { _netUp = value; OnPropertyChanged(); } }
        public string NetDown { get => _netDown; set { _netDown = value; OnPropertyChanged(); } }
        public string NetIface { get => _netIface; set { _netIface = value; OnPropertyChanged(); } }
        public string NetMax { get => _netMax; set { _netMax = value; OnPropertyChanged(); } }
        public string NetMid { get => _netMid; set { _netMid = value; OnPropertyChanged(); } }
        public double NetRxSpeed { get => _netRxSpeed; set { _netRxSpeed = value; OnPropertyChanged(); } }
        public double NetTxSpeed { get => _netTxSpeed; set { _netTxSpeed = value; OnPropertyChanged(); } }
        public List<(double rx, double tx)> NetHistory { get => _netHistory; set { _netHistory = value; OnPropertyChanged(); } }
        public List<ProcessItem> Processes { get => _processes; set { _processes = value; OnPropertyChanged(); } }
        public List<DiskItem> Disks { get => _disks; set { _disks = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SshSessionInstance : IDisposable
    {
        private static int _sessionCounter = 0;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public int SessionIndex { get; }
        public SshManager.SshConnectionInfo HostInfo { get; }
        public string DisplayName { get; }

        // Network Clients
        public SshClient? MasterClient { get; private set; }
        public SftpClient? SftpClient { get; private set; }
        public ConPtyConnection? TerminalConnection { get; private set; }

        // State
        public bool IsConnected { get; private set; }
        public uint LinuxMonitorLocalPort { get; private set; } = 0;

        // Monitor data exposed for UI binding
        public MonitorData Monitor { get; } = new MonitorData();

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Timer? _monitorTimer;
        private ulong _lastCpuTotal, _lastCpuIdle;
        private ulong _lastRx, _lastTx;
        private DateTime _lastNetTime = DateTime.MinValue;
        private int _tickCount;

        public event EventHandler<MonitorData>? MonitorUpdated;

        public SshSessionInstance(SshManager.SshConnectionInfo hostInfo)
        {
            HostInfo = hostInfo;
            SessionIndex = Interlocked.Increment(ref _sessionCounter) - 1;
            string baseName = string.IsNullOrEmpty(hostInfo.HostName) ? hostInfo.IpAddress : hostInfo.HostName;
            DisplayName = $"{baseName} #{SessionIndex}";
        }

        public async Task ConnectAsync()
        {
            await Task.Run(() =>
            {
                // 1. Establish Master Client
                MasterClient = new SshClient(HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? "");
                MasterClient.Connect();

                if (_cts.IsCancellationRequested) return;

                // 2. Establish SFTP
                SftpClient = new SftpClient(HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? "");
                SftpClient.Connect();

                // 3. Deploy Linux Monitor
                DeployLinuxMonitor();

                IsConnected = true;
            }, _cts.Token);

            if (_cts.IsCancellationRequested) return;

            // 4. Mount Terminal Control PTY
            TerminalConnection = new ConPtyConnection($"ssh {HostInfo.SshUser}@{HostInfo.IpAddress} -p {HostInfo.SshPort}", 120, 40);

            // Auto login hook
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

            // 5. Start background monitor polling (runs regardless of active tab)
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

                // Legacy command-based monitoring
                string cmdStr = "echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 11; echo \"==NET==\"; cat /proc/net/dev";
                if (_tickCount % 60 == 1)
                    cmdStr += "; echo \"==DISK==\"; df -h --output=target,avail,size";

                var cmd = MasterClient.CreateCommand(cmdStr);
                var result = await Task.Run(() => cmd.Execute());
                ParseTopOutput(result);
            }
            catch { }
        }

        private void ParseLinuxMonitorJson(string json)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var stats = JsonSerializer.Deserialize<SysStats>(json, options);
            if (stats == null) return;

            Monitor.CpuPct = stats.cpu_pct;

            if (stats.mem_total > 0)
            {
                double memPct = (stats.mem_used * 100.0) / stats.mem_total;
                Monitor.MemPct = memPct;
                Monitor.MemText = $"{stats.mem_used / 1024.0 / 1024.0 / 1024.0:F1}G / {stats.mem_total / 1024.0 / 1024.0 / 1024.0:F1}G";
            }

            if (stats.swap_total > 0)
            {
                double swapPct = (stats.swap_used * 100.0) / stats.swap_total;
                Monitor.SwapPct = swapPct;
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
                // Parse CPU from /proc/stat
                if (sections.Length > 1)
                {
                    var statLines = sections[1].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (statLines.Length > 0 && statLines[0].StartsWith("cpu "))
                    {
                        var parts = statLines[0].Substring(4).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            ulong total = 0;
                            foreach (var p in parts)
                                if (ulong.TryParse(p, out ulong v)) total += v;
                            ulong.TryParse(parts[3], out ulong idle);
                            if (parts.Length > 4 && ulong.TryParse(parts[4], out ulong iowait)) idle += iowait;

                            if (_lastCpuTotal > 0 && total > _lastCpuTotal)
                            {
                                ulong deltaTotal = total - _lastCpuTotal;
                                ulong deltaIdle = idle - _lastCpuIdle;
                                double usage = 100.0 * (1.0 - (double)deltaIdle / deltaTotal);
                                if (usage < 0) usage = 0;
                                if (usage > 100) usage = 100;
                                Monitor.CpuPct = usage;
                            }
                            _lastCpuTotal = total;
                            _lastCpuIdle = idle;
                        }
                    }
                }

                // Parse TOP
                if (sections.Length > 2)
                {
                    var lines = sections[2].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        string topStr = lines[0];
                        var upMatch = Regex.Match(topStr, @"up\s+(.*?),?\s+\d+\s+user");
                        if (upMatch.Success) Monitor.Uptime = "运行 " + upMatch.Groups[1].Value.Trim();

                        int loadIdx = topStr.IndexOf("average:");
                        if (loadIdx > 0) Monitor.Load = "负载 " + topStr.Substring(loadIdx + 8).Trim();

                        var memLine = lines.FirstOrDefault(l => l.Contains(" Mem"));
                        if (memLine != null)
                        {
                            double multiplier = 1.0;
                            if (memLine.StartsWith("MiB")) multiplier = 1024.0;
                            else if (memLine.StartsWith("GiB")) multiplier = 1024.0 * 1024.0;

                            var parts = memLine.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            double total = 0, used = 0;
                            foreach (var part in parts)
                            {
                                if (part.Contains("total")) total = ExtractDouble(part);
                                if (part.Contains("used")) used = ExtractDouble(part);
                            }
                            total *= multiplier;
                            used *= multiplier;
                            if (total > 0)
                            {
                                Monitor.MemPct = used / total * 100.0;
                                Monitor.MemText = $"{FormatMemSize(used)}/{FormatMemSize(total)}";
                            }
                        }

                        var swapLine = lines.FirstOrDefault(l => l.Contains(" Swap"));
                        if (swapLine != null)
                        {
                            double multiplier = 1.0;
                            if (swapLine.StartsWith("MiB")) multiplier = 1024.0;
                            else if (swapLine.StartsWith("GiB")) multiplier = 1024.0 * 1024.0;

                            var parts = swapLine.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            double total = 0, used = 0;
                            foreach (var part in parts)
                            {
                                if (part.Contains("total")) total = ExtractDouble(part);
                                if (part.Contains("used")) used = ExtractDouble(part);
                            }
                            total *= multiplier;
                            used *= multiplier;
                            if (total > 0)
                            {
                                Monitor.SwapPct = used / total * 100.0;
                                Monitor.SwapText = $"{FormatMemSize(used)}/{FormatMemSize(total)}";
                            }
                        }
                    }
                }

                // Parse Processes
                if (sections.Length > 3)
                {
                    var procs = new List<ProcessItem>();
                    var procLines = sections[3].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < procLines.Length; i++)
                    {
                        var parts = procLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            string cmd = string.Join(" ", parts.Skip(2));
                            if (cmd.Length > 30) cmd = cmd.Substring(0, 30) + "...";
                            procs.Add(new ProcessItem { Mem = parts[0] + "%", Cpu = parts[1] + "%", Cmd = cmd });
                        }
                    }
                    if (procs.Count > 0) Monitor.Processes = procs;
                }

                // Parse Net
                if (sections.Length > 4)
                {
                    var netLines = sections[4].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var nl in netLines)
                    {
                        if (nl.Contains(":") && !nl.Contains("lo:"))
                        {
                            var parts = nl.Trim().Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 10)
                            {
                                if (ulong.TryParse(parts[1], out ulong rx) && ulong.TryParse(parts[9], out ulong tx))
                                {
                                    Monitor.NetIface = parts[0];
                                    var now = DateTime.Now;
                                    if (_lastNetTime != DateTime.MinValue)
                                    {
                                        double secs = (now - _lastNetTime).TotalSeconds;
                                        if (secs > 0)
                                        {
                                            double rxSpeed = 0, txSpeed = 0;
                                            if (rx >= _lastRx && _lastRx > 0) rxSpeed = (rx - _lastRx) / secs;
                                            if (tx >= _lastTx && _lastTx > 0) txSpeed = (tx - _lastTx) / secs;

                                            if (_lastRx > 0 && _lastTx > 0)
                                            {
                                                Monitor.NetRxSpeed = rxSpeed;
                                                Monitor.NetTxSpeed = txSpeed;
                                                Monitor.NetDown = FormatNetSpeed(rxSpeed) + "/s";
                                                Monitor.NetUp = FormatNetSpeed(txSpeed) + "/s";

                                                var history = new List<(double, double)>(Monitor.NetHistory) { (rxSpeed, txSpeed) };
                                                if (history.Count > 50) history.RemoveAt(0);
                                                double maxVal = history.Max(x => Math.Max(x.Item1, x.Item2));
                                                if (maxVal < 1024) maxVal = 1024;
                                                Monitor.NetMax = FormatNetSpeed(maxVal);
                                                Monitor.NetMid = FormatNetSpeed(maxVal / 2);
                                                Monitor.NetHistory = history;
                                            }
                                        }
                                    }
                                    _lastRx = rx;
                                    _lastTx = tx;
                                    _lastNetTime = now;
                                }
                            }
                            break;
                        }
                    }
                }

                // Parse Disk
                if (sections.Length > 5)
                {
                    var disks = new List<DiskItem>();
                    var diskLines = sections[5].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < diskLines.Length; i++)
                    {
                        var parts = diskLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                            disks.Add(new DiskItem { Path = parts[0], Avail = parts[1], Size = parts[2] });
                    }
                    if (disks.Count > 0) Monitor.Disks = disks;
                }

                MonitorUpdated?.Invoke(this, Monitor);
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
                    ? $"{reply.RoundtripTime}ms"
                    : "超时";
            }
            catch { Monitor.Ping = "错误"; }
        }

        private void DeployLinuxMonitor()
        {
            var sm = new SshManager.SshConnectionManager();
            if (!sm.Settings.UseLinuxMonitor) return;
            if (MasterClient == null || SftpClient == null) return;

            try
            {
                LinuxMonitorLocalPort = (uint)(new Random().Next(40000, 60000));

                var tunnelInfo = new SshTunnelInfo
                {
                    Id = $"Mon_{SessionId}",
                    HostId = HostInfo.Id,
                    HostName = HostInfo.HostName ?? HostInfo.IpAddress,
                    Type = "Local",
                    BindAddress = "127.0.0.1",
                    BindPort = LinuxMonitorLocalPort,
                    DestAddress = "127.0.0.1",
                    DestPort = LinuxMonitorLocalPort,
                    Remark = "自动创建 - Linux Monitor"
                };

                var port = new Renci.SshNet.ForwardedPortLocal("127.0.0.1", LinuxMonitorLocalPort, "127.0.0.1", LinuxMonitorLocalPort);
                MasterClient.AddForwardedPort(port);
                port.Start();
                tunnelInfo.PortConfig = port;

                SshTunnelManager.Instance.RegisterTunnel(tunnelInfo);

                string binPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linux-monitor");

                if (System.IO.File.Exists(binPath))
                {
                    string uniqueRemotePath = $"/tmp/linux-monitor_{LinuxMonitorLocalPort}";

                    var cmdKill = MasterClient.CreateCommand($"pkill -9 -f {uniqueRemotePath}");
                    cmdKill.Execute();

                    using (var fs = System.IO.File.OpenRead(binPath))
                    {
                        SftpClient.UploadFile(fs, uniqueRemotePath, true);
                    }

                    var cmd1 = MasterClient.CreateCommand($"chmod +x {uniqueRemotePath}");
                    cmd1.Execute();

                    var cmd3 = MasterClient.CreateCommand($"nohup {uniqueRemotePath} {LinuxMonitorLocalPort} >/dev/null 2>&1 &");
                    cmd3.Execute();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Monitor Deploy Failed: " + ex.Message);
            }
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
                try
                {
                    var cmdKill = MasterClient?.CreateCommand($"pkill -9 -f linux-monitor_{LinuxMonitorLocalPort}");
                    cmdKill?.Execute();
                } catch {}
            }

            SftpClient?.Disconnect();
            SftpClient?.Dispose();
            MasterClient?.Disconnect();
            MasterClient?.Dispose();
            TerminalConnection?.Close();
        }

        public void Dispose()
        {
            Disconnect();
        }

        private static string FormatNetSpeed(double bytesPerSec)
        {
            if (bytesPerSec > 1024 * 1024) return (bytesPerSec / 1024 / 1024).ToString("0.0") + "M";
            if (bytesPerSec > 1024) return (bytesPerSec / 1024).ToString("0") + "K";
            return bytesPerSec.ToString("0") + "B";
        }

        private static double ExtractDouble(string s)
        {
            var match = Regex.Match(s, @"[\d\.]+");
            if (match.Success && double.TryParse(match.Value, out double val)) return val;
            return 0;
        }

        private static string FormatMemSize(double kb)
        {
            if (kb > 1024 * 1024) return (kb / 1024 / 1024).ToString("0.0") + "G";
            return (kb / 1024).ToString("0") + "M";
        }
    }
}
