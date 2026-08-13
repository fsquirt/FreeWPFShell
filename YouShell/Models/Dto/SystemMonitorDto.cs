using System.Collections.Generic;

namespace YouShell.Models.Dto
{
    /// <summary>
    /// Rust linux-monitor 探针返回的系统统计 DTO。
    /// 字段名与 Rust 端 serde 序列化保持一致（snake_case），仅供反序列化使用。
    /// </summary>
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

    /// <summary>
    /// 进程详细信息 DTO，由 Rust 探针 /process_detail 端点返回。
    /// </summary>
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
}
