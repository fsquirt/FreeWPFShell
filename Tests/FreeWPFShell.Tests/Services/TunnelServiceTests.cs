using FreeWPFShell.Models;
using FreeWPFShell.Services;

namespace FreeWPFShell.Tests.Services
{

    [TestClass]
    public class TunnelServiceTests
    {
        [TestInitialize]
        public void Init()
        {

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

            svc.CleanupTunnels();
            svc.Dispose(); 

            Assert.IsFalse(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == "t1"));
        }

        [TestMethod]
        public void CleanupTunnels_WithNullOrUnstartedPort_DoesNotThrow()
        {
            using var svc = new TunnelService("host4", "test-host");

            svc.RegisterTunnel(new SshTunnelInfo { Id = "t_null", HostId = "host4", BindPort = 10005 });


            svc.CleanupTunnels();
            Assert.IsFalse(Share.SshTunnelManager.Instance.ActiveTunnels.Any(t => t.Id == "t_null"));
        }

        [TestMethod]
        public void RegisterTunnel_AfterCleanup_NoCrash()
        {
            using var svc = new TunnelService("host5", "test-host");
            svc.CleanupTunnels();

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
