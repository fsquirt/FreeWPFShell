using FreeWPFShell.Models;
using FreeWPFShell.Services;
using Renci.SshNet;

namespace FreeWPFShell.Tests.Integration
{

    [TestClass]
    public class ProxyAndJumpIntegrationTests
    {
        private static SshTestConfig? _cfg;
        private static bool _loaded;

        private static SshTestConfig? GetConfig()
        {
            if (!_loaded) { _cfg = SshTestConfig.Load(); _loaded = true; }
            return _cfg;
        }


        private static SshConnectionInfo BuildRemoteInfo(SshTestConfig cfg)
        {
            return new SshConnectionInfo
            {
                IpAddress = cfg.RemoteHost,
                SshPort = cfg.RemotePort,
                SshUser = cfg.RemoteUser,
                AuthMethod = SshAuthMethod.Password,
                DecryptedSshSecret = cfg.RemotePassword,
            };
        }

        private static void AssertConnected(SshClient client)
        {
            Assert.IsTrue(client.IsConnected, "应成功连接服务器");
            var result = client.CreateCommand("echo PROXY_OK && hostname && whoami").Execute();
            Assert.IsTrue(result.Contains("PROXY_OK"), "应能执行命令");
        }

        [TestMethod]
        public void Connect_ThroughHttpProxy()
        {
            var cfg = GetConfig();
            if (cfg == null || !cfg.HasRemoteTarget)
            {
                Assert.Inconclusive("未配置代理及远端目标服务器，跳过。");
                return;
            }

            var info = BuildRemoteInfo(cfg);
            info.UseProxy = true;
            info.Proxy = new ProxyInfo
            {
                Type = ProxyType.Http,
                ServerAddress = cfg.ProxyHost,
                Port = cfg.ProxyPort,
                Username = cfg.ProxyUser,
                Password = cfg.ProxyPassword,
            };

            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(info, null, null);
            client.Connect();
            AssertConnected(client);
        }

        [TestMethod]
        public void Connect_ThroughSocks5Proxy()
        {
            var cfg = GetConfig();
            if (cfg == null || !cfg.HasRemoteTarget)
            {
                Assert.Inconclusive("未配置 SOCKS5 代理及远端目标服务器，跳过。");
                return;
            }

            var info = BuildRemoteInfo(cfg);
            info.UseProxy = true;
            info.Proxy = new ProxyInfo
            {
                Type = ProxyType.Socks5,
                ServerAddress = cfg.ProxyHost,
                Port = cfg.ProxyPort,
                Username = cfg.ProxyUser,
                Password = cfg.ProxyPassword,
            };

            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(info, null, null);
            client.Connect();
            AssertConnected(client);
        }

        [TestMethod]
        public void Connect_ThroughSocks4Proxy()
        {
            var cfg = GetConfig();
            if (cfg == null || !cfg.HasRemoteTarget)
            {
                Assert.Inconclusive("未配置 SOCKS 代理及远端目标服务器，跳过。");
                return;
            }

            var info = BuildRemoteInfo(cfg);
            info.UseProxy = true;
            info.Proxy = new ProxyInfo
            {
                Type = ProxyType.Socks4,
                ServerAddress = cfg.ProxyHost,
                Port = cfg.ProxyPort,
                Username = cfg.ProxyUser,
                Password = cfg.ProxyPassword,
            };

            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(info, null, null);
            client.Connect();
            AssertConnected(client);
        }

        [TestMethod]
        public void Connect_ThroughProxy_WithWrongPassword_Fails()
        {
            var cfg = GetConfig();
            if (cfg == null || !cfg.HasRemoteTarget)
            {
                Assert.Inconclusive("未配置代理及远端目标服务器，跳过。");
                return;
            }

            var info = BuildRemoteInfo(cfg);
            info.DecryptedSshSecret = "wrong-password";
            info.UseProxy = true;
            info.Proxy = new ProxyInfo
            {
                Type = ProxyType.Http,
                ServerAddress = cfg.ProxyHost,
                Port = cfg.ProxyPort,
            };

            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(info, null, null);
            bool threw = false;
            try { client.Connect(); }
            catch (Renci.SshNet.Common.SshAuthenticationException) { threw = true; }

            Assert.IsTrue(threw, "通过代理时错误密码应导致认证失败");
        }

        [TestMethod]
        public void Connect_ThroughSshJump()
        {
            var cfg = GetConfig();
            if (cfg == null || !cfg.HasJumpHost || !cfg.HasRemoteTarget)
            {
                Assert.Inconclusive("未配置 SSH 跳板机或远端目标服务器，跳过。");
                return;
            }


            using var jumpClient = new SshClient(cfg.JumpHost, cfg.JumpPort, cfg.JumpUser, cfg.JumpPassword);
            jumpClient.Connect();
            Assert.IsTrue(jumpClient.IsConnected, "跳板机应连接成功");


            int localPort = new Random().Next(40000, 60000);
            var jumpPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, cfg.RemoteHost, (uint)cfg.RemotePort);
            jumpClient.AddForwardedPort(jumpPort);
            jumpPort.Start();


            var info = BuildRemoteInfo(cfg);
            info.UseProxy = true;
            info.Proxy = new ProxyInfo
            {
                Type = ProxyType.Ssh,
                ServerAddress = cfg.JumpHost,
                Port = cfg.JumpPort,
                Username = cfg.JumpUser,
                Password = cfg.JumpPassword,
            };

            var factory = new ConnectionFactory();
            using var target = factory.BuildSshClient(info, null, jumpPort);
            target.Connect();
            AssertConnected(target);
        }
    }
}
