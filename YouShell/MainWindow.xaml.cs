using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using YouShell.Models;
using YouShell.Services;
using YouShell.Share;
using YouShell.Views;

namespace YouShell
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<SshSessionService> ActiveSessions { get; } = new();
        private SshSessionService? _currentSession;

        // 网络图表复用画刷与预分配矩形
        private static readonly SolidColorBrush s_chartRxFill = new(Windows.UI.Color.FromArgb(120, 39, 174, 96));
        private static readonly SolidColorBrush s_chartTxFill = new(Windows.UI.Color.FromArgb(180, 216, 67, 21));
        private readonly Rectangle[] _chartRxRects = new Rectangle[50];
        private readonly Rectangle[] _chartTxRects = new Rectangle[50];
        private bool _chartInitialized;

        private readonly ObservableCollection<ProcessItem> _sidebarProcesses = new();
        private readonly ObservableCollection<DiskItem> _sidebarDisks = new();

        public MainWindow()
        {
            InitializeComponent();
            Title = "YouShell";

            var settingsRepo = Core.AppServices.GetService<Repositories.SettingsRepository>();
            Services.BackdropService.Apply(this, settingsRepo.Load().BackdropType);

            ProcessGrid.ItemsSource = _sidebarProcesses;
            DiskGrid.ItemsSource = _sidebarDisks;
            WelcomePageControl.OpenSessionRequested = OpenSession;
            Closed += MainWindow_Closed;
        }

        // ── 会话 / 标签页 ────────────────────────────────────────

        public void OpenSession(SshConnectionInfo hostInfo)
        {
            var session = new SshSessionService(hostInfo);
            ActiveSessions.Add(session);

            var terminalPage = new TerminalAndSFTP(session);
            terminalPage.OpenSshTunnelRequested = OpenSshTunnelManager;
            terminalPage.OpenTracerouteRequested = OpenTraceroutePage;
            terminalPage.OpenSystemManagementRequested = OpenSystemManagementPage;
            terminalPage.CloseTabRequested = CloseTabByContent;

            var tab = AddTab(session.DisplayName, terminalPage);

            session.OnConnected = () => terminalPage.BindSession();
            session.OnConnectFailed = async (ex) =>
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                await UserForm.ModernMessageBox.ShowAsync(
                    $"无法连接到 {hostInfo.HostName} ({hostInfo.IpAddress})\n\n错误信息: {msg}",
                    "连接失败", UserForm.MessageBoxButton.OK, UserForm.MessageBoxImage.Error);

                if (SessionTabs.TabItems.Contains(tab))
                {
                    SessionTabs.TabItems.Remove(tab);
                    ActiveSessions.Remove(session);
                    session.Disconnect();
                }
            };

            session.ConnectAsync();
        }

        private void OpenSshTunnelManager()
        {
            AddTab("SSH隧道管理", new SshTunnelPage(ActiveSessions));
        }

        private void OpenTraceroutePage(string? initialTarget = null)
        {
            var page = new TraceroutePage();
            AddTab("路由追踪", page);
            if (!string.IsNullOrEmpty(initialTarget)) page.ViewModel.Target = initialTarget;
        }

        private void OpenSystemManagementPage(SshSessionService session)
        {
            AddTab($"系统管理-{session.DisplayName}", new SystemManagementPage(session));
        }

        private TabViewItem AddTab(string header, FrameworkElement content)
        {
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            var tab = new TabViewItem { Header = header, Content = content, Tag = content };
            SessionTabs.TabItems.Add(tab);
            SessionTabs.SelectedItem = tab;
            return tab;
        }

        private void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is TabViewItem tab) CloseTab(tab);
        }

        private void CloseTabByContent(TerminalAndSFTP page)
        {
            foreach (var item in SessionTabs.TabItems)
                if (item is TabViewItem tab && tab.Tag == page) { CloseTab(tab); return; }
        }

        private void CloseTab(TabViewItem tab)
        {
            if (tab == WelcomeTab) return;
            SessionTabs.TabItems.Remove(tab);

            if (tab.Tag is TerminalAndSFTP termPage)
            {
                var session = termPage.Session;
                termPage.Cleanup();
                if (session != null)
                {
                    ActiveSessions.Remove(session);
                    session.Dispose();
                }
            }
            else if (tab.Tag is IDisposable d)
            {
                d.Dispose();
            }
        }

        private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionTabs.SelectedItem is TabViewItem selectedTab && selectedTab.Tag is TerminalAndSFTP tp)
                SwitchMonitorToSession(tp.Session);
            else
            {
                UnsubscribeMonitor();
                _currentSession = null;
                ResetSidebar();
            }
        }

        // ── 侧栏监控 ────────────────────────────────────────────

        private void SwitchMonitorToSession(SshSessionService session)
        {
            if (_currentSession == session) return;
            UnsubscribeMonitor();
            _currentSession = session;
            TxtHostIp.Text = $"IP {session.HostInfo.IpAddress}";
            TxtHostGeo.Text = IpGeoService.Instance.Query(session.HostInfo.IpAddress).SimpleGeo;

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
                e.PropertyName == nameof(MonitorData.NetHistory))
                Core.UiDispatcher.Enqueue(() => DrawNetChart(data));
        }

        private void OnMonitorBulkUpdate(object? sender, MonitorData data)
        {
            if (sender is SshSessionService s && s == _currentSession)
                Core.UiDispatcher.Enqueue(() => DisplayMonitorData(data));
        }

        private void DisplayMonitorData(MonitorData d)
        {
            ProgCpu.Value = d.CpuPct; TxtCpuText.Text = d.CpuText;
            ProgMem.Value = d.MemPct; TxtMemText.Text = d.MemText;
            ProgSwap.Value = d.SwapPct; TxtSwapText.Text = d.SwapText;
            TxtUptime.Text = d.Uptime; TxtPing.Text = d.Ping; TxtLoad.Text = d.Load;
            TxtNetUp.Text = d.NetUp; TxtNetDown.Text = d.NetDown; TxtNetIface.Text = d.NetIface;
            TxtNetMax.Text = d.NetMax; TxtNetMid.Text = d.NetMid;
            SyncCollection(_sidebarProcesses, d.Processes);
            SyncCollection(_sidebarDisks, d.Disks);
            DrawNetChart(d);
        }

        private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
        {
            int srcCount = source.Count;
            while (target.Count > srcCount) target.RemoveAt(target.Count - 1);
            for (int i = 0; i < srcCount; i++)
            {
                if (i < target.Count)
                {
                    if (!EqualityComparer<T>.Default.Equals(target[i], source[i])) target[i] = source[i];
                }
                else target.Add(source[i]);
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
            ProgCpu.Value = 0; TxtCpuText.Text = "0.0%";
            ProgMem.Value = 0; TxtMemText.Text = "0M/0M";
            ProgSwap.Value = 0; TxtSwapText.Text = "0M/0M";
            TxtNetUp.Text = "0K"; TxtNetDown.Text = "0K"; TxtNetIface.Text = "--";
            TxtNetMax.Text = "100K"; TxtNetMid.Text = "50K";
            NetChartCanvas.Children.Clear(); _chartInitialized = false;
            _sidebarProcesses.Clear(); _sidebarDisks.Clear();
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession == null) return;
            var dp = new DataPackage();
            dp.SetText(_currentSession.HostInfo.IpAddress);
            Clipboard.SetContent(dp);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            UnsubscribeMonitor();
            foreach (var s in ActiveSessions) try { s.Dispose(); } catch { }
            ActiveSessions.Clear();
        }
    }
}
