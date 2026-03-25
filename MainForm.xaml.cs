using Microsoft.Terminal.Wpf;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Collections.ObjectModel;
using System.Linq;

namespace FreeWPFShell
{
    /// <summary>
    /// MainForm.xaml 的交互逻辑
    /// </summary>
    public class RemoteFile
    {
        public string Icon { get; set; }
        public string Name { get; set; }
        public string Size { get; set; }
        public string Type { get; set; }
        public string Date { get; set; }
        public string Perms { get; set; }
        public string Owner { get; set; }
        public bool IsDirectory { get; set; }
        public string FullName { get; set; }
    }

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
        private ConPtyConnection _connection;
        
        private SftpClient _sftpClient;
        private SshClient _sshMonitorClient;
        private System.Windows.Threading.DispatcherTimer _timer;
        
        private ObservableCollection<RemoteFile> _remoteFiles = new ObservableCollection<RemoteFile>();
        private ObservableCollection<ProcessItem> _processes = new ObservableCollection<ProcessItem>();
        private ObservableCollection<DiskItem> _disks = new ObservableCollection<DiskItem>();
        private ulong _lastCpuTotal = 0, _lastCpuIdle = 0;
        private ulong _lastRx = 0, _lastTx = 0;
        private DateTime _lastNetTime = DateTime.MinValue;
        private List<(double rx, double tx)> _netHistory = new List<(double, double)>();
        private int _tickCount = 0;
        private string _currentPath = "/";
        private Stack<string> _backHistory = new Stack<string>();
        private Stack<string> _forwardHistory = new Stack<string>();

        public MainForm()
        {
            InitializeComponent();
            Terminal.Loaded += Terminal_Loaded;
        }

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            uint[] colorTable = new uint[16]
            {
                0x000c0c0c, 0x001f0fc5, 0x000ea113, 0x00009cc1,
                0x00da3700, 0x00981788, 0x00dd963a, 0x00cccccc,
                0x00767676, 0x005648e7, 0x000cc616, 0x00a5f1f9,
                0x00ff783b, 0x009e00b4, 0x00d6d661, 0x00f2f2f2
            };

            var theme = new TerminalTheme
            {
                DefaultBackground = 0x0047301E, // #1E3047 (BGR format)
                DefaultForeground = 0x00ffffff,
                DefaultSelectionBackground = 0x00ffffff,
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = colorTable
            };

            Terminal.SetTheme(theme, "Cascadia Code", 10);

             //_connection = new ConPtyConnection("cmd.exe", 120, 30);
            _connection = new ConPtyConnection("ssh cloudyou@192.168.80.128", 120, 40);
            Terminal.Connection = _connection;

            Terminal.Focus();
            
            // Auto login via Output Hook (wait for prompt instead of blind delay)
            string outputBuffer = "";
            EventHandler<Microsoft.Terminal.Wpf.TerminalOutputEventArgs> onOutput = null;
            onOutput = (s, args) =>
            {
                if (args.Data != null)
                {
                    outputBuffer += args.Data;
                    if (outputBuffer.ToLower().Contains("password:"))
                    {
                        _connection.TerminalOutput -= onOutput;
                        System.Threading.Tasks.Task.Delay(50).ContinueWith(_ => _connection.WriteInput("1234\n"));
                    }
                    if (outputBuffer.Length > 2048) outputBuffer = ""; 
                }
            };
            _connection.TerminalOutput += onOutput;
            
            // Auto resize hack to fix TUI rendering glitches
            Dispatcher.InvokeAsync(async () => 
            {
                await System.Threading.Tasks.Task.Delay(600); // After login happens
                Terminal.Margin = new Thickness(10, 10, 10, 11);
                await System.Threading.Tasks.Task.Delay(100);
                Terminal.Margin = new Thickness(10);
            }, System.Windows.Threading.DispatcherPriority.Background);
            
            try
            {
                _sftpClient = new SftpClient("192.168.80.128", 22, "cloudyou", "1234");
                _sftpClient.Connect();
                _currentPath = _sftpClient.WorkingDirectory ?? "/";
                FileGrid.ItemsSource = _remoteFiles;
                LoadPath(_currentPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("SFTP连接失败: " + ex.Message);
            }

            // Start Monitoring Thread
            ProcessGrid.ItemsSource = _processes;
            DiskGrid.ItemsSource = _disks;
            
            System.Threading.Tasks.Task.Run(() => 
            {
                try {
                    _sshMonitorClient = new SshClient("192.168.80.128", 22, "cloudyou", "1234");
                    _sshMonitorClient.Connect();
                    
                    Dispatcher.InvokeAsync(() => {
                        _timer = new System.Windows.Threading.DispatcherTimer();
                        _timer.Interval = TimeSpan.FromSeconds(2);
                        _timer.Tick += MonitorTick;
                        _timer.Start();
                        MonitorTick(null, null); // Fire immediately once
                    });
                }
                catch {}
            });
        }

        private void Terminal_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Intercept Tab key before WPF attempts to use it for focus navigation
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                _connection?.WriteInput("\t");
            }
        }

        private async void MonitorTick(object sender, EventArgs e)
        {
            if (_sshMonitorClient == null || !_sshMonitorClient.IsConnected) return;
            
            _tickCount++;
            PingCheckAsync();

            try
            {
                string cmdStr = "echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 11; echo \"==NET==\"; cat /proc/net/dev";
                if (_tickCount % 60 == 1) {
                    cmdStr += "; echo \"==DISK==\"; df -h --output=target,avail,size";
                }
                var cmd = _sshMonitorClient.CreateCommand(cmdStr);
                var result = await System.Threading.Tasks.Task.Run(() => cmd.Execute());
                ParseTopOutput(result);
            }
            catch { }
        }

        private async void PingCheckAsync()
        {
            try {
                using (var ping = new System.Net.NetworkInformation.Ping()) {
                    var reply = await ping.SendPingAsync("192.168.32.132", 2000);
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
                    // line 0 is header: %MEM %CPU COMMAND
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
                            break; // use first external interface
                        }
                    }
                }

                // Parse Disk from section 5
                if (sections.Length > 5) {
                    _disks.Clear();
                    var diskLines = sections[5].Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < diskLines.Length; i++) { // Skip header
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
            if (maxVal < 1024) maxVal = 1024; // Min scale 1KB/s

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

        private void LoadPath(string path, bool isHistory = false)
        {
            if (_sftpClient == null || !_sftpClient.IsConnected) return;
            try
            {
                var files = _sftpClient.ListDirectory(path);
                
                if (!isHistory && _currentPath != path)
                {
                    _backHistory.Push(_currentPath);
                    _forwardHistory.Clear();
                }
                
                _currentPath = path;
                TxtCurrentPath.Text = path;
                
                var list = new List<RemoteFile>();
                foreach (var f in files)
                {
                    if (f.Name == "." || f.Name == "..") continue;
                    
                    list.Add(new RemoteFile
                    {
                        Icon = f.IsDirectory ? "📁" : "📄",
                        Name = f.Name,
                        Size = f.IsDirectory ? "" : FormatSize(f.Length),
                        Type = f.IsDirectory ? "文件夹" : "文件",
                        Date = f.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                        Perms = GetPerms(f),
                        Owner = $"{f.UserId}::{f.GroupId}",
                        IsDirectory = f.IsDirectory,
                        FullName = f.FullName
                    });
                }
                
                _remoteFiles.Clear();
                foreach (var f in list.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name))
                {
                    _remoteFiles.Add(f);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("访问失败: " + ex.Message);
            }
        }

        private string GetPerms(ISftpFile f)
        {
            string s = f.IsDirectory ? "d" : "-";
            s += f.OwnerCanRead ? "r" : "-";
            s += f.OwnerCanWrite ? "w" : "-";
            s += f.OwnerCanExecute ? "x" : "-";
            s += f.GroupCanRead ? "r" : "-";
            s += f.GroupCanWrite ? "w" : "-";
            s += f.GroupCanExecute ? "x" : "-";
            s += f.OthersCanRead ? "r" : "-";
            s += f.OthersCanWrite ? "w" : "-";
            s += f.OthersCanExecute ? "x" : "-";
            return s;
        }

        private string FormatSize(long bytes)
        {
            string[] exts = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double d = bytes;
            while (d >= 1024 && i < exts.Length - 1)
            {
                d /= 1024;
                i++;
            }
            return $"{d:0.##} {exts[i]}";
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_backHistory.Count > 0)
            {
                _forwardHistory.Push(_currentPath);
                LoadPath(_backHistory.Pop(), true);
            }
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (_forwardHistory.Count > 0)
            {
                _backHistory.Push(_currentPath);
                LoadPath(_forwardHistory.Pop(), true);
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadPath(_currentPath, true);
        }

        private void BtnUp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPath != "/")
            {
                int lastSlash = _currentPath.TrimEnd('/').LastIndexOf('/');
                string parent = lastSlash > 0 ? _currentPath.Substring(0, lastSlash) : "/";
                LoadPath(parent);
            }
        }

        private void BtnNewFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string newDir = _currentPath == "/" ? "/NewFolder" : _currentPath.TrimEnd('/') + "/NewFolder";
                _sftpClient.CreateDirectory(newDir);
                LoadPath(_currentPath, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("新建文件夹失败: " + ex.Message);
            }
        }

        private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile selectedFile && selectedFile.IsDirectory)
            {
                LoadPath(selectedFile.FullName);
            }
        }

        private void MicaWindow_Closed(object sender, EventArgs e)
        {
            _timer?.Stop();
            _sshMonitorClient?.Disconnect();
            _sshMonitorClient?.Dispose();
            _sftpClient?.Disconnect();
            _sftpClient?.Dispose();
            _connection?.Close();
        }
    }
}
