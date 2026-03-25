using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.Linq;
using FreeWPFShell.Share;

namespace FreeWPFShell
{
    public class ProcessItem
    {
        public string Mem { get; set; }
        public string Cpu { get; set; }
        public string Cmd { get; set; }
    }

    public class DiskItem
    {
        public string Path { get; set; }
        public string Avail { get; set; }
        public string Size { get; set; }
    }

    public partial class MainForm
    {
        private System.Windows.Threading.DispatcherTimer _timer;
        private Dictionary<string, SshClient> _activeMonitors = new Dictionary<string, SshClient>();
        private SshClient _currentMonitorClient;
        private SshManager.SshConnectionInfo _currentMonitorHost;
        
        private ObservableCollection<ProcessItem> _processes = new ObservableCollection<ProcessItem>();
        private ObservableCollection<DiskItem> _disks = new ObservableCollection<DiskItem>();
        private ulong _lastCpuTotal = 0, _lastCpuIdle = 0;
        private ulong _lastRx = 0, _lastTx = 0;
        private DateTime _lastNetTime = DateTime.MinValue;
        private List<(double rx, double tx)> _netHistory = new List<(double, double)>();
        private int _tickCount = 0;

        public MainForm()
        {
            InitializeComponent();

            // Setup WelcomeTab
            var welcomePage = new Pages.WelcomePage();
            WelcomeTab.Tag = welcomePage; // Store reference in Tag
            PagesContainer.Children.Add(welcomePage);

            ProcessGrid.ItemsSource = _processes;
            DiskGrid.ItemsSource = _disks;

            _timer = new System.Windows.Threading.DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += MonitorTick;
            _timer.Start();
        }

        public void OpenSession(SshManager.SshConnectionInfo hostInfo)
        {
            var terminalPage = new Pages.TerminalAndSFTP(hostInfo);
            
            string tabHeader = string.IsNullOrEmpty(hostInfo.HostName) 
                ? hostInfo.IpAddress 
                : hostInfo.HostName;

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = tabHeader, VerticalAlignment = VerticalAlignment.Center });
            
            var btnClose = new Button
            {
                Content = "×",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            
            var tabItem = new TabItem
            {
                Header = headerPanel,
                Tag = terminalPage
            };

            btnClose.Click += (s, e) => CloseSessionTab(tabItem, hostInfo.Id);
            headerPanel.Children.Add(btnClose);

            PagesContainer.Children.Add(terminalPage);
            SessionTabs.Items.Add(tabItem);
            SessionTabs.SelectedItem = tabItem;
        }

        private async void CloseSessionTab(TabItem tabItem, string hostId)
        {
            if (tabItem.Tag is Pages.TerminalAndSFTP terminalPage)
            {
                PagesContainer.Children.Remove(terminalPage);
                // Disconnect in background
                await System.Threading.Tasks.Task.Run(() => terminalPage.Disconnect());
            }

            // Remove background monitor connection
            if (_activeMonitors.ContainsKey(hostId))
            {
                var client = _activeMonitors[hostId];
                _activeMonitors.Remove(hostId);
                
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        client.Disconnect();
                        client.Dispose();
                    }
                    catch { }
                });
            }

            if (_currentMonitorHost?.Id == hostId)
            {
                _currentMonitorHost = null;
                _currentMonitorClient = null;
            }

            SessionTabs.Items.Remove(tabItem);
        }

        private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionTabs.SelectedItem is TabItem selectedTab && selectedTab.Tag is UIElement activeView)
            {
                // Toggle visibility in PagesContainer to completely prevent Virtualizing Unload/Load
                foreach (UIElement child in PagesContainer.Children)
                {
                    child.Visibility = (child == activeView) ? Visibility.Visible : Visibility.Collapsed;
                }

                if (activeView is Pages.TerminalAndSFTP terminalPage)
                {
                    SwitchMonitorToHost(terminalPage.HostInfo);
                }
                else
                {
                    // Must be WelcomePage
                    _currentMonitorHost = null;
                    _currentMonitorClient = null;
                    ResetSidebar();
                }
            }
        }

        private void SwitchMonitorToHost(SshManager.SshConnectionInfo hostInfo)
        {
            if (_currentMonitorHost != null && _currentMonitorHost.Id == hostInfo.Id)
                return; // Already monitoring this host

            _currentMonitorHost = hostInfo;
            TxtHostIp.Text = $"IP {hostInfo.IpAddress}";
            ResetMonitorState();

            // Try to reuse or spin up background monitor connection
            if (_activeMonitors.ContainsKey(hostInfo.Id) && _activeMonitors[hostInfo.Id].IsConnected)
            {
                _currentMonitorClient = _activeMonitors[hostInfo.Id];
                MonitorTick(null, null); // Immediate update
            }
            else
            {
                System.Threading.Tasks.Task.Run(() => 
                {
                    try {
                        var client = new SshClient(hostInfo.IpAddress, hostInfo.SshPort, hostInfo.SshUser, hostInfo.DecryptedSshSecret ?? "");
                        client.Connect();
                        _activeMonitors[hostInfo.Id] = client;
                        
                        Dispatcher.InvokeAsync(() => {
                            if (_currentMonitorHost?.Id == hostInfo.Id)
                            {
                                _currentMonitorClient = client;
                                MonitorTick(null, null);
                            }
                        });
                    }
                    catch {}
                });
            }
        }

        private void ResetMonitorState()
        {
            _lastCpuTotal = 0;
            _lastCpuIdle = 0;
            _lastRx = 0;
            _lastTx = 0;
            _lastNetTime = DateTime.MinValue;
            _netHistory.Clear();
            _tickCount = 0;
            NetChartCanvas.Children.Clear();
        }

        private void ResetSidebar()
        {
            TxtHostIp.Text = "未连接";
            TxtUptime.Text = "运行 -- 天...";
            TxtPing.Text = "--ms";
            TxtLoad.Text = "负载 --, --, --";
            ProgCpu.Value = 0;
            TxtCpuPct.Text = "0%";
            TxtCpuText.Text = "0.0%";
            ProgMem.Value = 0;
            TxtMemPct.Text = "0%";
            TxtMemText.Text = "0M/0M";
            ProgSwap.Value = 0;
            TxtSwapPct.Text = "0%";
            TxtSwapText.Text = "0M/0M";
            TxtNetUp.Text = "0K";
            TxtNetDown.Text = "0K";
            TxtNetIface.Text = "--";
            TxtNetMax.Text = "100K";
            TxtNetMid.Text = "50K";
            NetChartCanvas.Children.Clear();
            _processes.Clear();
            _disks.Clear();
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMonitorHost != null)
            {
                Clipboard.SetText(_currentMonitorHost.IpAddress);
            }
        }

        private async void MonitorTick(object sender, EventArgs e)
        {
            if (_currentMonitorClient == null || !_currentMonitorClient.IsConnected) return;
            
            _tickCount++;
            PingCheckAsync();

            try
            {
                string cmdStr = "echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 11; echo \"==NET==\"; cat /proc/net/dev";
                if (_tickCount % 60 == 1) {
                    cmdStr += "; echo \"==DISK==\"; df -h --output=target,avail,size";
                }
                var cmd = _currentMonitorClient.CreateCommand(cmdStr);
                var result = await System.Threading.Tasks.Task.Run(() => cmd.Execute());
                ParseTopOutput(result);
            }
            catch { }
        }

        private async void PingCheckAsync()
        {
            if (_currentMonitorHost == null) return;
            try {
                using (var ping = new System.Net.NetworkInformation.Ping()) {
                    var reply = await ping.SendPingAsync(_currentMonitorHost.IpAddress, 2000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success) {
                        TxtPing.Text = $"{reply.RoundtripTime}ms";
                    } else {
                        TxtPing.Text = "超时";
                    }
                }
            }
            catch {
                TxtPing.Text = "错误";
            }
        }

        private void ParseTopOutput(string output)
        {
            var sections = output.Split(new[] { "==STAT==", "==TOP==", "==PROC==", "==NET==", "==DISK==" }, StringSplitOptions.None);
            if (sections.Length == 0) return;
            
            try {
                // Parse STAT (Instantaneous CPU Average)
                if (sections.Length > 1) {
                    var statLines = sections[1].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (statLines.Length > 0 && statLines[0].StartsWith("cpu ")) {
                        var parts = statLines[0].Substring(4).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4) {
                            ulong total = 0;
                            foreach (var p in parts) {
                                if (ulong.TryParse(p, out ulong v)) total += v;
                            }
                            ulong.TryParse(parts[3], out ulong idle);
                            if (parts.Length > 4 && ulong.TryParse(parts[4], out ulong iowait)) idle += iowait;

                            if (_lastCpuTotal > 0 && total > _lastCpuTotal) {
                                ulong deltaTotal = total - _lastCpuTotal;
                                ulong deltaIdle = idle - _lastCpuIdle;
                                double usage = 100.0 * (1.0 - (double)deltaIdle / deltaTotal);
                                if (usage < 0) usage = 0;
                                if (usage > 100) usage = 100;
                                
                                ProgCpu.Value = usage;
                                TxtCpuPct.Text = $"{usage:0.0}%";
                                TxtCpuText.Text = $"{usage:0.0}%";
                            }
                            _lastCpuTotal = total;
                            _lastCpuIdle = idle;
                        }
                    }
                }

                // Parse TOP (Load/Uptime/Mem/Swap)
                if (sections.Length > 2) {
                    var lines = sections[2].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0) {
                        string topStr = lines[0];
                        var upMatch = System.Text.RegularExpressions.Regex.Match(topStr, @"up\s+(.*?),?\s+\d+\s+user");
                        if (upMatch.Success) {
                            TxtUptime.Text = "运行 " + upMatch.Groups[1].Value.Trim();
                        }
                        
                        int loadIdx = topStr.IndexOf("average:");
                        if (loadIdx > 0) {
                            TxtLoad.Text = "负载 " + topStr.Substring(loadIdx + 8).Trim();
                        }

                        // Parse Mem line
                        var memLine = lines.FirstOrDefault(l => l.Contains(" Mem"));
                        if (memLine != null) {
                            double multiplier = 1.0;
                            if (memLine.StartsWith("MiB")) multiplier = 1024.0;
                            else if (memLine.StartsWith("GiB")) multiplier = 1024.0 * 1024.0;
                            
                            var parts = memLine.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            double total = 0, used = 0;
                            foreach(var part in parts) {
                                if (part.Contains("total")) total = ExtractDouble(part);
                                if (part.Contains("used")) used = ExtractDouble(part);
                            }
                            
                            total *= multiplier;
                            used *= multiplier;
                            
                            if (total > 0) {
                                double pct = used / total * 100.0;
                                ProgMem.Value = pct;
                                TxtMemPct.Text = $"{pct:0}%";
                                TxtMemText.Text = $"{FormatMemSize(used)}/{FormatMemSize(total)}";
                            }
                        }

                        // Parse Swap line
                        var swapLine = lines.FirstOrDefault(l => l.Contains(" Swap"));
                        if (swapLine != null) {
                            double multiplier = 1.0;
                            if (swapLine.StartsWith("MiB")) multiplier = 1024.0;
                            else if (swapLine.StartsWith("GiB")) multiplier = 1024.0 * 1024.0;
                            
                            var parts = swapLine.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            double total = 0, used = 0;
                            foreach(var part in parts) {
                                if (part.Contains("total")) total = ExtractDouble(part);
                                if (part.Contains("used")) used = ExtractDouble(part);
                            }
                            total *= multiplier;
                            used *= multiplier;
                            
                            if (total > 0) {
                                double pct = used / total * 100.0;
                                ProgSwap.Value = pct;
                                TxtSwapPct.Text = $"{pct:0}%";
                                TxtSwapText.Text = $"{FormatMemSize(used)}/{FormatMemSize(total)}";
                            }
                        }
                    }
                }

                // Parse Processes from section 3
                if (sections.Length > 3) {
                    _processes.Clear();
                    var procLines = sections[3].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < procLines.Length; i++) {
                        var parts = procLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3) {
                            string mem = parts[0] + "%";
                            string cpu = parts[1] + "%";
                            string cmd = string.Join(" ", parts.Skip(2));
                            if (cmd.Length > 30) cmd = cmd.Substring(0, 30) + "...";
                            _processes.Add(new ProcessItem { Mem = mem, Cpu = cpu, Cmd = cmd });
                        }
                    }
                }
                
                // Parse Net from section 4
                if (sections.Length > 4) {
                    var netLines = sections[4].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var nl in netLines) {
                        if (nl.Contains(":") && !nl.Contains("lo:")) {
                            var parts = nl.Trim().Split(new[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 10) {
                                string iface = parts[0];
                                if (ulong.TryParse(parts[1], out ulong rx) && ulong.TryParse(parts[9], out ulong tx)) {
                                    TxtNetIface.Text = iface;
                                    var now = DateTime.Now;
                                    if (_lastNetTime != DateTime.MinValue) {
                                        double secs = (now - _lastNetTime).TotalSeconds;
                                        if (secs > 0) {
                                            double rxSpeed = 0;
                                            double txSpeed = 0;
                                            
                                            if (rx >= _lastRx && _lastRx > 0) rxSpeed = (rx - _lastRx) / secs;
                                            if (tx >= _lastTx && _lastTx > 0) txSpeed = (tx - _lastTx) / secs;
                                            
                                            if (_lastRx > 0 && _lastTx > 0) {
                                                TxtNetDown.Text = FormatNetSpeed(rxSpeed);
                                                TxtNetUp.Text = FormatNetSpeed(txSpeed);
                                                DrawNetChart(rxSpeed, txSpeed);
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

                // Parse Disk from section 5
                if (sections.Length > 5) {
                    _disks.Clear();
                    var diskLines = sections[5].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < diskLines.Length; i++) {
                        var parts = diskLines[i].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3) {
                            string target = parts[0];
                            string avail = parts[1];
                            string size = parts[2];
                            _disks.Add(new DiskItem { Path = target, Avail = avail, Size = size });
                        }
                    }
                }
            }
            catch {}
        }

        private void DrawNetChart(double rx, double tx)
        {
            _netHistory.Add((rx, tx));
            if (_netHistory.Count > 50) _netHistory.RemoveAt(0);

            NetChartCanvas.Children.Clear();
            if (_netHistory.Count == 0) return;

            double maxVal = _netHistory.Max(x => Math.Max(x.rx, x.tx));
            if (maxVal < 1024) maxVal = 1024;

            TxtNetMax.Text = FormatNetSpeed(maxVal);
            TxtNetMid.Text = FormatNetSpeed(maxVal / 2);

            double width = NetChartCanvas.ActualWidth;
            double height = NetChartCanvas.ActualHeight;
            if (width == 0 || height == 0) return;

            double barWidth = width / 50.0;
            
            for (int i = 0; i < _netHistory.Count; i++)
            {
                var val = _netHistory[i];
                double rxH = (val.rx / maxVal) * height;
                double txH = (val.tx / maxVal) * height;

                var rectRx = new Rectangle {
                    Width = Math.Ceiling(barWidth),
                    Height = rxH,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 39, 174, 96))
                };
                Canvas.SetLeft(rectRx, i * barWidth);
                Canvas.SetTop(rectRx, height - rxH);
                NetChartCanvas.Children.Add(rectRx);

                var rectTx = new Rectangle {
                    Width = Math.Ceiling(barWidth),
                    Height = txH,
                    Fill = new SolidColorBrush(Color.FromArgb(180, 216, 67, 21))
                };
                Canvas.SetLeft(rectTx, i * barWidth);
                Canvas.SetTop(rectTx, height - txH);
                NetChartCanvas.Children.Add(rectTx);
            }
        }

        private string FormatNetSpeed(double bytesPerSec)
        {
            if (bytesPerSec > 1024 * 1024) return (bytesPerSec / 1024 / 1024).ToString("0.0") + "M";
            if (bytesPerSec > 1024) return (bytesPerSec / 1024).ToString("0") + "K";
            return bytesPerSec.ToString("0") + "B";
        }

        private double ExtractDouble(string s)
        {
            var match = System.Text.RegularExpressions.Regex.Match(s, @"[\d\.]+");
            if (match.Success && double.TryParse(match.Value, out double val)) return val;
            return 0;
        }

        private string FormatMemSize(double kb)
        {
            if (kb > 1024 * 1024) return (kb / 1024 / 1024).ToString("0.0") + "G";
            return (kb / 1024).ToString("0") + "M";
        }

        private void MicaWindow_Closed(object sender, EventArgs e)
        {
            _timer?.Stop();
            
            foreach (var client in _activeMonitors.Values)
            {
                try {
                    client.Disconnect();
                    client.Dispose();
                } catch {}
            }
            _activeMonitors.Clear();

            foreach (var item in SessionTabs.Items.OfType<TabItem>())
            {
                if (item.Tag is Pages.TerminalAndSFTP terminalPage)
                {
                    terminalPage.Disconnect();
                }
            }
        }
    }
}
