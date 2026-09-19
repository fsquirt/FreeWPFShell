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


fn collect_unit_pids() -> HashMap<String, u32> {
    let mut map: HashMap<String, u32> = HashMap::new();
    let dir = match fs::read_dir("/proc") { Ok(d) => d, Err(_) => return map };
    for entry in dir.flatten() {
        let pid: u32 = match entry.file_name().to_string_lossy().parse() {
            Ok(p) => p,
            Err(_) => continue,
        };
        if pid == 0 { continue; }
        let content = match fs::read_to_string(format!("/proc/{}/cgroup", pid)) { Ok(c) => c, Err(_) => continue };
        for line in content.lines() {
            let path = match line.rsplit(':').next() { Some(p) => p, None => continue };
            let unit = match path.split('/').find(|seg| seg.ends_with(".service")) { Some(u) => u, None => continue };
            let better = match map.get(unit) {
                Some(&cur) => pid < cur,
                None => true,
            };
            if better { map.insert(unit.to_string(), pid); }
        }
    }
    map
}

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
    }

    let mut rows: Vec<Row> = Vec::new();
    for line in out.lines() {
        if line.trim().is_empty() { continue; }
        let mut it = line.split_whitespace();
        let mut name = match it.next() { Some(n) => n, None => continue };
        if name == "●" {
            name = match it.next() { Some(n) => n, None => continue };
        }
        let name = name.trim_start_matches('●').to_string();
        if !name.ends_with(".service") { continue; }
        let load_state = it.next().unwrap_or("").to_string();
        let active_state = it.next().unwrap_or("").to_string();
        let sub_state = it.next().unwrap_or("").to_string();
        let description = it.collect::<Vec<&str>>().join(" ");
        rows.push(Row { name, description, load_state, active_state, sub_state });
    }


    let unit_pids = collect_unit_pids();

    let mut services = Vec::with_capacity(rows.len());
    for row in rows.into_iter() {
        let pid = unit_pids.get(&row.name).copied().unwrap_or(0);
        let (user, group) = if pid > 0 {
            let (uid_val, gid_val) = read_pid_uid_gid(pid);
            (resolve_uid(uid_val, &passwd_cache), resolve_gid(gid_val, &group_cache))
        } else {
            (String::new(), String::new())
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
