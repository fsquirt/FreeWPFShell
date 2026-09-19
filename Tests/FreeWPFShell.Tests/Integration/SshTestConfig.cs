using System.IO;
using System.Text.Json;

namespace FreeWPFShell.Tests.Integration
{

    public class SshTestConfig
    {

        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 22;
        public string User { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;


        public string ProxyHost { get; set; } = string.Empty;
        public int ProxyPort { get; set; } = 0;
        public string ProxyUser { get; set; } = string.Empty;
        public string ProxyPassword { get; set; } = string.Empty;


        public string RemoteHost { get; set; } = string.Empty;
        public int RemotePort { get; set; } = 22;
        public string RemoteUser { get; set; } = string.Empty;
        public string RemotePassword { get; set; } = string.Empty;


        public string JumpHost { get; set; } = string.Empty;
        public int JumpPort { get; set; } = 22;
        public string JumpUser { get; set; } = string.Empty;
        public string JumpPassword { get; set; } = string.Empty;


        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "sshtest.json");

        public bool IsValid =>
            !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Password);

        public bool HasHttpProxy => !string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0;
        public bool HasSocksProxy => !string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0;


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
