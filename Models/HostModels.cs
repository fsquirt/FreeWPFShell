using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class SshConnectionInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

        public string? SshKeyId { get; set; }

        private string _linuxDistro = string.Empty;

        public string LinuxDistro
        {
            get => _linuxDistro;
            set { if (_linuxDistro != value) { _linuxDistro = value; OnPropertyChanged(); } }
        }

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

        public string? SshKeyId { get; set; }
    }

    public enum ProxyType { None, Http, Socks4, Socks5, Ssh }

    public enum PixelShaderImageStretchMode
    {
        None = 0,      
        Fill = 1,      
        Uniform = 2,   
        UniformToFill = 3, 
        Center = 4,    
        Span = 5       
    }

    public class AppSettings
    {
        public bool UseWindowsHello { get; set; } = false;
        public bool UseLinuxMonitor { get; set; } = true;
        public string BackdropType { get; set; } = "Mica";
        public string TerminalBackground { get; set; } = "#1E3047";
        public bool UseImageBackground { get; set; } = false;
        public string? ImageBackgroundPath { get; set; }
        public int ImageStretchMode { get; set; } = 1; 
        public int TracerouteTimeout { get; set; } = 2; 
        public int TracerouteMaxHops { get; set; } = 30; 
        public string TerminalFont { get; set; } = "Cascadia Code";
        public int TerminalFontSize { get; set; } = 10;
        public bool InjectChineseLocale { get; set; } = true;
    }
}
