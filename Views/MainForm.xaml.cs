using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{
    public partial class MainForm
    {
        public ObservableCollection<SshSessionService> ActiveSessions { get; } = new();
        private SshSessionService? _currentSession;

        public MainForm()
        {
            InitializeComponent();

            var settingsRepo = new Repositories.SettingsRepository();
            BackdropService.ApplyToAllWindows(settingsRepo.Load().BackdropType);

            var welcomePage = new WelcomePage();
            WelcomeTab.Tag = welcomePage;
            PagesContainer.Children.Add(welcomePage);

            if (System.Environment.OSVersion.Version.Build < 22000)
            {
                UserForm.ModernMessageBox.Show("你正在使用不支持的操作系统，你仍然可以使用此程序，但是部分现代功能如云母材质窗口会失效", "不支持的操作系统", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public async void OpenSession(SshConnectionInfo hostInfo)
        {
            var session = new SshSessionService(hostInfo);
            ActiveSessions.Add(session);

            var terminalPage = new TerminalAndSFTP(session);
            var tabItem = AddTab(session.DisplayName, terminalPage);

            try
            {
                await session.ConnectAsync();
                terminalPage.BindSession();
            }
            catch (Exception ex)
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                UserForm.ModernMessageBox.Show($"无法连接到 {hostInfo.HostName} ({hostInfo.IpAddress})\n\n错误信息: {msg}", "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
                
                // 检查 Tab 是否已被用户手动关闭，防止二次移除导致崩溃
                if (SessionTabs.Items.Contains(tabItem))
                {
                    PagesContainer.Children.Remove(terminalPage);
                    SessionTabs.Items.Remove(tabItem);
                    ActiveSessions.Remove(session);
                    session.Dispose();
                }
            }
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

        public void OpenProcessPage(SshSessionService session)
        {
            var page = new ProcessPage(session);
            AddTab($"进程-{session.DisplayName}", page);
        }

        public void OpenSystemManagementPage(SshSessionService session)
        {
            var page = new SystemManagementPage(session);
            AddTab($"系统管理-{session.DisplayName}", page);
        }

        private TabItem AddTab(string header, UIElement content)
        {
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });

            var btnClose = new Button
            {
                Content = "×", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };

            var tabItem = new TabItem { Header = headerPanel, Tag = content };
            btnClose.Click += (s, e) =>
            {
                PagesContainer.Children.Remove(content);
                SessionTabs.Items.Remove(tabItem);
                
                // 处理 SSH 会话资源的彻底释放
                if (content is TerminalAndSFTP termPage)
                {
                    if (termPage.Session != null)
                    {
                        ActiveSessions.Remove(termPage.Session);
                        termPage.Session.Dispose();
                    }
                }

                if (content is IDisposable d) d.Dispose();
                else if (content is FrameworkElement fe && fe.DataContext is IDisposable disposable)
                    disposable.Dispose();
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
                (e.PropertyName == nameof(MonitorData.NetHistory) || e.PropertyName == nameof(MonitorData.NetRxSpeed)))
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
            ProcessGrid.ItemsSource = new ObservableCollection<ProcessItem>(d.Processes);
            DiskGrid.ItemsSource = new ObservableCollection<DiskItem>(d.Disks);
            DrawNetChart(d);
        }

        private void DrawNetChart(MonitorData d)
        {
            NetChartCanvas.Children.Clear();
            var history = d.NetHistory;
            if (history.Count == 0) return;
            double maxVal = history.Max(x => Math.Max(x.Item1, x.Item2));
            if (maxVal < 1024) maxVal = 1024;
            double width = NetChartCanvas.ActualWidth, height = NetChartCanvas.ActualHeight;
            if (width == 0 || height == 0) return;
            double barW = width / 50.0;
            for (int i = 0; i < history.Count; i++)
            {
                var (rx, tx) = history[i];
                double rxH = (rx / maxVal) * height, txH = (tx / maxVal) * height;
                var rxRect = new Rectangle { Width = Math.Ceiling(barW), Height = rxH, Fill = new SolidColorBrush(Color.FromArgb(120, 39, 174, 96)) };
                Canvas.SetLeft(rxRect, i * barW); Canvas.SetTop(rxRect, height - rxH);
                NetChartCanvas.Children.Add(rxRect);
                var txRect = new Rectangle { Width = Math.Ceiling(barW), Height = txH, Fill = new SolidColorBrush(Color.FromArgb(180, 216, 67, 21)) };
                Canvas.SetLeft(txRect, i * barW); Canvas.SetTop(txRect, height - txH);
                NetChartCanvas.Children.Add(txRect);
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
            NetChartCanvas.Children.Clear(); ProcessGrid.ItemsSource = null; DiskGrid.ItemsSource = null;
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null) Clipboard.SetText(_currentSession.HostInfo.IpAddress);
        }

        private void MicaWindow_Closed(object sender, EventArgs e)
        {
            // 5秒后强制退出，防止 UnsubscribeMonitor 卡住残留进程
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
