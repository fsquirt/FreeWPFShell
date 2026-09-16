use crate::models::ServiceItem;
use std::collections::HashMap;
use std::fs;
use std::process::Command;
use std::sync::OnceLock;

pub fn resolve_uid(uid: u32, passwd_cache: &HashMap<u32, String>) -> String {
    if uid == 0 { return "root".to_string(); }
    if let Some(name) = passwd_cache.get(&uid) { return name.clone(); }
    uid.to_string()
}

pub fn resolve_gid(gid: u32, group_cache: &HashMap<u32, String>) -> String {
    if gid == 0 { return "root".to_string(); }
    if let Some(name) = group_cache.get(&gid) { return name.clone(); }
    gid.to_string()
}

fn load_passwd_map() -> HashMap<u32, String> {
    let mut map = HashMap::new();
    if let Ok(content) = fs::read_to_string("/etc/passwd") {
        for line in content.lines() {
            let parts: Vec<&str> = line.split(':').collect();
            if parts.len() >= 3 {
                if let Ok(uid) = parts[2].parse::<u32>() {
                    map.insert(uid, parts[0].to_string());
                }
            }
        }
    }
    map
}

fn load_group_map() -> HashMap<u32, String> {
    let mut map = HashMap::new();
    if let Ok(content) = fs::read_to_string("/etc/group") {
        for line in content.lines() {
            let parts: Vec<&str> = line.split(':').collect();
            if parts.len() >= 3 {
                if let Ok(gid) = parts[2].parse::<u32>() {
                    map.insert(gid, parts[0].to_string());
                }
            }
        }
    }
    map
}

static PASSWD_CACHE: OnceLock<HashMap<u32, String>> = OnceLock::new();
static GROUP_CACHE: OnceLock<HashMap<u32, String>> = OnceLock::new();

pub fn get_passwd_cache() -> &'static HashMap<u32, String> {
    PASSWD_CACHE.get_or_init(load_passwd_map)
}

pub fn get_group_cache() -> &'static HashMap<u32, String> {
    GROUP_CACHE.get_or_init(load_group_map)
}

pub fn read_pid_uid_gid(pid: u32) -> (u32, u32) {
    let status_path = format!("/proc/{}/status", pid);
    let content = match fs::read_to_string(&status_path) { Ok(c) => c, Err(_) => return (0, 0) };
    let mut uid: u32 = 0;
    let mut gid: u32 = 0;
    for line in content.lines() {
        if line.starts_with("Uid:") {
            uid = line[4..].split_whitespace().next().and_then(|v| v.parse().ok()).unwrap_or(0);
        }
        if line.starts_with("Gid:") {
            gid = line[4..].split_whitespace().next().and_then(|v| v.parse().ok()).unwrap_or(0);
        }
    }
    (uid, gid)
}

/// systemd 服务列表：systemctl list-units 一行含 名称/LOAD/ACTIVE/SUB/描述，
/// 活跃服务的主 PID 用一次批量 `systemctl show <units...> -p MainPID --value` 获取
/// （输出按参数顺序，每单元一行）。
pub fn get_systemd_services() -> Vec<ServiceItem> {
    let out = match Command::new("systemctl")
        .args(["list-units", "--type=service", "--all", "--no-legend", "--no-pager"])
        .output()
    {
        Ok(o) if o.status.success() => String::from_utf8_lossy(&o.stdout).to_string(),
        _ => return Vec::new(),
    };

    let passwd_cache = get_passwd_cache();
    let group_cache = get_group_cache();

    struct Row {
        name: String,
        description: String,
        load_state: String,
        active_state: String,
        sub_state: String,
        pid_slot: Option<usize>, // 活跃服务在 active_units/pids 中的下标
    }

    let mut rows: Vec<Row> = Vec::new();
    let mut active_units: Vec<String> = Vec::new();
    for line in out.lines() {
        if line.trim().is_empty() { continue; }
        let mut it = line.split_whitespace();
        let name = match it.next() { Some(n) => n.to_string(), None => continue };
        if !name.ends_with(".service") { continue; }
        let load_state = it.next().unwrap_or("").to_string();
        let active_state = it.next().unwrap_or("").to_string();
        let sub_state = it.next().unwrap_or("").to_string();
        let description = {
            let rest_start = name.len() + load_state.len() + active_state.len() + sub_state.len() + 4;
            if line.len() > rest_start { line[rest_start..].trim().to_string() } else { String::new() }
        };
        let pid_slot = if active_state == "active" {
            active_units.push(name.clone());
            Some(active_units.len() - 1)
        } else {
            None
        };
        rows.push(Row { name, description, load_state, active_state, sub_state, pid_slot });
    }

    // 批量查活跃服务主 PID：一条 systemctl 调用拿全部，避免逐个调用拖慢响应
    let mut pids: Vec<u32> = vec![0; active_units.len()];
    if !active_units.is_empty() {
        let mut cmd = Command::new("systemctl");
        cmd.arg("show");
        for u in &active_units { cmd.arg(u); }
        cmd.args(["-p", "MainPID", "--value"]);
        if let Ok(o) = cmd.output() {
            if o.status.success() {
                let stdout = String::from_utf8_lossy(&o.stdout).into_owned();
                let lines: Vec<&str> = stdout.lines().collect();
                if lines.len() == active_units.len() {
                    for (i, l) in lines.iter().enumerate() {
                        pids[i] = l.trim().parse().unwrap_or(0);
                    }
                }
            }
        }
    }

    let mut services = Vec::with_capacity(rows.len());
    for row in rows.into_iter() {
        let (pid, user, group) = match row.pid_slot.and_then(|i| pids.get(i).copied()) {
            Some(p) if p > 0 => {
                let (uid_val, gid_val) = read_pid_uid_gid(p);
                (p, resolve_uid(uid_val, &passwd_cache), resolve_gid(gid_val, &group_cache))
            }
            _ => (0, String::new(), String::new()),
        };
        services.push(ServiceItem {
            name: row.name,
            description: row.description,
            load_state: row.load_state,
            active_state: row.active_state,
            sub_state: row.sub_state,
            pid,
            user,
            group,
        });
    }
    services
}

pub fn service_action(name: &str, action: &str) -> bool {
    match action {
        "start" | "stop" | "restart" => Command::new("systemctl")
            .arg(action)
            .arg(name)
            .status()
            .map(|s| s.success())
            .unwrap_or(false),
        _ => false,
    }
}

pub fn get_service_log(name: &str) -> String {
    Command::new("journalctl")
        .args(["-u", name, "-n", "50", "--no-pager"])
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).to_string())
        .unwrap_or_else(|_| "Failed to read journal".to_string())
}
