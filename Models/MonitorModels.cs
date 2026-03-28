using System.Collections.Generic;

namespace FreeWPFShell.Models
{
    public class ProcessItem
    {
        public string Mem { get; set; } = string.Empty;
        public string Cpu { get; set; } = string.Empty;
        public string Cmd { get; set; } = string.Empty;
    }

    public class DiskItem
    {
        public string Path { get; set; } = string.Empty;
        public string Avail { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
    }

    public class SysStats
    {
        public float cpu_pct { get; set; }
        public ulong mem_used { get; set; }
        public ulong mem_total { get; set; }
        public ulong swap_used { get; set; }
        public ulong swap_total { get; set; }
        public string uptime { get; set; } = string.Empty;
        public string load { get; set; } = string.Empty;
        public ulong rx_speed { get; set; }
        public ulong tx_speed { get; set; }
        public string iface { get; set; } = string.Empty;
        public List<ProcessItem> processes { get; set; } = new();
        public List<DiskItem> disks { get; set; } = new();
    }
}
