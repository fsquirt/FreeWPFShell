use serde::Serialize;

#[derive(Serialize, Clone)]
pub struct ProcessItem {
    #[serde(rename = "Pid")]
    pub pid: u32,
    #[serde(rename = "User")]
    pub user: String,
    #[serde(rename = "Mem")]
    pub mem: String,
    #[serde(rename = "Cpu")]
    pub cpu: String,
    #[serde(rename = "File")]
    pub file: String,
    #[serde(rename = "Cmd")]
    pub cmd: String,
}

#[derive(Serialize, Clone)]
pub struct ProcessDetail {
    pub pid: u32,
    pub ppid: u32,
    pub uid_gid: String,
    pub status: String,
    pub priority_nice: String,
    pub cpu_time: String,
    pub fd_count: usize,
    pub mem_info: String,
    pub ulimit: String,
    pub cwd: String,
    pub argv: String,
    pub signals: String,
    pub tty: String,
    pub context: String,
}

#[derive(Serialize, Clone)]
pub struct DiskItem {
    #[serde(rename = "Path")]
    pub path: String,
    #[serde(rename = "Avail")]
    pub avail: String,
    #[serde(rename = "Size")]
    pub size: String,
}

#[derive(Serialize, Clone)]
pub struct SysStats {
    pub cpu_pct: f32,
    pub mem_used: u64,
    pub mem_total: u64,
    pub swap_used: u64,
    pub swap_total: u64,
    pub uptime: String,
    pub load: String,
    pub rx_speed: u64,
    pub tx_speed: u64,
    pub iface: String,
    pub processes: Vec<ProcessItem>,
    pub disks: Vec<DiskItem>,
}

#[derive(Serialize, Clone)]
pub struct LoginRecord {
    pub user: String,
    pub ip: String,
    pub time: String,
    pub timestamp: i64,
}

#[derive(Serialize, Clone)]
pub struct ServiceItem {
    pub name: String,
    pub description: String,
    pub active_state: String,
    pub sub_state: String,
    pub load_state: String,
    pub pid: u32,
    pub user: String,
    pub group: String,
}

#[derive(Serialize, Clone)]
pub struct NetConnItem {
    pub proto: String,
    pub local: String,
    pub remote: String,
    pub state: String,
    pub pid: u32,
    pub user: String,
    pub program: String,
}

#[derive(Serialize, Clone)]
pub struct CronJobItem {
    pub line_index: usize,
    pub schedule: String,
    pub command: String,
    pub enabled: bool,
    pub raw: String,
}
