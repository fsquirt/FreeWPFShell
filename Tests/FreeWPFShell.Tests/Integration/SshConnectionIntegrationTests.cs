using System.IO;
using System.Text;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FreeWPFShell.Tests.Integration
{
    /// <summary>
    /// 真实 SSH 连接集成测试。从 sshtest.json 读取服务器配置，
    /// 配置缺失或无效时自动跳过（Assert.Inconclusive）。
    /// </summary>
    [TestClass]
    public class SshConnectionIntegrationTests
    {
        private static SshTestConfig? Config;
        private static bool _loaded;

        private static SshTestConfig? GetConfig()
        {
            if (!_loaded)
            {
                Config = SshTestConfig.Load();
                _loaded = true;
            }
            return Config;
        }

        private static SshConnectionInfo BuildHostInfo(SshTestConfig cfg)
        {
            return new SshConnectionInfo
            {
                IpAddress = cfg.Host,
                SshPort = cfg.Port,
                SshUser = cfg.User,
                AuthMethod = SshAuthMethod.Password,
                DecryptedSshSecret = cfg.Password,
            };
        }

        // ── 连接与命令 ─────────────────────────────────────────────

        [TestMethod]
        public void Connect_AndExecuteCommand()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(BuildHostInfo(cfg), null, null);
            client.Connect();

            try
            {
                Assert.IsTrue(client.IsConnected, "客户端应已连接");

                var result = client.CreateCommand("echo SSHTEST_OK && uname -s && whoami").Execute();
                Assert.IsTrue(result.Contains("SSHTEST_OK"));
                Assert.IsTrue(result.Contains("Linux"));
                Assert.IsTrue(result.TrimEnd().EndsWith("root") || result.Contains(cfg.User));
            }
            finally { client.Disconnect(); }
        }

        [TestMethod]
        public void Connect_WithWrongPassword_ThrowsAuthException()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            var info = BuildHostInfo(cfg);
            info.DecryptedSshSecret = "definitely-wrong-password";

            var factory = new ConnectionFactory();
            using var client = factory.BuildSshClient(info, null, null);
            bool threw = false;
            try { client.Connect(); }
            catch (Renci.SshNet.Common.SshAuthenticationException) { threw = true; }

            Assert.IsTrue(threw, "错误密码应导致认证失败异常");
        }

        // ── SFTP 功能 ──────────────────────────────────────────────

        [TestMethod]
        public void Sftp_ListHomeDirectory()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            using var sftp = new SftpClient(cfg.Host, cfg.Port, cfg.User, cfg.Password);
            sftp.Connect();

            try
            {
                Assert.IsTrue(sftp.IsConnected);
                var entries = sftp.ListDirectory("/").ToList();
                Assert.IsTrue(entries.Count > 0, "根目录应至少有一个条目");
                Assert.IsTrue(entries.Any(e => e.Name == "root" || e.IsDirectory), "应包含目录条目");
            }
            finally { sftp.Disconnect(); }
        }

        [TestMethod]
        public void Sftp_UploadAndDownload_RoundTrip()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            string remotePath = $"/tmp/fwpt_roundtrip_{Guid.NewGuid():N}.txt";
            string content = "FWPT_SFTP_ROUNDTRIP_" + Guid.NewGuid();

            using var sftp = new SftpClient(cfg.Host, cfg.Port, cfg.User, cfg.Password);
            sftp.Connect();

            try
            {
                // 上传
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                    sftp.UploadFile(ms, remotePath, true);
                Assert.IsTrue(sftp.Exists(remotePath), "上传后远程文件应存在");

                // 下载并校验内容
                using var download = new MemoryStream();
                sftp.DownloadFile(remotePath, download);
                string downloaded = Encoding.UTF8.GetString(download.ToArray());
                Assert.AreEqual(content, downloaded, "下载内容应与上传一致");

                // 清理
                sftp.DeleteFile(remotePath);
                Assert.IsFalse(sftp.Exists(remotePath), "删除后远程文件应不存在");
            }
            finally { sftp.Disconnect(); }
        }

        [TestMethod]
        public void Sftp_RemoteCommandWorks_WithCpFallback()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            // 验证服务器端命令执行（对应粘贴时 cp -a 的场景）
            using var ssh = new SshClient(cfg.Host, cfg.Port, cfg.User, cfg.Password);
            ssh.Connect();

            string src = $"/tmp/fwpt_src_{Guid.NewGuid():N}.txt";
            string dst = $"/tmp/fwpt_dst_{Guid.NewGuid():N}.txt";

            try
            {
                ssh.CreateCommand($"echo hi > {src}").Execute();
                var cpResult = ssh.CreateCommand($"cp -a \"{src}\" \"{dst}\" && echo CP_OK").Execute();
                Assert.IsTrue(cpResult.Contains("CP_OK"), "服务器端 cp -a 应成功");
            }
            finally
            {
                ssh.CreateCommand($"rm -f {src} {dst}").Execute();
                ssh.Disconnect();
            }
        }

        // ── 终端 ShellStream ───────────────────────────────────────

        [TestMethod]
        public void Terminal_ShellStream_WritesAndReads()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            using var ssh = new SshClient(cfg.Host, cfg.Port, cfg.User, cfg.Password);
            ssh.Connect();

            using var shell = ssh.CreateShellStream(
                "xterm-256color", 120, 30, 960, 480, 65536);

            try
            {
                shell.WriteLine("echo FWPT_TERMINAL_OK");
                shell.WriteLine("exit");

                var sb = new StringBuilder();
                shell.DataReceived += (s, e) => sb.Append(Encoding.UTF8.GetString(e.Data));
                // 等待输出
                Thread.Sleep(2000);

                string output = sb.ToString();
                Assert.IsTrue(output.Contains("FWPT_TERMINAL_OK"), $"终端应回显测试标记，实际输出: {output}");
            }
            finally
            {
                ssh.Disconnect();
            }
        }

        // ── 监控（Linux 探测相关） ─────────────────────────────────

        [TestMethod]
        public void Monitor_CollectsSystemStats_FromRealServer()
        {
            var cfg = GetConfig();
            if (cfg == null) { Assert.Inconclusive("未配置 sshtest.json，跳过集成测试。"); return; }

            using var ssh = new SshClient(cfg.Host, cfg.Port, cfg.User, cfg.Password);
            ssh.Connect();

            // 用真实命令组合拉取 /proc/stat 和 /proc/net/dev，验证 SshMonitorService 的解析依赖的命令可用
            var cmd = ssh.CreateCommand("echo \"==STAT==\"; head -n 1 /proc/stat; echo \"==TOP==\"; top -b -n 1 | head -n 5; echo \"==PROC==\"; ps axo %mem,%cpu,command --sort=-%cpu | head -n 5; echo \"==NET==\"; cat /proc/net/dev");
            var result = cmd.Execute();

            Assert.IsTrue(result.Contains("==STAT=="));
            Assert.IsTrue(result.Contains("cpu"), "应能读取 /proc/stat");
            Assert.IsTrue(result.Contains("==NET=="));
            Assert.IsTrue(result.Contains("lo:"), "/proc/net/dev 应包含 lo 接口");
            ssh.Disconnect();
        }
    }
}
