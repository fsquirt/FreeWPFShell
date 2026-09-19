using System.Collections.ObjectModel;
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

        partial void OnTunnelTypeIndexChanged(int value) => OnPropertyChanged(nameof(IsLocal));

        [ObservableProperty]
        private string _bindPort = "8080";

        [ObservableProperty]
        private string _destAddr = "127.0.0.1";

        [ObservableProperty]
        private string _destPort = "80";

        [ObservableProperty]
        private string _remark = "手动创建";


        public ObservableCollection<SshTunnelInfo> ActiveTunnels => _tunnelManager.ActiveTunnels;


        public Action<string>? ShowMessage { get; set; }

        public SshTunnelViewModel(SshTunnelManager? tunnelManager = null)
        {
            _tunnelManager = tunnelManager ?? SshTunnelManager.Instance;
        }

        [RelayCommand]
        private void AddTunnel()
        {
            var session = SelectedSession;
            if (session == null)
            {
                ShowMessage?.Invoke("请选择一个活跃的主机连接。");
                return;
            }
            var client = session.MasterClient;
            if (client == null || !client.IsConnected)
            {
                ShowMessage?.Invoke("所选主机未连接或已断开。");
                return;
            }
            if (!uint.TryParse(BindPort, out uint bindPort) ||
                !uint.TryParse(DestPort, out uint destPort) ||
                string.IsNullOrWhiteSpace(DestAddr))
            {
                ShowMessage?.Invoke("请正确填写端口号和地址。");
                return;
            }

            try
            {
                ForwardedPort port;
                if (IsLocal)
                {

                    port = new ForwardedPortLocal("127.0.0.1", bindPort, DestAddr, destPort);
                    client.AddForwardedPort(port);
                    port.Start();
                }
                else
                {

                    port = new ForwardedPortRemote(destPort, DestAddr, bindPort);
                    client.AddForwardedPort(port);
                    port.Start();
                }

                session.RegisterTunnel(new SshTunnelInfo
                {
                    HostId = session.HostInfo.Id,
                    HostName = session.DisplayName,
                    PortConfig = port,
                    Type = IsLocal ? "服务器->本机" : "本机->服务器",
                    BindAddress = IsLocal ? "127.0.0.1" : "*",
                    BindPort = bindPort,
                    DestAddress = DestAddr,
                    DestPort = destPort,
                    Remark = Remark
                });
                ShowMessage?.Invoke("隧道创建成功并在后台运行！");
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke($"创建隧道失败:\n{ex.Message}");
            }
        }

        [RelayCommand]
        private void DeleteTunnel(SshTunnelInfo? tunnel)
        {
            if (tunnel == null) return;
            try
            {
                if (tunnel.PortConfig != null && tunnel.PortConfig.IsStarted)
                    tunnel.PortConfig.Stop();
            }
            catch { }
            _tunnelManager.UnregisterTunnel(tunnel.Id);
        }
    }
}
