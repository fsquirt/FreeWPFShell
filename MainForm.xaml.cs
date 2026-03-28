using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Collections.ObjectModel;
using System.Linq;
using FreeWPFShell.Share;
using MicaWPF.Core.Extensions;

namespace FreeWPFShell
{
    public partial class MainForm
    {
        public ObservableCollection<SshSessionInstance> ActiveSessions { get; } = new ObservableCollection<SshSessionInstance>();
        private SshSessionInstance? _currentSession;

        public MainForm()
        {
            InitializeComponent();

            // Apply saved backdrop type
            var sm = new SshManager.SshConnectionManager();
            ApplyBackdrop(sm.Settings.BackdropType);

            // Setup WelcomeTab
            var welcomePage = new Pages.WelcomePage();
            WelcomeTab.Tag = welcomePage;
            PagesContainer.Children.Add(welcomePage);
        }

        public async void OpenSession(SshManager.SshConnectionInfo hostInfo)
        {
            var session = new SshSessionInstance(hostInfo);
            ActiveSessions.Add(session);

            var terminalPage = new Pages.TerminalAndSFTP(session);

            string tabHeader = string.IsNullOrEmpty(hostInfo.HostName)
                ? hostInfo.IpAddress
                : hostInfo.HostName;

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = session.DisplayName, VerticalAlignment = VerticalAlignment.Center });

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

            btnClose.Click += (s, e) => CloseSessionTab(tabItem, session);
            headerPanel.Children.Add(btnClose);

            PagesContainer.Children.Add(terminalPage);
            SessionTabs.Items.Add(tabItem);
            SessionTabs.SelectedItem = tabItem;

            // Initialize the network concurrently in the background
            await session.ConnectAsync();
            terminalPage.BindSession();
        }

        private void CloseSessionTab(TabItem tabItem, SshSessionInstance session)
        {
            if (tabItem.Tag is Pages.TerminalAndSFTP terminalPage)
            {
                PagesContainer.Children.Remove(terminalPage);
            }

            UnsubscribeMonitor(session);
            ActiveSessions.Remove(session);

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    session.Dispose();
                }
                catch { }
            });

            if (_currentSession == session)
            {
                _currentSession = null!;
            }

            SessionTabs.Items.Remove(tabItem);
        }

        public void OpenSshTunnelManager()
        {
            var tunnelPage = new Pages.SshTunnelPage();

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "SSH隧道管理", VerticalAlignment = VerticalAlignment.Center });

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
                Tag = tunnelPage
            };

            btnClose.Click += (s, e) => {
                PagesContainer.Children.Remove(tunnelPage);
                SessionTabs.Items.Remove(tabItem);
            };

            headerPanel.Children.Add(btnClose);

            PagesContainer.Children.Add(tunnelPage);
            SessionTabs.Items.Add(tabItem);
            SessionTabs.SelectedItem = tabItem;
        }

        public void OpenTraceroutePage(string? initialTarget = null)
        {
            var traceroutePage = new Pages.TraceroutePage();

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock { Text = "路由追踪", VerticalAlignment = VerticalAlignment.Center });

            if (!string.IsNullOrEmpty(initialTarget))
                traceroutePage.TxtTarget.Text = initialTarget;

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
                Tag = traceroutePage
            };

            btnClose.Click += (s, e) => {
                PagesContainer.Children.Remove(traceroutePage);
                SessionTabs.Items.Remove(tabItem);
            };

            headerPanel.Children.Add(btnClose);

            PagesContainer.Children.Add(traceroutePage);
            SessionTabs.Items.Add(tabItem);
            SessionTabs.SelectedItem = tabItem;
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
                    SwitchMonitorToSession(terminalPage.Session);
                }
                else
                {
                    _currentSession = null!;
                    ResetSidebar();
                }
            }
        }

        private void SwitchMonitorToSession(SshSessionInstance session)
        {
            if (_currentSession != null && _currentSession == session)
                return;

            // Unsubscribe old session
            if (_currentSession != null)
                UnsubscribeMonitor(_currentSession);

            _currentSession = session;
            TxtHostIp.Text = $"IP {session.HostInfo.IpAddress}";
            var geo = IpGeoService.Instance.Query(session.HostInfo.IpAddress);
            TxtHostGeo.Text = geo.SimpleGeo;

            // Subscribe to session's monitor updates
            session.Monitor.PropertyChanged += OnMonitorPropertyChanged;
            session.MonitorUpdated += OnMonitorBulkUpdate;

            // Immediately display current data
            DisplayMonitorData(session.Monitor);
        }

        private void UnsubscribeMonitor(SshSessionInstance session)
        {
            session.Monitor.PropertyChanged -= OnMonitorPropertyChanged;
            session.MonitorUpdated -= OnMonitorBulkUpdate;
        }

        private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is MonitorData data && data == _currentSession?.Monitor)
            {
                if (e.PropertyName == nameof(MonitorData.NetHistory) || e.PropertyName == nameof(MonitorData.NetRxSpeed))
                {
                    Dispatcher.BeginInvoke(() => DrawNetChart(data));
                }
            }
        }

        private void OnMonitorBulkUpdate(object? sender, MonitorData data)
        {
            if (sender is SshSessionInstance s && s == _currentSession)
            {
                Dispatcher.BeginInvoke(() => DisplayMonitorData(data));
            }
        }

        private void DisplayMonitorData(MonitorData d)
        {
            ProgCpu.Value = d.CpuPct;
            TxtCpuPct.Text = d.CpuText;
            TxtCpuText.Text = d.CpuText;

            ProgMem.Value = d.MemPct;
            TxtMemPct.Text = $"{d.MemPct:F1}%";
            TxtMemText.Text = d.MemText;

            ProgSwap.Value = d.SwapPct;
            TxtSwapPct.Text = $"{d.SwapPct:F1}%";
            TxtSwapText.Text = d.SwapText;

            TxtUptime.Text = d.Uptime;
            TxtPing.Text = d.Ping;
            TxtLoad.Text = d.Load;

            TxtNetUp.Text = d.NetUp;
            TxtNetDown.Text = d.NetDown;
            TxtNetIface.Text = d.NetIface;
            TxtNetMax.Text = d.NetMax;
            TxtNetMid.Text = d.NetMid;

            ProcessGrid.ItemsSource = new ObservableCollection<ProcessItem>(d.Processes);
            DiskGrid.ItemsSource = new ObservableCollection<DiskItem>(d.Disks);

            DrawNetChart(d);
        }

        private void DrawNetChart(MonitorData d)
        {
            NetChartCanvas.Children.Clear();
            var history = d.NetHistory;
            if (history.Count == 0) return;

            double maxVal = history.Max(x => Math.Max(x.rx, x.tx));
            if (maxVal < 1024) maxVal = 1024;

            double width = NetChartCanvas.ActualWidth;
            double height = NetChartCanvas.ActualHeight;
            if (width == 0 || height == 0) return;

            double barWidth = width / 50.0;

            for (int i = 0; i < history.Count; i++)
            {
                var val = history[i];
                double rxH = (val.rx / maxVal) * height;
                double txH = (val.tx / maxVal) * height;

                var rectRx = new Rectangle
                {
                    Width = Math.Ceiling(barWidth),
                    Height = rxH,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 39, 174, 96))
                };
                Canvas.SetLeft(rectRx, i * barWidth);
                Canvas.SetTop(rectRx, height - rxH);
                NetChartCanvas.Children.Add(rectRx);

                var rectTx = new Rectangle
                {
                    Width = Math.Ceiling(barWidth),
                    Height = txH,
                    Fill = new SolidColorBrush(Color.FromArgb(180, 216, 67, 21))
                };
                Canvas.SetLeft(rectTx, i * barWidth);
                Canvas.SetTop(rectTx, height - txH);
                NetChartCanvas.Children.Add(rectTx);
            }
        }

        private static void ApplyBackdrop(string type)
        {
            try
            {
                var backdrop = type switch
                {
                    "Mica" => MicaWPF.Core.Enums.BackdropType.Mica,
                    "Acrylic" => MicaWPF.Core.Enums.BackdropType.Acrylic,
                    "Tabbed" => MicaWPF.Core.Enums.BackdropType.Tabbed,
                    _ => MicaWPF.Core.Enums.BackdropType.None
                };

                foreach (Window w in Application.Current.Windows)
                {
                    if (w is MicaWPF.Controls.MicaWindow mw)
                    {
                        w.EnableBackdrop(backdrop);
                    }
                }
            }
            catch { }
        }

        private void ResetSidebar()
        {
            TxtHostIp.Text = "未连接";
            TxtHostGeo.Text = "";
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
            ProcessGrid.ItemsSource = null;
            DiskGrid.ItemsSource = null;
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSession != null)
            {
                Clipboard.SetText(_currentSession.HostInfo.IpAddress);
            }
        }

        private void MicaWindow_Closed(object sender, EventArgs e)
        {
            foreach (var session in ActiveSessions)
            {
                try {
                    session.Dispose();
                } catch {}
            }
            ActiveSessions.Clear();
        }
    }
}
