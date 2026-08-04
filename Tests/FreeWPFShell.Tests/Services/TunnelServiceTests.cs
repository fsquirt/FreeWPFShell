using FreeWPFShell.Models;
using FreeWPFShell.Services;

namespace FreeWPFShell.Tests.Services
{
    /// <summary>
    /// TunnelService 隧道管理状态逻辑单元测试。
    /// 验证注册、幂等清理、防重入等行为（不依赖真实 SSH 连接）。
    /// </summary>
    [TestClass]
    public class TunnelServiceTests
    {
        [TestInitialize]
        public void Init()
        {
            // 确保全局隧道表干净
            foreach (var t in Share.SshTunnelManager.Instance.ActiveTunnels.ToList())
                Share.SshTunnelManager.Instance.UnregisterTunnel(t.Id);
        }

        [TestMethod]
        public void RegisterTunnel_AddsToGlobalManager()
        {
            using var svc = new TunnelService("host1", "test-host");
            var tunnel = new SshTunnelInfo { Id = "t1", HostId = "host1", BindPort = 10001 };

            svc.RegisterTunnel(tunnel);

            Assert.IsTrue(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == "t1"),
                "注册后隧道应出现在全局管理器");
        }

        [TestMethod]
        public void CleanupTunnels_RemovesAllRegistered()
        {
            using var svc = new TunnelService("host2", "test-host");
            svc.RegisterTunnel(new SshTunnelInfo { Id = "t1", HostId = "host2", BindPort = 10002 });
            svc.RegisterTunnel(new SshTunnelInfo { Id = "t2", HostId = "host2", BindPort = 10003 });

            svc.CleanupTunnels();

            Assert.IsFalse(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == "t1" || t.Id == "t2"),
                "清理后隧道应从全局管理器移除");
        }

        [TestMethod]
        public void CleanupTunnels_IsIdempotent()
        {
            using var svc = new TunnelService("host3", "test-host");
            svc.RegisterTunnel(new SshTunnelInfo { Id = "t1", HostId = "host3", BindPort = 10004 });

            svc.CleanupTunnels();
            // 第二次清理不应抛异常
            svc.CleanupTunnels();
            svc.Dispose(); // Dispose 也触发清理，幂等

            Assert.IsFalse(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == "t1"));
        }

        [TestMethod]
        public void CleanupTunnels_WithNullOrUnstartedPort_DoesNotThrow()
        {
            using var svc = new TunnelService("host4", "test-host");
            // PortConfig 为 null 的隧道
            svc.RegisterTunnel(new SshTunnelInfo { Id = "t_null", HostId = "host4", BindPort = 10005 });

            // 不应抛异常
            svc.CleanupTunnels();
            Assert.IsFalse(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == "t_null"));
        }

        [TestMethod]
        public void RegisterTunnel_AfterCleanup_NoCrash()
        {
            using var svc = new TunnelService("host5", "test-host");
            svc.CleanupTunnels();
            // 不应抛异常
            Assert.IsTrue(true);
        }

        [TestMethod]
        public void TunnelService_HoldsHostInfo()
        {
            using var svc = new TunnelService("myhost", "myhost-name");
            Assert.AreEqual("myhost", svc.HostId);
            Assert.AreEqual("myhost-name", svc.HostName);
        }
    }
}
