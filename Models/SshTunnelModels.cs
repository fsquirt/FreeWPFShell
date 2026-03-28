using Renci.SshNet;

namespace FreeWPFShell.Models
{
    public class SshTunnelInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string Type { get; set; } = "Local";
        public string BindAddress { get; set; } = string.Empty;
        public uint BindPort { get; set; }
        public string DestAddress { get; set; } = string.Empty;
        public uint DestPort { get; set; }
        public string Remark { get; set; } = string.Empty;
        public ForwardedPort? PortConfig { get; set; }
        public string PortMapping => $"{BindAddress}:{BindPort} -> {DestAddress}:{DestPort}";
    }
}
