using System.Collections.Generic;

namespace YouShell.Models
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
    }
}
