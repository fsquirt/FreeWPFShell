using System;
using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using YouShell.Models;
using YouShell.Services;
using YouShell.Share;
using YouShell.Terminal;
using YouShell.UserForm;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// 终端页。原生终端 + 上排监控文字 + 下排按钮；SFTP 已拆分为独立 <see cref="SftpPage"/>。
    /// </summary>
    public sealed partial class TerminalAndSFTP : UserControl
    {
        public TerminalViewModel ViewModel { get; }
        public SshSessionService? Session { get; private set; }

        private PropertyChangedEventHandler? _sessionPropertyChangedHandler;
        private EventHandler<MonitorData>? _monitorHandler;

        // 由 MainWindow 注入的导航回调
        public Action<SshSessionService, TerminalViewModel>? OpenSftpRequested { get; set; }
        public Action? OpenSshTunnelRequested { get; set; }
        public Action<string?>? OpenTracerouteRequested { get; set; }
        public Action<SshSessionService>? OpenSystemManagementRequested { get; set; }
        public Action<TerminalAndSFTP>? CloseTabRequested { get; set; }

        public TerminalAndSFTP(SshSessionService session)
        {
            InitializeComponent();
            Session = session;
            ViewModel = new TerminalViewModel(session);
            DataContext = ViewModel;

            ViewModel.TransferStateChanged += UpdateStatusIcon;

            _sessionPropertyChangedHandler = (s, e) =>
            {
                var sess = Session;
                if (sess == null) return;
                YouShell.Core.UiDispatcher.Run(() =>
                {
                    if (e.PropertyName == nameof(SshSessionService.ConnectionStatus))
                        TxtConnStatus.Text = sess.ConnectionStatus;
                    else if (e.PropertyName == nameof(SshSessionService.IsAppCursorMode))
                        TxtCursorMode.Text = sess.IsAppCursorMode ? "APP MODE" : "NORMAL MODE";
                });
            };
            session.PropertyChanged += _sessionPropertyChangedHandler;

            // 监控：订阅批量刷新并初始化 IP/地理信息
            _monitorHandler = (s, d) => YouShell.Core.UiDispatcher.Enqueue(() => UpdateMonitorTopRow(d));
            session.MonitorUpdated += _monitorHandler;

            TxtHostIp.Text = $"IP {session.HostInfo.IpAddress}";
            try
            {
                var geo = IpGeoService.Instance.Query(session.HostInfo.IpAddress).SimpleGeo;
                if (!string.IsNullOrEmpty(geo)) TxtHostIp.Text += $" ({geo})";
            }
            catch { }
        }

        /// <summary>关 Tab 时必须调用，断开引用链并释放资源。</summary>
        public void Cleanup()
        {
            if (Session != null && _sessionPropertyChangedHandler != null)
            {
                Session.PropertyChanged -= _sessionPropertyChangedHandler;
                _sessionPropertyChangedHandler = null;
            }
            if (Session != null && _monitorHandler != null)
            {
                Session.MonitorUpdated -= _monitorHandler;
                _monitorHandler = null;
            }
            if (Session?.TerminalConnection != null)
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;

            ViewModel.TransferStateChanged -= UpdateStatusIcon;

            Terminal.Connection = null;
            Terminal.DisposeHost();
            ViewModel.CancelAllTransfersCommand.Execute(null);
            ViewModel.Files.Clear();
            Session = null;
        }

        // ── 终端 ─────────────────────────────────────────────────

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            var settings = new Repositories.SettingsRepository().Load();
            uint bg = ParseHexColor(settings.TerminalBackground);

            if (settings.UseImageBackground && !string.IsNullOrEmpty(settings.ImageBackgroundPath))
            {
                Terminal.PixelShaderImagePath = settings.ImageBackgroundPath;
                Terminal.PixelShaderPath = Path.Combine(AppContext.BaseDirectory, "background_blur.hlsl");
                Terminal.PixelShaderImageStretchMode = (YouShell.Terminal.PixelShaderImageStretchMode)settings.ImageStretchMode;
            }
            else
            {
                Terminal.ClearPixelShaderBackground();
            }

            Terminal.SetTheme(new TerminalTheme
            {
                DefaultBackground = bg,
                DefaultForeground = 0x00ffffff,
                DefaultSelectionBackground = 0x00ffffff,
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = new uint[16]
                {
                    0x000c0c0c, 0x001f0fc5, 0x000ea113, 0x00009cc1, 0x00da3700, 0x00981788, 0x00dd963a, 0x00cccccc,
                    0x00767676, 0x005648e7, 0x000cc616, 0x00a5f1f9, 0x00ff783b, 0x009e00b4, 0x00d6d661, 0x00f2f2f2
                }
            }, settings.TerminalFont ?? "Cascadia Code", (short)(settings.TerminalFontSize > 0 ? settings.TerminalFontSize : 10));
        }

        private static uint ParseHexColor(string? s)
        {
            s = s?.Trim().TrimStart('#') ?? "";
            if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return v; // 0xRRGGBB
            return 0x1E3047;
        }

        public void BindSession()
        {
            if (Session?.IsConnected != true) return;

            Terminal.Connection = Session.TerminalConnection;

            if (Session.TerminalConnection != null)
            {
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;
                Session.TerminalConnection.ConnectionLost += TerminalConnection_ConnectionLost;
            }

            Terminal.FocusTerminal();
        }

        private void BtnTermCopy_Click(object sender, RoutedEventArgs e)
        {
            string selectedText = Terminal.GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                var dp = new DataPackage();
                dp.SetText(selectedText);
                Clipboard.SetContent(dp);
            }
        }

        private void TerminalConnection_ConnectionLost(object? sender, EventArgs e)
        {
            Session?.TerminalConnection?.ConnectionLost -= TerminalConnection_ConnectionLost;
            HandleConnectionLost();
        }

        private async void HandleConnectionLost()
        {
            await ModernMessageBox.ShowAsync("与服务器的连接已断开，将关闭当前终端窗口。", "连接已断开", MessageBoxButton.OKCancel, MessageBoxImage.Error);
            CloseTabRequested?.Invoke(this);
        }

        // ── 状态图标（SFTP 传输，与 SftpPage 共享 ViewModel） ──

        private void UpdateStatusIcon()
        {
            YouShell.Core.UiDispatcher.Run(() =>
            {
                if (ViewModel.IsTransferring)
                {
                    TxtStatusIcon.Glyph = "";
                    TxtStatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold);
                }
                else
                {
                    TxtStatusIcon.Glyph = ""; // CheckMark
                    TxtStatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                }
                ToolTipService.SetToolTip(TxtStatusIcon, ViewModel.StatusText);
            });
        }

        private async void StatusIcon_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ViewModel.IsTransferring)
            {
                var r = await ModernMessageBox.ShowAsync("确定要中断当前所有的传输任务吗？", "中断传输", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) ViewModel.CancelAllTransfersCommand.Execute(null);
            }
        }

        // ── 监控文字 ─────────────────────────────────────────────

        private void UpdateMonitorTopRow(MonitorData d)
        {
            TxtUptime.Text = d.Uptime;
            TxtPing.Text = d.Ping;
            TxtCpu.Text = $"CPU {d.CpuText}";
            TxtMem.Text = $"内存 {d.MemPct:F1}%";
            TxtSwap.Text = $"交换 {d.SwapPct:F1}%";
            TxtNetIface.Text = $"网卡 {d.NetIface}";
            TxtNetUp.Text = $"↑ {d.NetUp}";
            TxtNetDown.Text = $"↓ {d.NetDown}";
        }

        // ── 下排按钮 ─────────────────────────────────────────────

        private void BtnSftpManager_Click(object sender, RoutedEventArgs e)
        {
            if (Session != null) OpenSftpRequested?.Invoke(Session, ViewModel);
        }

        private void BtnSshTunnel_Click(object sender, RoutedEventArgs e) => OpenSshTunnelRequested?.Invoke();
        private void BtnTraceroute_Click(object sender, RoutedEventArgs e) => OpenTracerouteRequested?.Invoke(Session?.HostInfo?.IpAddress);
        private void BtnSysManagement_Click(object sender, RoutedEventArgs e)
        {
            if (Session != null) OpenSystemManagementRequested?.Invoke(Session);
        }
    }
}
