using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class ProcessItem
    {
        public uint Pid { get; set; }
        public string User { get; set; } = "root";
        public string Mem { get; set; } = string.Empty;
        public string Cpu { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Cmd { get; set; } = string.Empty;
    }

    public class DiskItem
    {
        public string Path { get; set; } = string.Empty;
        public string Avail { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
    }

    public class NetConnItem
    {
        public string Proto { get; set; } = string.Empty;
        public string Local { get; set; } = string.Empty;
        public string Remote { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public uint Pid { get; set; }
        public string User { get; set; } = string.Empty;
        public string Program { get; set; } = string.Empty;

        /// <summary>远程地址的 IP 归属地，由客户端查询本地 IP 库填充。</summary>
        [JsonIgnore]
        public string RemoteGeo { get; set; } = string.Empty;
    }
}
