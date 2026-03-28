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
    }

    public enum ProxyType { None, Http, Socks5 }

    public class AppSettings
    {
        public bool UseWindowsHello { get; set; } = false;
        public bool UseLinuxMonitor { get; set; } = true;
        public string BackdropType { get; set; } = "Acrylic";
    }
}
