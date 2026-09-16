use crate::models::{ProcessDetail, NetConnItem, DiskItem, ProcessItem, SysStats};
use std::collections::HashMap;
use std::fs;
use std::net::{Ipv4Addr, Ipv6Addr};
use std::os::raw::c_char;
use std::ffi::CString;

// ── 系统调用直接声明（零依赖，不引入 libc crate）──────────────────────────

#[repr(C)]
struct StatVfs {
    f_bsize: u64,
    f_frsize: u64,
    f_blocks: u64,
    f_bfree: u64,
    f_bavail: u64,
    f_files: u64,
    f_ffree: u64,
    f_favail: u64,
    f_fsid: u64,
    f_flag: u64,
    f_namemax: u64,
    __spare: [i32; 6],
}

extern "C" {
    fn statvfs(path: *const c_char, buf: *mut StatVfs) -> i32;
    fn sysconf(name: i32) -> i64;
}

const SC_PAGESIZE: i32 = 30;
const SC_NPROCESSORS_ONLN: i32 = 84;

// ── /proc 采集器（替代 sysinfo crate）────────────────────────────────────

pub struct Collector {
    prev_cpu: Option<(u64, u64)>,          // (total, idle) jiffies
    prev_procs: HashMap<u32, u64>,         // pid -> utime+stime jiffies
    prev_net: HashMap<String, (u64, u64)>, // iface -> (rx, tx) bytes
    prev_net_time: Option<std::time::Instant>,
    page_size: u64,
    ncpus: u64,
}

impl Collector {
    pub fn new() -> Self {
        Collector {
            prev_cpu: None,
            prev_procs: HashMap::new(),
            prev_net: HashMap::new(),
            prev_net_time: None,
            page_size: unsafe { sysconf(SC_PAGESIZE) }.max(512) as u64,
            ncpus: unsafe { sysconf(SC_NPROCESSORS_ONLN) }.max(1) as u64,
        }
    }

    pub fn collect(&mut self) -> (SysStats, Vec<ProcessItem>) {
        let mem = read_meminfo();
        let now = std::time::Instant::now();

        // ── 进程列表 ──
        let total_cpu = read_total_cpu_jiffies();
        let mut procs: Vec<(u32, u64, u64)> = Vec::new(); // (pid, jiffies, rss_bytes)
        if let Ok(entries) = fs::read_dir("/proc") {
            for entry in entries.flatten() {
                let pid: u32 = match entry.file_name().to_string_lossy().parse() {
                    Ok(p) => p,
                    Err(_) => continue,
                };
                let stat = match fs::read_to_string(format!("/proc/{}/stat", pid)) {
                    Ok(s) => s,
                    Err(_) => continue,
                };
                let Some((jiffies, rss_pages)) = parse_stat_jiffies_rss(&stat) else { continue };
                procs.push((pid, jiffies, rss_pages * self.page_size));
            }
        }

        let cpu_delta_total = match (self.prev_cpu, total_cpu) {
            (Some((pt, _)), (t, _)) if t > pt => t - pt,
            _ => 0,
        };
        let elapsed = match self.prev_net_time {
            Some(prev) => now.duration_since(prev).as_secs_f64().max(0.001),
            None => 1.0,
        };

        let mem_total = mem.mem_total.max(1);
        let mut all_processes: Vec<ProcessItem> = Vec::with_capacity(procs.len());
        let mut new_prev_procs: HashMap<u32, u64> = HashMap::with_capacity(procs.len());
        for (pid, jiffies, rss_bytes) in procs {
            new_prev_procs.insert(pid, jiffies);
            let cpu_pct = if cpu_delta_total > 0 {
                let p_delta = match self.prev_procs.get(&pid) {
                    Some(prev) if jiffies > *prev => jiffies - *prev,
                    _ => 0,
                };
                // 与 top 的 %CPU 一致：多核进程可超过 100%
                100.0 * (p_delta * self.ncpus) as f64 / cpu_delta_total as f64
            } else {
                0.0
            };
            let cmd = fs::read_to_string(format!("/proc/{}/cmdline", pid))
                .map(|s| s.replace('\0', " "))
                .unwrap_or_default();
            let stat_comm = fs::read_to_string(format!("/proc/{}/stat", pid))
                .ok()
                .and_then(|s| parse_stat_comm(&s));
            let cmd = if cmd.trim().is_empty() {
                stat_comm.unwrap_or_default()
            } else {
                cmd
            };
            let file = fs::read_link(format!("/proc/{}/exe", pid))
                .map(|p| p.to_string_lossy().into_owned())
                .unwrap_or_default();
            all_processes.push(ProcessItem {
                pid,
                user: read_proc_uid(pid).to_string(),
                mem: format!("{:.1}%", (rss_bytes as f64 / mem_total as f64) * 100.0),
                cpu: format!("{:.1}%", cpu_pct),
                file,
                cmd,
            });
        }
        all_processes.sort_by(|a, b| {
            let pa = parse_pct(&a.cpu);
            let pb = parse_pct(&b.cpu);
            pb.partial_cmp(&pa).unwrap_or(std::cmp::Ordering::Equal).then(a.pid.cmp(&b.pid))
        });

        // ── 网络 ──
        let (rx_speed, tx_speed, iface) = read_net_speed(
            &mut self.prev_net,
            self.prev_net_time.is_none(),
            elapsed,
        );
        self.prev_net_time = Some(now);

        // ── 磁盘 ──
        let disks = read_disks();

        self.prev_cpu = Some(total_cpu);
        self.prev_procs = new_prev_procs;

        let stats = SysStats {
            cpu_pct: read_cpu_pct(),
            mem_used: mem.mem_used,
            mem_total: mem.mem_total,
            swap_used: mem.swap_used,
            swap_total: mem.swap_total,
            uptime: crate::utils::format_uptime(read_uptime_secs()),
            load: read_load1(),
            rx_speed,
            tx_speed,
            iface,
            processes: all_processes.iter().take(15).cloned().collect(),
            disks,
        };
        (stats, all_processes)
    }
}

fn parse_pct(s: &str) -> f64 {
    s.trim_end_matches('%').parse().unwrap_or(0.0)
}

fn read_proc_uid(pid: u32) -> u32 {
    fs::read_to_string(format!("/proc/{}/status", pid))
        .ok()
        .and_then(|s| s.lines().find(|l| l.starts_with("Uid:")).and_then(|l| l.split_whitespace().nth(1).and_then(|v| v.parse().ok())))
        .unwrap_or(0)
}

/// /proc/[pid]/stat：comm 可含空格，定位最后一个 ')' 之后的字段。
/// utime=字段14(索引11)、stime=字段15(索引12)、rss=字段24(索引21，单位页)。
fn parse_stat_jiffies_rss(stat: &str) -> Option<(u64, u64)> {
    let after = stat.rsplit_once(')')?.1.trim();
    let parts: Vec<&str> = after.split_whitespace().collect();
    if parts.len() < 22 { return None; }
    let utime: u64 = parts[11].parse().unwrap_or(0);
    let stime: u64 = parts[12].parse().unwrap_or(0);
    let rss: u64 = parts[21].parse().unwrap_or(0);
    Some((utime + stime, rss))
}

fn parse_stat_comm(stat: &str) -> Option<String> {
    let start = stat.find('(')? + 1;
    let end = stat.rfind(')')?;
    Some(stat[start..end].to_string())
}

fn read_total_cpu_jiffies() -> (u64, u64) {
    // 返回 (total, idle+iowait)
    let Ok(content) = fs::read_to_string("/proc/stat") else { return (0, 0) };
    let Some(line) = content.lines().find(|l| l.starts_with("cpu ")) else { return (0, 0) };
    let vals: Vec<u64> = line[4..]
        .split_whitespace()
        .filter_map(|v| v.parse().ok())
        .collect();
    if vals.is_empty() { return (0, 0); }
    let total: u64 = vals.iter().sum();
    let idle = vals.get(3).copied().unwrap_or(0) + vals.get(4).copied().unwrap_or(0);
    (total, idle)
}

fn read_cpu_pct() -> f32 {
    // 基于连续两次采样差值：用独立静态状态保存上次值
    thread_local! {
        static PREV: std::cell::RefCell<Option<(u64, u64)>> = const { std::cell::RefCell::new(None) };
    }
    let cur = read_total_cpu_jiffies();
    PREV.with(|p| {
        let mut p = p.borrow_mut();
        let pct = match (*p, cur) {
            (Some((pt, pi)), (t, i)) if t > pt => {
                let dt = t - pt;
                let di = i.saturating_sub(pi);
                if dt > 0 { (1.0 - di as f64 / dt as f64).clamp(0.0, 1.0) as f32 * 100.0 } else { 0.0 }
            }
            _ => 0.0,
        };
        *p = Some(cur);
        pct
    })
}

struct MemInfo {
    mem_total: u64,
    mem_used: u64,
    swap_total: u64,
    swap_used: u64,
}

fn read_meminfo() -> MemInfo {
    let mut info = MemInfo { mem_total: 0, mem_used: 0, swap_total: 0, swap_used: 0 };
    let Ok(content) = fs::read_to_string("/proc/meminfo") else { return info };
    let mut mem_available = 0u64;
    let mut mem_free = 0u64;
    let mut buffers = 0u64;
    let mut cached = 0u64;
    let mut swap_free = 0u64;
    for line in content.lines() {
        let mut it = line.split_whitespace();
        let key = it.next().unwrap_or("");
        let val: u64 = it.next().and_then(|v| v.parse().ok()).unwrap_or(0);
        match key {
            "MemTotal:" => info.mem_total = val * 1024,
            "MemAvailable:" => mem_available = val * 1024,
            "MemFree:" => mem_free = val * 1024,
            "Buffers:" => buffers = val * 1024,
            "Cached:" => cached = val * 1024,
            "SwapTotal:" => info.swap_total = val * 1024,
            "SwapFree:" => swap_free = val * 1024,
            _ => {}
        }
    }
    // 与 sysinfo 定义一致：used = total - available（老内核无 MemAvailable 时用 free+buffers+cached 估算）
    let avail = if mem_available > 0 { mem_available } else { mem_free + buffers + cached };
    info.mem_used = info.mem_total.saturating_sub(avail);
    info.swap_used = info.swap_total.saturating_sub(swap_free);
    info
}

fn read_uptime_secs() -> u64 {
    fs::read_to_string("/proc/uptime")
        .ok()
        .and_then(|s| s.split_whitespace().next().and_then(|v| v.parse::<f64>().ok()))
        .unwrap_or(0.0) as u64
}

fn read_load1() -> String {
    fs::read_to_string("/proc/loadavg")
        .ok()
        .and_then(|s| s.split_whitespace().next().map(|v| v.to_string()))
        .unwrap_or_else(|| "0".to_string())
}

fn read_net_speed(
    prev: &mut HashMap<String, (u64, u64)>,
    first_tick: bool,
    elapsed: f64,
) -> (u64, u64, String) {
    let Ok(content) = fs::read_to_string("/proc/net/dev") else { return (0, 0, "eth0".to_string()) };
    let mut cur: HashMap<String, (u64, u64)> = HashMap::new();
    for line in content.lines().skip(2) {
        let Some((name, rest)) = line.split_once(':') else { continue };
        let name = name.trim().to_string();
        let vals: Vec<u64> = rest.split_whitespace().filter_map(|v| v.parse().ok()).collect();
        if vals.len() < 9 { continue; }
        cur.insert(name, (vals[0], vals[8])); // rx bytes, tx bytes
    }

    // 选流量最大的非 lo 网卡（与原 sysinfo 逻辑一致）
    let mut best: Option<(String, (u64, u64))> = None;
    for (name, cnt) in &cur {
        if name.contains("lo") { continue; }
        if best.as_ref().map(|(_, b)| cnt.0 + cnt.1 > b.0 + b.1).unwrap_or(true) {
            best = Some((name.clone(), *cnt));
        }
    }
    let Some((iface, (rx, tx))) = best else { return (0, 0, "eth0".to_string()) };

    let (rx_speed, tx_speed) = if first_tick {
        (0, 0)
    } else {
        match prev.get(&iface) {
            Some(&(prx, ptx)) => {
                let secs = elapsed.max(0.001);
                (
                    (rx.saturating_sub(prx) as f64 / secs) as u64,
                    (tx.saturating_sub(ptx) as f64 / secs) as u64,
                )
            }
            None => (0, 0),
        }
    };
    *prev = cur;
    (rx_speed, tx_speed, iface)
}

fn read_disks() -> Vec<DiskItem> {
    let Ok(mounts) = fs::read_to_string("/proc/mounts") else { return Vec::new() };
    let mut seen = std::collections::HashSet::new();
    let mut items = Vec::new();
    for line in mounts.lines() {
        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.len() < 3 { continue; }
        // /proc/mounts 格式：<设备> <挂载点> <文件系统类型> <选项...>
        let (dev, mp, fstype) = (parts[0], parts[1], parts[2]);
        // 只保留真实块设备（与 sysinfo 行为一致），排除伪文件系统与重复挂载
        if !dev.starts_with("/dev/") || fstype == "squashfs" { continue; }
        if !seen.insert(dev.to_string()) { continue; }

        let Ok(cpath) = CString::new(mp.replace("\\040", " ").replace("\\011", "\t")) else { continue };
        let mut sv = StatVfs {
            f_bsize: 0, f_frsize: 0, f_blocks: 0, f_bfree: 0, f_bavail: 0,
            f_files: 0, f_ffree: 0, f_favail: 0, f_fsid: 0, f_flag: 0, f_namemax: 0,
            __spare: [0; 6],
        };
        if unsafe { statvfs(cpath.as_ptr(), &mut sv) } != 0 { continue; }
        items.push(DiskItem {
            path: mp.to_string(),
            avail: sv.f_bavail.saturating_mul(sv.f_frsize),
            size: sv.f_blocks.saturating_mul(sv.f_frsize),
        });
    }
    items
}

// ── 进程详情 / 网络连接（原有 /proc 手写实现，保留）──────────────────────

pub fn get_process_detail(pid_val: u32) -> Option<ProcessDetail> {
    let pid_str = pid_val.to_string();
    let proc_path = format!("/proc/{}", pid_str);
    let status_content = match fs::read_to_string(format!("{}/status", proc_path)) {
        Ok(c) => c,
        Err(_) => return None, // 读不到说明进程退出了，直接返回 None
    };
    let mut ppid = 0;
    let mut uid_gid = String::new();
    let mut state = String::new();
    for line in status_content.lines() {
        if line.starts_with("PPid:") { ppid = line[5..].trim().parse().unwrap_or(0); }
        if line.starts_with("Uid:") { uid_gid = line[4..].trim().to_string(); }
        if line.starts_with("State:") { state = line[6..].trim().to_string(); }
    }

    let stat_content = fs::read_to_string(format!("{}/stat", proc_path)).unwrap_or_default();
    let stat_parts: Vec<&str> = stat_content.split_whitespace().collect();
    let (priority, nice, utime, stime) = if stat_parts.len() > 18 {
        (stat_parts[17], stat_parts[18], stat_parts[13].parse::<u64>().unwrap_or(0), stat_parts[14].parse::<u64>().unwrap_or(0))
    } else { ("0", "0", 0, 0) };

    let fd_count = fs::read_dir(format!("{}/fd", proc_path)).map(|d| d.count()).unwrap_or(0);
    let cwd = fs::read_link(format!("{}/cwd", proc_path)).map(|p| p.to_string_lossy().into_owned()).unwrap_or_else(|_| "Unknown".to_string());
    let cmdline = fs::read_to_string(format!("{}/cmdline", proc_path)).map(|s| s.replace('\0', " ")).unwrap_or_default();
    let limits = fs::read_to_string(format!("{}/limits", proc_path)).unwrap_or_default();
    let tty = if let Ok(t) = fs::read_link(format!("{}/fd/0", proc_path)) { t.to_string_lossy().into_owned() } else { "None".to_string() };

    Some(ProcessDetail {
        pid: pid_val,
        ppid,
        uid_gid,
        status: state,
        priority_nice: format!("{}/{}", priority, nice),
        cpu_time: format!("{}s", (utime + stime) / 100),
        fd_count,
        mem_info: fs::read_to_string(format!("{}/statm", proc_path)).unwrap_or_default(),
        ulimit: limits.lines().filter(|l| l.contains("Max open files") || l.contains("Max resident set")).collect::<Vec<_>>().join("\n"),
        cwd,
        argv: cmdline,
        signals: fs::read_to_string(format!("{}/status", proc_path)).unwrap_or_default().lines().filter(|l| l.contains("Sig")).collect::<Vec<_>>().join("\n"),
        tty,
        context: fs::read_to_string(format!("{}/stack", proc_path)).unwrap_or_else(|_| "Unavailable".to_string()),
    })
}

fn hex_to_ip_port(hex: &str) -> String {
    let parts: Vec<&str> = hex.split(':').collect();
    if parts.len() != 2 { return hex.to_string(); }

    let ip_hex = parts[0];
    let port = u16::from_str_radix(parts[1], 16).unwrap_or(0);

    if ip_hex.len() == 8 {
        // IPv4 (Little Endian in /proc/net/tcp)
        if let Ok(val) = u32::from_str_radix(ip_hex, 16) {
            let ip = Ipv4Addr::from(val.swap_bytes()); // 内核以小端存储
            return format!("{}:{}", ip, port);
        }
    } else if ip_hex.len() == 32 {
        // IPv6 (4 chunks of 32-bit little-endian)
        let mut addr = [0u8; 16];
        for i in 0..4 {
            if let Ok(val) = u32::from_str_radix(&ip_hex[i*8..(i+1)*8], 16) {
                let bytes = val.to_ne_bytes(); // 内核在tcp6中通常按机器字节序存
                addr[i*4..(i+1)*4].copy_from_slice(&bytes);
            }
        }
        let ip = Ipv6Addr::from(addr);
        return format!("[{}]:{}", ip, port);
    }
    hex.to_string()
}

fn get_tcp_state(state_hex: &str) -> &str {
    match state_hex {
        "01" => "ESTABLISHED",
        "02" => "SYN_SENT",
        "03" => "SYN_RECV",
        "04" => "FIN_WAIT1",
        "05" => "FIN_WAIT2",
        "06" => "TIME_WAIT",
        "07" => "CLOSE",
        "08" => "CLOSE_WAIT",
        "09" => "LAST_ACK",
        "0A" => "LISTEN",
        "0B" => "CLOSING",
        _ => "UNKNOWN",
    }
}

pub fn get_net_conns() -> Vec<NetConnItem> {
    let mut inode_to_pid = HashMap::new();
    let mut pid_to_info = HashMap::new();

    // 1. 扫描所有进程的 fd 以建立 inode -> pid 映射
    if let Ok(entries) = fs::read_dir("/proc") {
        for entry in entries.flatten() {
            let pid_str = entry.file_name();
            let pid: u32 = match pid_str.to_string_lossy().parse() {
                Ok(p) => p,
                Err(_) => continue,
            };

            let fd_path = format!("/proc/{}/fd", pid);
            if let Ok(fds) = fs::read_dir(&fd_path) {
                for fd in fds.flatten() {
                    if let Ok(link) = fs::read_link(fd.path()) {
                        let link_str = link.to_string_lossy();
                        if link_str.starts_with("socket:[") {
                            let inode = &link_str[8..link_str.len() - 1];
                            inode_to_pid.insert(inode.to_string(), pid);
                        }
                    }
                }
            }

            // 预存进程信息（程序路径和用户名）
            let exe = fs::read_link(format!("/proc/{}/exe", pid))
                .map(|p| p.to_string_lossy().into_owned())
                .unwrap_or_else(|_| {
                    // 如果 exe 读不到，尝试从 status 读名称
                    fs::read_to_string(format!("/proc/{}/status", pid))
                        .ok()
                        .and_then(|s| s.lines().next().map(|l| l[5..].trim().to_string()))
                        .unwrap_or_default()
                });

            let user = fs::read_to_string(format!("/proc/{}/status", pid))
                .ok()
                .and_then(|s| s.lines().find(|l| l.starts_with("Uid:")).map(|l| l.split_whitespace().nth(1).unwrap_or("0").to_string()))
                .unwrap_or_else(|| "0".to_string());

            pid_to_info.insert(pid, (exe, user));
        }
    }

    let mut conns = Vec::new();
    let proc_files = [
        ("/proc/net/tcp", "TCP"),
        ("/proc/net/tcp6", "TCP6"),
        ("/proc/net/udp", "UDP"),
        ("/proc/net/udp6", "UDP6"),
    ];

    let passwd_cache = crate::service_manager::get_passwd_cache();

    for (file, proto) in proc_files {
        if let Ok(content) = fs::read_to_string(file) {
            for line in content.lines().skip(1) {
                let parts: Vec<&str> = line.split_whitespace().collect();
                if parts.len() < 10 { continue; }

                let local = hex_to_ip_port(parts[1]);
                let remote = hex_to_ip_port(parts[2]);
                let state = if proto.starts_with("TCP") { get_tcp_state(parts[3]).to_string() } else { "".to_string() };
                let inode = parts[9];

                let pid = *inode_to_pid.get(inode).unwrap_or(&0);
                let mut user_name = String::new();
                let mut program = String::new();

                if pid > 0 {
                    if let Some((exe, uid_str)) = pid_to_info.get(&pid) {
                        program = exe.clone();
                        if let Ok(uid) = uid_str.parse::<u32>() {
                            user_name = crate::service_manager::resolve_uid(uid, &passwd_cache);
                        }
                    }
                } else {
                    // 如果没找到进程关联，尝试获取该连接本身的 UID
                    if let Ok(uid) = parts[7].parse::<u32>() {
                        user_name = crate::service_manager::resolve_uid(uid, &passwd_cache);
                    }
                }

                conns.push(NetConnItem {
                    proto: proto.to_string(),
                    local,
                    remote,
                    state,
                    pid,
                    user: user_name,
                    program,
                });
            }
        }
    }

    conns
}