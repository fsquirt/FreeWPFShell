using System.IO;
using System.Text.Json;

namespace FreeWPFShell.Tests.Integration
{
    /// <summary>
    /// 集成测试服务器配置。从 sshtest.json 读取（该文件已加入 .gitignore，不入库）。
    /// 若文件不存在或配置不完整，则跳过集成测试。
    /// </summary>
    public class SshTestConfig
    {
        /// <summary>主测试服务器（直连 / SFTP / 隧道）。</summary>
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 22;
        public string User { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        /// <summary>代理服务器（HTTP/SOCKS5，本地或远端）。</summary>
        public string ProxyHost { get; set; } = string.Empty;
        public int ProxyPort { get; set; } = 0;
        public string ProxyUser { get; set; } = string.Empty;
        public string ProxyPassword { get; set; } = string.Empty;

        /// <summary>远端目标服务器（通过代理/跳板访问）。</summary>
        public string RemoteHost { get; set; } = string.Empty;
        public int RemotePort { get; set; } = 22;
        public string RemoteUser { get; set; } = string.Empty;
        public string RemotePassword { get; set; } = string.Empty;

        /// <summary>SSH 跳板机。</summary>
        public string JumpHost { get; set; } = string.Empty;
        public int JumpPort { get; set; } = 22;
        public string JumpUser { get; set; } = string.Empty;
        public string JumpPassword { get; set; } = string.Empty;

        /// <summary>配置文件相对于测试输出目录的路径（拷贝到输出目录）。</summary>
        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "sshtest.json");

        public bool IsValid =>
            !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Password);

        public bool HasHttpProxy => !string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0;
        public bool HasSocksProxy => !string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0;

        /// <summary>是否配置了"远端目标服务器"（通过代理/跳板访问）。</summary>
        public bool HasRemoteTarget =>
            HasHttpProxy && !string.IsNullOrEmpty(RemoteHost) &&
            !string.IsNullOrEmpty(RemoteUser) && !string.IsNullOrEmpty(RemotePassword);

        public bool HasJumpHost => !string.IsNullOrEmpty(JumpHost) && !string.IsNullOrEmpty(JumpUser);

        public static SshTestConfig? Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<SshTestConfig>(json);
                return cfg?.IsValid == true ? cfg : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
