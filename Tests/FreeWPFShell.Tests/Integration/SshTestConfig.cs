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
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 22;
        public string User { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        /// <summary>配置文件相对于测试输出目录的路径（拷贝到输出目录）。</summary>
        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "sshtest.json");

        public bool IsValid =>
            !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Password);

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
