use crate::utils::{json_escape, format_size};


pub fn json_array<T, F: Fn(&T) -> String>(items: &[T], f: F) -> String {
    let parts: Vec<String> = items.iter().map(f).collect();
    format!("[{}]", parts.join(","))
}

#[derive(Clone)]
pub struct ProcessItem {
    pub pid: u32,
    pub user: String,
    pub mem: String,
    pub cpu: String,
    pub file: String,
    pub cmd: String,
}

impl ProcessItem {
    pub fn json(&self) -> String {
        format!(
            "{{\"Pid\":{},\"User\":\"{}\",\"Mem\":\"{}\",\"Cpu\":\"{}\",\"File\":\"{}\",\"Cmd\":\"{}\"}}",
            self.pid,
            json_escape(&self.user),
            json_escape(&self.mem),
            json_escape(&self.cpu),
            json_escape(&self.file),
            json_escape(&self.cmd)
        )
    }
}

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

impl ProcessDetail {
    pub fn json(&self) -> String {
        format!(
            "{{\"pid\":{},\"ppid\":{},\"uid_gid\":\"{}\",\"status\":\"{}\",\"priority_nice\":\"{}\",\"cpu_time\":\"{}\",\"fd_count\":{},\"mem_info\":\"{}\",\"ulimit\":\"{}\",\"cwd\":\"{}\",\"argv\":\"{}\",\"signals\":\"{}\",\"tty\":\"{}\",\"context\":\"{}\"}}",
            self.pid,
            self.ppid,
            json_escape(&self.uid_gid),
            json_escape(&self.status),
            json_escape(&self.priority_nice),
            json_escape(&self.cpu_time),
            self.fd_count,
            json_escape(&self.mem_info),
            json_escape(&self.ulimit),
            json_escape(&self.cwd),
            json_escape(&self.argv),
            json_escape(&self.signals),
            json_escape(&self.tty),
            json_escape(&self.context)
        )
    }
}

#[derive(Clone)]
pub struct DiskItem {
    pub path: String,
    pub avail: u64,
    pub size: u64,
}

impl DiskItem {
    pub fn json(&self) -> String {
        format!(
            "{{\"Path\":\"{}\",\"Avail\":\"{}\",\"Size\":\"{}\"}}",
            json_escape(&self.path),
            format_size(self.avail),
            format_size(self.size)
        )
    }
}

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
    pub os_id: String,
    pub processes: Vec<ProcessItem>,
    pub disks: Vec<DiskItem>,
}

impl SysStats {
    pub fn json(&self) -> String {
        let procs = json_array(&self.processes, |p| p.json());
        let disks = json_array(&self.disks, |d| d.json());
        format!(
            "{{\"cpu_pct\":{:.2},\"mem_used\":{},\"mem_total\":{},\"swap_used\":{},\"swap_total\":{},\"uptime\":\"{}\",\"load\":\"{}\",\"rx_speed\":{},\"tx_speed\":{},\"iface\":\"{}\",\"os_id\":\"{}\",\"processes\":{},\"disks\":{}}}",
            self.cpu_pct,
            self.mem_used,
            self.mem_total,
            self.swap_used,
            self.swap_total,
            json_escape(&self.uptime),
            json_escape(&self.load),
            self.rx_speed,
            self.tx_speed,
            json_escape(&self.iface),
            json_escape(&self.os_id),
            procs,
            disks
        )
    }
}

#[derive(Clone)]
pub struct LoginRecord {
    pub user: String,
    pub ip: String,
    pub time: String,
    pub timestamp: i64,
}

impl LoginRecord {
    pub fn json(&self) -> String {
        format!(
            "{{\"user\":\"{}\",\"ip\":\"{}\",\"time\":\"{}\",\"timestamp\":{}}}",
            json_escape(&self.user),
            json_escape(&self.ip),
            json_escape(&self.time),
            self.timestamp
        )
    }
}

#[derive(Clone)]
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

impl ServiceItem {
    pub fn json(&self) -> String {
        format!(
            "{{\"name\":\"{}\",\"description\":\"{}\",\"active_state\":\"{}\",\"sub_state\":\"{}\",\"load_state\":\"{}\",\"pid\":{},\"user\":\"{}\",\"group\":\"{}\"}}",
            json_escape(&self.name),
            json_escape(&self.description),
            json_escape(&self.active_state),
            json_escape(&self.sub_state),
            json_escape(&self.load_state),
            self.pid,
            json_escape(&self.user),
            json_escape(&self.group)
        )
    }
}

#[derive(Clone)]
pub struct NetConnItem {
    pub proto: String,
    pub local: String,
    pub remote: String,
    pub state: String,
    pub pid: u32,
    pub user: String,
    pub program: String,
}

impl NetConnItem {
    pub fn json(&self) -> String {
        format!(
            "{{\"proto\":\"{}\",\"local\":\"{}\",\"remote\":\"{}\",\"state\":\"{}\",\"pid\":{},\"user\":\"{}\",\"program\":\"{}\"}}",
            json_escape(&self.proto),
            json_escape(&self.local),
            json_escape(&self.remote),
            json_escape(&self.state),
            self.pid,
            json_escape(&self.user),
            json_escape(&self.program)
        )
    }
}
