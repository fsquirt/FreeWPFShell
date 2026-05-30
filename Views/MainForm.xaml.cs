using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FreeWPFShell.Models;
using FreeWPFShell.Services;

namespace FreeWPFShell.Views
{
    public partial class MainForm
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        public ObservableCollection<SshSessionService> ActiveSessions { get; } = new();
        private SshSessionService? _currentSession;

        // 复用的画刷
        private static readonly SolidColorBrush s_activeTabBg = new(Color.FromRgb(0x2D, 0x2D, 0x30));
        private static readonly SolidColorBrush s_chartRxFill = new(Color.FromArgb(120, 39, 174, 96));
        private static readonly SolidColorBrush s_chartTxFill = new(Color.FromArgb(180, 216, 67, 21));

        // 预创建的柱状图矩形（复用，不每 tick new）
        private readonly Rectangle[] _chartRxRects = new Rectangle[50];
        private readonly Rectangle[] _chartTxRects = new Rectangle[50];
        private bool _chartInitialized;

        // 侧边栏绑定的 ObservableCollection，直接复用 MonitorData 的集合
        private readonly ObservableCollection<ProcessItem> _sidebarProcesses = new();
        private readonly ObservableCollection<DiskItem> _sidebarDisks = new();

        public MainForm()
        {
            InitializeComponent();

            var settingsRepo = new Repositories.SettingsRepository();
            BackdropService.ApplyToAllWindows(settingsRepo.Load().BackdropType);

            var welcomePage = new WelcomePage();
            WelcomeTab.Tag = welcomePage;
            PagesContainer.Children.Add(welcomePage);

            ProcessGrid.ItemsSource = _sidebarProcesses;
            DiskGrid.ItemsSource = _sidebarDisks;

            if (System.Environment.OSVersion.Version.Build < 22000)
            {
                UserForm.ModernMessageBox.Show("你正在使用不支持的操作系统，你仍然可以使用此程序，但是部分现代功能如云母材质窗口会失效", "不支持的操作系统", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void OpenSession(SshConnectionInfo hostInfo)
        {
            var session = new SshSessionService(hostInfo);
            ActiveSessions.Add(session);

            var terminalPage = new TerminalAndSFTP(session);
            var tabItem = AddTab(session.DisplayName, terminalPage);

            session.OnConnected = () =>
            {
                terminalPage.BindSession();
            };

            session.OnConnectFailed = (ex) =>
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                UserForm.ModernMessageBox.Show($"无法连接到 {hostInfo.HostName} ({hostInfo.IpAddress})\n\n错误信息: {msg}", "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);

                if (SessionTabs.Items.Contains(tabItem))
                {
                    PagesContainer.Children.Remove(terminalPage);
                    SessionTabs.Items.Remove(tabItem);
                    ActiveSessions.Remove(session);
                    session.Disconnect();
                }
            };

            session.ConnectAsync();
        }

        public void OpenSshTunnelManager()
        {
            var page = new SshTunnelPage();
            AddTab("SSH隧道管理", page);
        }

        public void OpenTraceroutePage(string? initialTarget = null)
        {
            var page = new TraceroutePage();
            AddTab("路由追踪", page);
            if (!string.IsNullOrEmpty(initialTarget)) page.TxtTarget.Text = initialTarget;
        }

        public void OpenSystemManagementPage(SshSessionService session)
        {
            var page = new SystemManagementPage(session);
            AddTab($"系统管理-{session.DisplayName}", page);
        }

        public void CloseTab(UIElement content)
        {
            TabItem? targetTab = null;
            foreach (TabItem item in SessionTabs.Items)
            {
                if (item.Tag == content)
                {
                    targetTab = item;
                    break;
                }
            }

            if (targetTab != null)
            {
                PagesContainer.Children.Remove(content);
                SessionTabs.Items.Remove(targetTab);

                if (content is TerminalAndSFTP termPage)
                {
                    // 先清理引用链（退订事件、释放 Terminal 原生资源），再断开连接
                    termPage.Cleanup();
                    if (termPage.Session != null)
                    {
                        ActiveSessions.Remove(termPage.Session);
                        termPage.Session.Dispose();
                    }
                }

                if (content is IDisposable d) d.Dispose();
                else if (content is FrameworkElement fe && fe.DataContext is IDisposable disposable)
                    disposable.Dispose();

                // 等后台断开线程跑完，然后GC回收 + 强制trim工作集
                Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    // 等效于 memreduct：强制OS回收进程的物理页面
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
                });
            }
        }

        private TabItem AddTab(string header, UIElement content)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });

            var btnClose = new Button
            {
                Content = "\u00d7", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };

            var tabItem = new TabItem { Header = headerPanel, Tag = content };
            btnClose.Click += (s, e) =>
            {
                CloseTab(content);
            };
            headerPanel.Children.Add(btnClose);
            PagesContainer.Children.Add(content);
            SessionTabs.Items.Add(tabItem);
            SessionTabs.SelectedItem = tabItem;
            return tabItem;
        }

        private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionTabs.SelectedItem is TabItem selectedTab && selectedTab.Tag is UIElement activeView)
            {
                foreach (UIElement child in PagesContainer.Children)
                    child.Visibility = (child == activeView) ? Visibility.Visible : Visibility.Collapsed;

                if (activeView is TerminalAndSFTP tp)
                    SwitchMonitorToSession(tp.Session);
                else
                {
                    UnsubscribeMonitor();
                    _currentSession = null;
                    ResetSidebar();
                }
            }
        }

        private void SwitchMonitorToSession(SshSessionService session)
        {
            if (_currentSession == session) return;
            UnsubscribeMonitor();
            _currentSession = session;
            TxtHostIp.Text = $"IP {session.HostInfo.IpAddress}";
            TxtHostGeo.Text = Share.IpGeoService.Instance.Query(session.HostInfo.IpAddress).SimpleGeo;

            session.Monitor.PropertyChanged += OnMonitorPropertyChanged;
            session.MonitorUpdated += OnMonitorBulkUpdate;
            DisplayMonitorData(session.Monitor);
        }

        private void UnsubscribeMonitor()
        {
            if (_currentSession != null)
            {
                _currentSession.Monitor.PropertyChanged -= OnMonitorPropertyChanged;
                _currentSession.MonitorUpdated -= OnMonitorBulkUpdate;
            }
        }

        private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is MonitorData data && data == _currentSession?.Monitor &&
                (e.PropertyName == nameof(MonitorData.NetHistory)))
                Dispatcher.BeginInvoke(() => DrawNetChart(data));
        }

        private void OnMonitorBulkUpdate(object? sender, MonitorData data)
        {
            if (sender is SshSessionService s && s == _currentSession)
                Dispatcher.BeginInvoke(() => DisplayMonitorData(data));
        }

        private void DisplayMonitorData(MonitorData d)
        {
            ProgCpu.Value = d.CpuPct; TxtCpuPct.Text = d.CpuText; TxtCpuText.Text = d.CpuText;
            ProgMem.Value = d.MemPct; TxtMemPct.Text = $"{d.MemPct:F1}%"; TxtMemText.Text = d.MemText;
            ProgSwap.Value = d.SwapPct; TxtSwapPct.Text = $"{d.SwapPct:F1}%"; TxtSwapText.Text = d.SwapText;
            TxtUptime.Text = d.Uptime; TxtPing.Text = d.Ping; TxtLoad.Text = d.Load;
            TxtNetUp.Text = d.NetUp; TxtNetDown.Text = d.NetDown; TxtNetIface.Text = d.NetIface;
            TxtNetMax.Text = d.NetMax; TxtNetMid.Text = d.NetMid;

            // 内联更新而不创建新 ObservableCollection
            SyncCollection(_sidebarProcesses, d.Processes);
            SyncCollection(_sidebarDisks, d.Disks);
            DrawNetChart(d);
        }

        /// <summary>内联同步：避免 new ObservableCollection 造成 GC 压力</summary>
        private static void SyncCollection<T>(ObservableCollection<T> target, System.Collections.Generic.IReadOnlyList<T> source)
        {
            int srcCount = source.Count;
            // 调整大小
            while (target.Count > srcCount) target.RemoveAt(target.Count - 1);
            for (int i = 0; i < srcCount; i++)
            {
                if (i < target.Count)
                {
                    if (!EqualityComparer<T>.Default.Equals(target[i], source[i]))
                        target[i] = source[i];
                }
                else
                {
                    target.Add(source[i]);
                }
            }
        }

        private void DrawNetChart(MonitorData d)
        {
            var history = d.NetHistory;
            int count = history.Count;
            if (count == 0) return;

            double width = NetChartCanvas.ActualWidth, height = NetChartCanvas.ActualHeight;
            if (width == 0 || height == 0) return;

            double maxVal = d.GetNetHistoryMax();
            if (maxVal < 1024) maxVal = 1024;
            double barW = width / 50.0;

            // 懒初始化预分配的矩形
            if (!_chartInitialized)
            {
                for (int i = 0; i < 50; i++)
                {
                    var rxRect = new Rectangle { Width = Math.Ceiling(barW), Fill = s_chartRxFill };
                    Canvas.SetLeft(rxRect, i * barW);
                    NetChartCanvas.Children.Add(rxRect);
                    _chartRxRects[i] = rxRect;

                    var txRect = new Rectangle { Width = Math.Ceiling(barW), Fill = s_chartTxFill };
                    Canvas.SetLeft(txRect, i * barW);
                    NetChartCanvas.Children.Add(txRect);
                    _chartTxRects[i] = txRect;
                }
                _chartInitialized = true;
            }

            // 仅更新高度和底部位置，不创建新对象
            for (int i = 0; i < 50; i++)
            {
                var rxRect = _chartRxRects[i];
                var txRect = _chartTxRects[i];
                if (i < count)
                {
                    var (rx, tx) = history[i];
                    double rxH = (rx / maxVal) * height, txH = (tx / maxVal) * height;
                    rxRect.Height = rxH; rxRect.Visibility = Visibility.Visible;
                    Canvas.SetTop(rxRect, height - rxH);
                    txRect.Height = txH; txRect.Visibility = Visibility.Visible;
                    Canvas.SetTop(txRect, height - txH);
                    // 更新宽度以防 DPI / 布局变化
                    double newW = Math.Ceiling(barW);
                    rxRect.Width = newW; txRect.Width = newW;
                    Canvas.SetLeft(rxRect, i * barW); Canvas.SetLeft(txRect, i * barW);
                }
                else
                {
                    rxRect.Visibility = Visibility.Collapsed;
                    txRect.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ResetSidebar()
        {
            TxtHostIp.Text = "未连接"; TxtHostGeo.Text = "";
            TxtUptime.Text = "运行 -- 天..."; TxtPing.Text = "--ms"; TxtLoad.Text = "负载 --, --, --";
            ProgCpu.Value = 0; TxtCpuPct.Text = "0%"; TxtCpuText.Text = "0.0%";
            ProgMem.Value = 0; TxtMemPct.Text = "0%"; TxtMemText.Text = "0M/0M";
            ProgSwap.Value = 0; TxtSwapPct.Text = "0%"; TxtSwapText.Text = "0M/0M";
            TxtNetUp.Text = "0K"; TxtNetDown.Text = "0K"; TxtNetIface.Text = "--";
            TxtNetMax.Text = "100K"; TxtNetMid.Text = "50K";
            NetChartCanvas.Children.Clear(); _chartInitialized = false;
            _sidebarProcesses.Clear(); _sidebarDisks.Clear();
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null)
            {
                try { Clipboard.SetText(_currentSession.HostInfo.IpAddress); }
                catch (Exception ex) { UserForm.ModernMessageBox.Show("复制到剪切板失败: " + ex.Message); }
            }
        }

        private void MicaWindow_Closed(object sender, EventArgs e)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                Environment.Exit(0);
            });

            UnsubscribeMonitor();
            foreach (var s in ActiveSessions) try { s.Dispose(); } catch { }
            ActiveSessions.Clear();
        }
    }
}
