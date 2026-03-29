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
        public string PortMapping
        {
            get
            {
                if (Type == "服务器->本机")
                {
                    // 把远程服务器上的资源映射到本地
                    return $"{DestAddress}:{DestPort} -> 127.0.0.1:{BindPort}";
                }
                else if (Type == "本机->服务器")
                {
                    // 把本机的资源映射到远程服务器端口
                    return $"{DestAddress}:{BindPort} -> 服务器监听:{DestPort}";
                }
                return $"{BindAddress}:{BindPort} -> {DestAddress}:{DestPort}";
            }
        }
    }
}
