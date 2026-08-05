using System.Text;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using Renci.SshNet;

namespace FreeWPFShell.Tests.Services
{
    /// <summary>
    /// ConnectionFactory 客户端构建逻辑测试。
    /// 验证不同认证方式、代理类型、跳板机端口下生成的连接参数（不实际连接服务器）。
    /// </summary>
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

            // 直接连接信息不变（代理走 SSH.NET 内部）
            Assert.AreEqual("10.0.0.1", client.ConnectionInfo.Host);
        }

        [TestMethod]
        public void BuildSshClient_JumpPort_ConnectsViaLocalhost()
        {
            var factory = new ConnectionFactory();
            var info = BuildInfo();
            info.UseProxy = true;
            info.Proxy = new ProxyInfo { Type = ProxyType.Ssh, ServerAddress = "jump.example", Port = 22, Username = "jumpuser", Password = "jumppass" };

            // 模拟已建立的跳板机转发端口
            var jumpPort = new ForwardedPortLocal("127.0.0.1", 50001, "10.0.0.1", 22);

            using var client = factory.BuildSshClient(info, null, jumpPort);

            // 跳板机模式下应通过本地转发端口连接
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
    }
}
