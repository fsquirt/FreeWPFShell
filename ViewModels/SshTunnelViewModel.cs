using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.Share;
using Renci.SshNet;

namespace FreeWPFShell.ViewModels
{

    public partial class SshTunnelViewModel : ObservableObject
    {
        private readonly SshTunnelManager _tunnelManager;


        public ObservableCollection<SshSessionService> ActiveSessions { get; } = new();

        [ObservableProperty]
        private SshSessionService? _selectedSession;

        [ObservableProperty]
        private int _tunnelTypeIndex;


        public bool IsLocal => TunnelTypeIndex == 0;

        public bool IsDynamic => TunnelTypeIndex == 2;

        public bool IsStaticTunnel => TunnelTypeIndex != 2;

        partial void OnTunnelTypeIndexChanged(int value)
        {
            OnPropertyChanged(nameof(IsLocal));
            OnPropertyChanged(nameof(IsDynamic));
            OnPropertyChanged(nameof(IsStaticTunnel));
        }

        [ObservableProperty]
        private string _bindPort = "8080";

        [ObservableProperty]
        private string _destAddr = "127.0.0.1";

        [ObservableProperty]
        private string _destPort = "80";

        [ObservableProperty]
        private string _remark = "手动创建";


        public ObservableCollection<SshTunnelInfo> ActiveTunnels => _tunnelManager.ActiveTunnels;


        public Action<string, MessageBoxImage>? ShowMessage { get; set; }

        public SshTunnelViewModel(SshTunnelManager? tunnelManager = null)
        {
            _tunnelManager = tunnelManager ?? SshTunnelManager.Instance;
        }

        [RelayCommand]
        private async Task AddTunnelAsync()
        {
            var session = SelectedSession;
            if (session == null)
            {
                ShowMessage?.Invoke("请选择一个活跃的主机连接。", MessageBoxImage.Warning);
                return;
            }
            var client = session.MasterClient;
            if (client == null || !client.IsConnected)
            {
                ShowMessage?.Invoke("所选主机未连接或已断开。", MessageBoxImage.Warning);
                return;
            }
            if (!uint.TryParse(BindPort, out uint bindPort) || bindPort == 0)
            {
                ShowMessage?.Invoke("请正确填写本机端口。", MessageBoxImage.Warning);
                return;
            }

            bool isDynamic = IsDynamic;
            uint destPort = 0;
            if (!isDynamic)
            {
                if (!uint.TryParse(DestPort, out destPort) || string.IsNullOrWhiteSpace(DestAddr))
                {
                    ShowMessage?.Invoke("请正确填写服务器地址和端口。", MessageBoxImage.Warning);
                    return;
                }
            }

            try
            {
                ForwardedPort port;
                if (isDynamic)
                {
                    var dynamicPort = new ForwardedPortDynamic("127.0.0.1", bindPort);
                    client.AddForwardedPort(dynamicPort);
                    await Task.Run(() => dynamicPort.Start());
                    port = dynamicPort;
                }
                else if (IsLocal)
                {
                    var local = new ForwardedPortLocal("127.0.0.1", bindPort, DestAddr, destPort);
                    client.AddForwardedPort(local);
                    await Task.Run(() => local.Start());
                    port = local;
                }
                else
                {
                    var remote = new ForwardedPortRemote(destPort, DestAddr, bindPort);
                    client.AddForwardedPort(remote);
                    await Task.Run(() => remote.Start());
                    port = remote;
                }

                session.RegisterTunnel(new SshTunnelInfo
                {
                    HostId = session.HostInfo.Id,
                    HostName = session.DisplayName,
                    PortConfig = port,
                    Type = isDynamic ? "动态(SOCKS5)" : (IsLocal ? "服务器->本机" : "本机->服务器"),
                    BindAddress = IsLocal || isDynamic ? "127.0.0.1" : "*",
                    BindPort = bindPort,
                    DestAddress = isDynamic ? string.Empty : DestAddr,
                    DestPort = isDynamic ? 0 : destPort,
                    Remark = Remark
                });
                ShowMessage?.Invoke(isDynamic
                    ? $"SOCKS5 代理已启动，请把浏览器/应用的代理设置为 127.0.0.1:{bindPort}"
                    : "隧道创建成功并在后台运行！", MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke($"创建隧道失败:\n{ex.Message}", MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task DeleteTunnelAsync(SshTunnelInfo? tunnel)
        {
            if (tunnel == null) return;
            try
            {
                var port = tunnel.PortConfig;
                if (port != null && port.IsStarted)
                    await Task.Run(() => port.Stop());
            }
            catch { }
            _tunnelManager.UnregisterTunnel(tunnel.Id);
        }
    }
}
