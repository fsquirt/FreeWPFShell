using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class SshConnectionInfo
    {
        public string Id { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int SshPort { get; set; } = 22;
        public string SshUser { get; set; } = string.Empty;
        public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
        public bool UseProxy { get; set; } = false;
        public ProxyInfo? Proxy { get; set; }
        public bool UseVault { get; set; } = false;
        public string? ProtectedSecret { get; set; }
        /// <summary>密钥登录时引用的 Key ID（来自 KeyRepository）</summary>
        public string? SshKeyId { get; set; }

        [JsonIgnore] public string? DecryptedSshSecret { get; set; }
        [JsonIgnore] public string? SimpleIpGEO { get; set; }
    }

    public enum SshAuthMethod { Password, PrivateKey }

    public class ProxyInfo
    {
        public ProxyType Type { get; set; } = ProxyType.None;
        public string ServerAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        /// <summary>SSH 隧道代理（跳板机）使用密钥登录时引用的 Key ID</summary>
        public string? SshKeyId { get; set; }
    }

    public enum ProxyType { None, Http, Socks4, Socks5, Ssh }

    public enum PixelShaderImageStretchMode
    {
        None = 0,      // 原始尺寸
        Fill = 1,      // 填充（可能变形）
        Uniform = 2,   // 适应（保持比例，留黑边）
        UniformToFill = 3, // 裁剪填充（保持比例，裁掉溢出）
        Center = 4,    // 居中原始尺寸
        Span = 5       // 跨区
    }

    public class AppSettings
    {
        public bool UseWindowsHello { get; set; } = false;
        public bool UseLinuxMonitor { get; set; } = true;
        public string BackdropType { get; set; } = "Mica";
        public string TerminalBackground { get; set; } = "#1E3047";
        public bool UseImageBackground { get; set; } = false;
        public string? ImageBackgroundPath { get; set; }
        public int ImageStretchMode { get; set; } = 1; // 默认填充
        public int TracerouteTimeout { get; set; } = 2; // 默认 2 秒
        public int TracerouteMaxHops { get; set; } = 30; // 默认 30 跳
        public string TerminalFont { get; set; } = "Cascadia Code";
        public int TerminalFontSize { get; set; } = 10;
        public bool InjectChineseLocale { get; set; } = true;
    }
}
