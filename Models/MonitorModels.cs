using System.Collections.Generic;

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

    public class ProcessDetail
    {
        public uint pid { get; set; }
        public uint ppid { get; set; }
        public string uid_gid { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public string priority_nice { get; set; } = string.Empty;
        public string cpu_time { get; set; } = string.Empty;
        public int fd_count { get; set; }
        public string mem_info { get; set; } = string.Empty;
        public string ulimit { get; set; } = string.Empty;
        public string cwd { get; set; } = string.Empty;
        public string argv { get; set; } = string.Empty;
        public string signals { get; set; } = string.Empty;
        public string tty { get; set; } = string.Empty;
        public string context { get; set; } = string.Empty;
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
