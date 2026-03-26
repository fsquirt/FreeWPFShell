using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Share;
using Renci.SshNet;

namespace FreeWPFShell.Pages
{
    public partial class SshTunnelPage : UserControl
    {
        public SshTunnelPage()
        {
            InitializeComponent();
            TunnelGrid.ItemsSource = SshTunnelManager.Instance.ActiveTunnels;
            
            // Bind to the live ObservableCollection so new sessions appear automatically
            if (Application.Current.MainWindow is MainForm mainForm)
            {
                CmbHosts.ItemsSource = mainForm.ActiveSessions;
                CmbHosts.DisplayMemberPath = "DisplayName";
                if (mainForm.ActiveSessions.Count > 0)
                {
                    CmbHosts.SelectedIndex = 0;
                }
            }
        }

        private void BtnAddTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (CmbHosts.SelectedItem == null)
            {
                MessageBox.Show("请选择一个活跃的主机连接。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var session = (SshSessionInstance)CmbHosts.SelectedItem;
            var client = session.MasterClient;

            if (client == null || !client.IsConnected)
            {
                MessageBox.Show("所选主机未连接或已断开。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!uint.TryParse(TxtBindPort.Text, out uint bindPort) || 
                !uint.TryParse(TxtDestPort.Text, out uint destPort) ||
                string.IsNullOrWhiteSpace(TxtDestAddr.Text))
            {
                MessageBox.Show("请正确填写端口号和地址 (1-65535)。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool isLocal = CmbTunnelType.SelectedIndex == 0;

            try
            {
                ForwardedPort port;
                if (isLocal)
                {
                    port = new ForwardedPortLocal("127.0.0.1", bindPort, TxtDestAddr.Text, destPort);
                    client.AddForwardedPort(port);
                    port.Start();
                }
                else
                {
                    port = new ForwardedPortRemote(bindPort, TxtDestAddr.Text, destPort);
                    client.AddForwardedPort(port);
                    port.Start();
                }

                var info = new SshTunnelInfo
                {
                    HostId = session.HostInfo.Id,
                    HostName = session.DisplayName,
                    PortConfig = port,
                    Type = isLocal ? "本地转发" : "远程转发",
                    BindAddress = isLocal ? "127.0.0.1" : "*",
                    BindPort = bindPort,
                    DestAddress = TxtDestAddr.Text,
                    DestPort = destPort,
                    Remark = TxtRemark.Text
                };

                SshTunnelManager.Instance.RegisterTunnel(info);
                MessageBox.Show("隧道创建成功并在后台运行！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"创建隧道失败:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDeleteTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var tunnel = SshTunnelManager.Instance.ActiveTunnels.FirstOrDefault(t => t.Id == id);
                if (tunnel != null)
                {
                    try
                    {
                        if (tunnel.PortConfig != null && tunnel.PortConfig.IsStarted)
                        {
                            tunnel.PortConfig.Stop();
                        }
                    }
                    catch { }
                    SshTunnelManager.Instance.UnregisterTunnel(id);
                }
            }
        }
    }
}
