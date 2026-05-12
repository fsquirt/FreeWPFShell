use crate::models::{ProcessDetail, NetConnItem};
use std::fs;
use std::path::Path;
use std::collections::HashMap;
use std::net::{Ipv4Addr, Ipv6Addr};

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

    let passwd_cache = crate::service_manager::load_passwd_cache();

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
