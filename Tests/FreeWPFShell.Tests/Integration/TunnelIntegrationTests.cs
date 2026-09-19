using System.Net.Sockets;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using Renci.SshNet;

namespace FreeWPFShell.Tests.Integration
{

    [TestClass]
    public class TunnelIntegrationTests
    {
        private readonly SshTestConfig? _cfg;

        public TunnelIntegrationTests()
        {
            _cfg = SshTestConfig.Load();
        }

        private void SkipIfNoConfig()
        {
            if (_cfg == null)
                Assert.Inconclusive("未配置 sshtest.json，跳过隧道集成测试。");
        }

        [TestMethod]
        public void LocalForward_ForwardsToSshPort()
        {
            SkipIfNoConfig();
            using var ssh = new SshClient(_cfg!.Host, _cfg.Port, _cfg.User, _cfg.Password);
            ssh.Connect();

            int localPort = new Random().Next(30000, 50000);
            var port = new ForwardedPortLocal("127.0.0.1", (uint)localPort, "127.0.0.1", (uint)_cfg.Port);
            ssh.AddForwardedPort(port);
            port.Start();

            try
            {
                Assert.IsTrue(port.IsStarted, "本地转发端口应已启动");


                using var client = new TcpClient();
                client.Connect("127.0.0.1", localPort);
                Assert.IsTrue(client.Connected, "应能通过隧道连接本地转发端口");
            }
            finally
            {
                port.Stop();
                ssh.Disconnect();
            }
        }

        [TestMethod]
        public void TunnelService_RegistersAndCleanup_OnRealConnection()
        {
            SkipIfNoConfig();
            using var ssh = new SshClient(_cfg!.Host, _cfg.Port, _cfg.User, _cfg.Password);
            ssh.Connect();


            using var tunnelSvc = new TunnelService(_cfg.Host, _cfg.User);
            int localPort = new Random().Next(30000, 50000);
            var port = new ForwardedPortLocal("127.0.0.1", (uint)localPort, "127.0.0.1", (uint)_cfg.Port);
            ssh.AddForwardedPort(port);
            port.Start();

            var info = new SshTunnelInfo
            {
                Id = $"itest_{Guid.NewGuid():N}",
                HostId = _cfg.Host,
                HostName = _cfg.User,
                BindPort = (uint)localPort,
                DestPort = (uint)_cfg.Port,
                PortConfig = port,
                Type = "本地(测试)"
            };
            tunnelSvc.RegisterTunnel(info);

            Assert.IsTrue(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == info.Id),
                "隧道应注册到全局管理器");

            tunnelSvc.CleanupTunnels();
            Assert.IsFalse(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == info.Id),
                "清理后隧道应从全局管理器移除");
            Assert.IsFalse(port.IsStarted, "清理后转发端口应停止");

            ssh.Disconnect();
        }
    }
}
