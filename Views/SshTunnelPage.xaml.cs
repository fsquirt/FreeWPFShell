using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.Share;
using FreeWPFShell.UserForm;
using Renci.SshNet;

namespace FreeWPFShell.Views
{
    public partial class SshTunnelPage : UserControl
    {
        public SshTunnelPage()
        {
            InitializeComponent();
            TunnelGrid.ItemsSource = SshTunnelManager.Instance.ActiveTunnels;
            if (Application.Current.MainWindow is MainForm mf)
            {
                CmbHosts.ItemsSource = mf.ActiveSessions;
                CmbHosts.DisplayMemberPath = "DisplayName";
                if (mf.ActiveSessions.Count > 0) CmbHosts.SelectedIndex = 0;
            }
        }

        private async void BtnAddTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (CmbHosts.SelectedItem == null) { UserForm.ModernMessageBox.Show("请选择一个活跃的主机连接。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var session = (SshSessionService)CmbHosts.SelectedItem;
            var client = session.MasterClient;
            if (client == null || !client.IsConnected) { UserForm.ModernMessageBox.Show("所选主机未连接或已断开。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (!uint.TryParse(TxtBindPort.Text, out uint bindPort) || !uint.TryParse(TxtDestPort.Text, out uint destPort) || string.IsNullOrWhiteSpace(TxtDestAddr.Text)) { UserForm.ModernMessageBox.Show("请正确填写端口号和地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            bool isLocal = CmbTunnelType.SelectedIndex == 0;
            try
            {
                ForwardedPort port;
                if (isLocal)
                {
                    // 服务器 -> 本机 (Local Port Forwarding)
                    // 在本机 127.0.0.1:bindPort 监听，转发到服务器能访问的 destAddr:destPort
                    port = new ForwardedPortLocal("127.0.0.1", bindPort, TxtDestAddr.Text, destPort);
                    client.AddForwardedPort(port);
                    await Task.Run(() => port.Start());
                }
                else
                {
                    // 本机 -> 服务器 (Remote Port Forwarding)
                    // 在服务器上监听 destPort，转发到本机能访问的 destAddr:bindPort
                    // 常见的需求是将本机的 bindPort 映射到服务器的 destPort
                    port = new ForwardedPortRemote(destPort, TxtDestAddr.Text, bindPort);
                    client.AddForwardedPort(port);
                    await Task.Run(() => port.Start());
                }

                session.RegisterTunnel(new SshTunnelInfo
                {
                    HostId = session.HostInfo.Id,
                    HostName = session.DisplayName,
                    PortConfig = port,
                    Type = isLocal ? "服务器->本机" : "本机->服务器",
                    BindAddress = isLocal ? "127.0.0.1" : "*",
                    BindPort = bindPort,
                    DestAddress = TxtDestAddr.Text,
                    DestPort = destPort,
                    Remark = TxtRemark.Text
                });
                UserForm.ModernMessageBox.Show("隧道创建成功并在后台运行！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { UserForm.ModernMessageBox.Show($"创建隧道失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void BtnDeleteTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var tunnel = SshTunnelManager.Instance.ActiveTunnels.FirstOrDefault(t => t.Id == id);
                if (tunnel != null) { try { if (tunnel.PortConfig != null && tunnel.PortConfig.IsStarted) tunnel.PortConfig.Stop(); } catch { } SshTunnelManager.Instance.UnregisterTunnel(id); }
            }
        }
    }
}
