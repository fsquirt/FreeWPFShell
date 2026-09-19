using System.Text;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using Renci.SshNet;

namespace FreeWPFShell.Tests.Services
{

    [TestClass]
    public class ConnectionFactoryTests
    {
        private static SshConnectionInfo BuildInfo()
        {
            return new SshConnectionInfo
            {
                Id = "host-1",
                HostName = "test",
                IpAddress = "10.0.0.1",
                SshPort = 22,
                SshUser = "root",
                AuthMethod = SshAuthMethod.Password,
                DecryptedSshSecret = "secret123"
            };
        }

        [TestMethod]
        public void BuildSshClient_DefaultPassword_UsesHostPortUser()
        {
            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(BuildInfo(), null, null);

            var info = client.ConnectionInfo;
            Assert.AreEqual("10.0.0.1", info.Host);
            Assert.AreEqual(22, info.Port);
            Assert.AreEqual("root", info.Username);
            Assert.AreEqual(Encoding.UTF8, info.Encoding, "应使用 UTF-8 编码");
        }

        [TestMethod]
        public void BuildSshClient_PasswordAuth_HasOneAuthMethod()
        {
            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(BuildInfo(), null, null);

            Assert.AreEqual(1, client.ConnectionInfo.AuthenticationMethods.Count);
            Assert.IsInstanceOfType(client.ConnectionInfo.AuthenticationMethods[0], typeof(PasswordAuthenticationMethod));
        }

        [TestMethod]
        public void BuildSshClient_PrivateKeyAuth_ThrowsWithoutKey()
        {
            var factory = new ConnectionFactory();
            var info = BuildInfo();
            info.AuthMethod = SshAuthMethod.PrivateKey;

            var ex = Assert.ThrowsException<Exception>(() => factory.BuildSshClient(info, null, null));
            Assert.IsTrue(ex.Message.Contains("密钥未预加载"), "应提示密钥未预加载");
        }

        [TestMethod]
        public void BuildSshClient_HttpProxy_UsesProxyHost()
        {
            var factory = new ConnectionFactory();
            var info = BuildInfo();
            info.UseProxy = true;
            info.Proxy = new ProxyInfo { Type = ProxyType.Http, ServerAddress = "proxy.example", Port = 8080 };

            using var client = factory.BuildSshClient(info, null, null);


            Assert.AreEqual("10.0.0.1", client.ConnectionInfo.Host);
        }

        [TestMethod]
        public void BuildSshClient_JumpPort_ConnectsViaLocalhost()
        {
            var factory = new ConnectionFactory();
            var info = BuildInfo();
            info.UseProxy = true;
            info.Proxy = new ProxyInfo { Type = ProxyType.Ssh, ServerAddress = "jump.example", Port = 22, Username = "jumpuser", Password = "jumppass" };


            var jumpPort = new ForwardedPortLocal("127.0.0.1", 50001, "10.0.0.1", 22);

            using var client = factory.BuildSshClient(info, null, jumpPort);


            Assert.AreEqual("127.0.0.1", client.ConnectionInfo.Host);
            Assert.AreEqual(50001, client.ConnectionInfo.Port);
        }

        [TestMethod]
        public void BuildJumpClient_NonSshProxy_Throws()
        {
            var factory = new ConnectionFactory();
            var info = BuildInfo();
            info.UseProxy = true;
            info.Proxy = new ProxyInfo { Type = ProxyType.Http, ServerAddress = "proxy", Port = 8080 };

            var ex = Assert.ThrowsException<Exception>(() => factory.BuildJumpClient(info, null));
            Assert.IsTrue(ex.Message.Contains("跳板机配置无效"), "非 SSH 代理不应能构建跳板机");
        }

        [TestMethod]
        public void BuildJumpClient_SshProxy_UsesJumpCredentials()
        {
            var factory = new ConnectionFactory();
            var info = BuildInfo();
            info.UseProxy = true;
            info.Proxy = new ProxyInfo { Type = ProxyType.Ssh, ServerAddress = "jump.example", Port = 22, Username = "jumpuser", Password = "jumppass" };

            using var client = factory.BuildJumpClient(info, null);

            Assert.AreEqual("jump.example", client.ConnectionInfo.Host);
            Assert.AreEqual(22, client.ConnectionInfo.Port);
            Assert.AreEqual("jumpuser", client.ConnectionInfo.Username);
            Assert.IsInstanceOfType(client.ConnectionInfo.AuthenticationMethods[0], typeof(PasswordAuthenticationMethod));
        }

        [TestMethod]
        public void BuildSftpClient_UsesSameConnectionInfo()
        {
            var factory = new ConnectionFactory();
            using var client = factory.BuildSftpClient(BuildInfo(), null, null);

            Assert.AreEqual("10.0.0.1", client.ConnectionInfo.Host);
            Assert.AreEqual(22, client.ConnectionInfo.Port);
            Assert.AreEqual("root", client.ConnectionInfo.Username);
        }

        [TestMethod]
        public void BuildSftpClient_SetsKeepAliveHeartbeat()
        {
            var factory = new ConnectionFactory();
            using var client = factory.BuildSftpClient(BuildInfo(), null, null);

            Assert.AreEqual(TimeSpan.FromSeconds(2), client.KeepAliveInterval,
                "SFTP 应每 2 秒发送心跳包，防止空闲断联");
        }
    }
}
