use serde::Serialize;
use sysinfo::{Disks, Networks, System};
use tiny_http::{Response, Server};
use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicI64, Ordering};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use std::env;
use std::fs;
use std::path::Path;

#[derive(Serialize, Clone)]
struct ProcessItem {
    #[serde(rename = "Pid")]
    pid: u32,
    #[serde(rename = "User")]
    user: String,
    #[serde(rename = "Mem")]
    mem: String,
    #[serde(rename = "Cpu")]
    cpu: String,
    #[serde(rename = "File")]
    file: String,
    #[serde(rename = "Cmd")]
    cmd: String,
}

#[derive(Serialize, Clone)]
struct ProcessDetail {
    pid: u32,
    ppid: u32,
    uid_gid: String,
    status: String,
    priority_nice: String,
    cpu_time: String,
    fd_count: usize,
    mem_info: String,
    ulimit: String,
    cwd: String,
    argv: String,
    signals: String,
    tty: String,
    context: String,
}

#[derive(Serialize, Clone)]
struct DiskItem {
    #[serde(rename = "Path")]
    path: String,
    #[serde(rename = "Avail")]
    avail: String,
    #[serde(rename = "Size")]
    size: String,
}

#[derive(Serialize, Clone)]
struct SysStats {
    cpu_pct: f32,
    mem_used: u64,
    mem_total: u64,
    swap_used: u64,
    swap_total: u64,
    uptime: String,
    load: String,
    rx_speed: u64,
    tx_speed: u64,
    iface: String,
    processes: Vec<ProcessItem>,
    disks: Vec<DiskItem>,
}

fn format_size(bytes: u64) -> String {
    let kb = bytes as f64 / 1024.0;
    let mb = kb / 1024.0;
    let gb = mb / 1024.0;
    if gb >= 1.0 { format!("{:.1}G", gb) }
    else if mb >= 1.0 { format!("{:.1}M", mb) }
    else { format!("{:.1}K", kb) }
}

fn format_uptime(secs: u64) -> String {
    let days = secs / 86400;
    let hours = (secs % 86400) / 3600;
    let mins = (secs % 3600) / 60;
    if days > 0 { format!("{} days, {:02}:{:02}", days, hours, mins) }
    else { format!("{:02}:{:02}", hours, mins) }
}

fn now_secs() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs() as i64
}

fn url_decode(s: &str) -> String {
    let mut res = String::new();
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c == '%' {
            let h1 = chars.next().unwrap_or('0');
            let h2 = chars.next().unwrap_or('0');
            if let Ok(b) = u8::from_str_radix(&format!("{}{}", h1, h2), 16) {
                res.push(b as char);
            }
        } else if c == '+' {
            res.push(' ');
        } else {
            res.push(c);
        }
    }
    res
}

// --- utmp parsing via utmp-rs ---

#[derive(Serialize, Clone)]
struct LoginRecord {
    user: String,
    ip: String,
    time: String,
    timestamp: i64,
}

fn extract_ip_from_host(host: &str) -> String {
    if host.is_empty() { return "(本地)".to_string(); }
    let trimmed = host.trim();
    let possible_ip = trimmed.split(|c: char| c == ' ' || c == ':').next().unwrap_or(trimmed);
    if possible_ip.parse::<std::net::IpAddr>().is_ok() {
        return possible_ip.to_string();
    }
    trimmed.to_string()
}

fn format_offset_datetime(dt: &time::OffsetDateTime) -> String {
    format!("{}-{:02}-{:02} {:02}:{:02}:{:02}",
        dt.year(), u8::from(dt.month()), dt.day(),
        dt.hour(), dt.minute(), dt.second())
}

fn parse_utmp_file(path: &str, filter_user_process: bool, max_count: Option<usize>) -> Vec<LoginRecord> {
    let entries = match utmp_rs::parse_from_path(path) {
        Ok(e) => e,
        Err(_) => return Vec::new(),
    };

    let mut records = Vec::new();

    for entry in entries.into_iter().rev() {
        if let Some(max) = max_count {
            if records.len() >= max { break; }
        }

        let (user, host, dt) = match entry {
            utmp_rs::UtmpEntry::UserProcess { ref user, ref host, ref time, .. } => {
                (user.clone(), host.clone(), time.clone())
            }
            utmp_rs::UtmpEntry::LoginProcess { ref user, ref host, ref time, .. } => {
                if filter_user_process { continue; }
                (user.clone(), host.clone(), time.clone())
            }
            _ => continue,
        };

        let user_trimmed = user.trim().to_string();
        let host_trimmed = host.trim().to_string();

        if user_trimmed.is_empty() && host_trimmed.is_empty() { continue; }

        let ip = extract_ip_from_host(&host_trimmed);
        let timestamp = dt.unix_timestamp();
        let time_str = format_offset_datetime(&dt);

        records.push(LoginRecord {
            user: if user_trimmed.is_empty() { "(未知)".to_string() } else { user_trimmed },
            ip,
            time: time_str,
            timestamp,
        });
    }
    records
}

// --- systemd service list via D-Bus (zbus) ---

#[derive(Serialize, Clone)]
struct ServiceItem {
    name: String,
    description: String,
    active_state: String,
    sub_state: String,
    load_state: String,
    pid: u32,
    user: String,
    group: String,
}

fn resolve_uid(uid: u32, passwd_cache: &std::collections::HashMap<u32, String>) -> String {
    if uid == 0 { return "root".to_string(); }
    if let Some(name) = passwd_cache.get(&uid) { return name.clone(); }
    uid.to_string()
}

fn resolve_gid(gid: u32, group_cache: &std::collections::HashMap<u32, String>) -> String {
    if gid == 0 { return "root".to_string(); }
    if let Some(name) = group_cache.get(&gid) { return name.clone(); }
    gid.to_string()
}

fn load_passwd_cache() -> std::collections::HashMap<u32, String> {
    let mut map = std::collections::HashMap::new();
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

fn load_group_cache() -> std::collections::HashMap<u32, String> {
    let mut map = std::collections::HashMap::new();
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

async fn list_systemd_services() -> Result<Vec<ServiceItem>, Box<dyn std::error::Error>> {
    let connection = zbus::Connection::system().await?;
    let proxy = zbus::Proxy::new(
        &connection,
        "org.freedesktop.systemd1",
        "/org/freedesktop/systemd1",
        "org.freedesktop.systemd1.Manager",
    ).await?;

    // ListUnits returns: Vec<(name, description, load_state, active_state, sub_state, following, path, job_id, job_type, job_path)>
    let units: Vec<(String, String, String, String, String, String, zbus::zvariant::OwnedObjectPath, u32, String, zbus::zvariant::OwnedObjectPath)> =
        proxy.call_method("ListUnits", &()).await?.body().deserialize()?;

    let passwd_cache = load_passwd_cache();
    let group_cache = load_group_cache();

    let mut services = Vec::new();

    for u in units.iter() {
        if !u.0.ends_with(".service") { continue; }

        let (pid, user, group) = if u.3 == "active" {
            match get_service_main_pid(&connection, &u.6).await {
                Ok(p) if p > 0 => {
                    let (uid_val, gid_val) = read_pid_uid_gid(p);
                    (p, resolve_uid(uid_val, &passwd_cache), resolve_gid(gid_val, &group_cache))
                }
                Ok(p) => (p, String::new(), String::new()),
                Err(_) => (0, String::new(), String::new()),
            }
        } else {
            (0, String::new(), String::new())
        };

        services.push(ServiceItem {
            name: u.0.clone(),
            description: u.1.clone(),
            load_state: u.2.clone(),
            active_state: u.3.clone(),
            sub_state: u.4.clone(),
            pid,
            user,
            group,
        });
    }

    Ok(services)
}

async fn get_service_main_pid(connection: &zbus::Connection, path: &zbus::zvariant::OwnedObjectPath) -> Result<u32, Box<dyn std::error::Error>> {
    let proxy = zbus::Proxy::new(
        connection,
        "org.freedesktop.systemd1",
        path.as_str(),
        "org.freedesktop.DBus.Properties",
    ).await?;

    let reply = proxy.call_method("Get", &("org.freedesktop.systemd1.Service", "MainPID")).await?;
    let body = reply.body();
    let pid: zbus::zvariant::OwnedValue = body.deserialize()?;
    match &*pid {
        zbus::zvariant::Value::U32(p) => Ok(*p),
        zbus::zvariant::Value::I32(p) => Ok(*p as u32),
        _ => Ok(0),
    }
}

fn read_pid_uid_gid(pid: u32) -> (u32, u32) {
    let status_path = format!("/proc/{}/status", pid);
    let content = match fs::read_to_string(&status_path) { Ok(c) => c, Err(_) => return (0, 0) };
    let mut uid: u32 = 0;
    let mut gid: u32 = 0;
    for line in content.lines() {
        if line.starts_with("Uid:") {
            // Uid: 1000 1000 1000 1000  — take the first (real UID)
            uid = line[4..].split_whitespace().next().and_then(|v| v.parse().ok()).unwrap_or(0);
        }
        if line.starts_with("Gid:") {
            gid = line[4..].split_whitespace().next().and_then(|v| v.parse().ok()).unwrap_or(0);
        }
    }
    (uid, gid)
}

async fn do_service_action(name: &str, action: &str) -> Result<bool, Box<dyn std::error::Error>> {
    let connection = zbus::Connection::system().await?;
    let proxy = zbus::Proxy::new(
        &connection,
        "org.freedesktop.systemd1",
        "/org/freedesktop/systemd1",
        "org.freedesktop.systemd1.Manager",
    ).await?;

    let method = match action {
        "start" => "StartUnit",
        "stop" => "StopUnit",
        "restart" => "RestartUnit",
        _ => return Ok(false),
    };

    // method signature: (name: String, mode: String) -> o (object path)
    let _: zbus::zvariant::OwnedObjectPath = proxy.call_method(method, &(name, "replace")).await?.body().deserialize()?;
    Ok(true)
}

fn get_systemd_services() -> Vec<ServiceItem> {
    let rt = tokio::runtime::Runtime::new().unwrap();
    rt.block_on(async {
        list_systemd_services().await.unwrap_or_default()
    })
}

fn service_action(name: &str, action: &str) -> bool {
    let rt = tokio::runtime::Runtime::new().unwrap();
    rt.block_on(async {
        do_service_action(name, action).await.unwrap_or(false)
    })
}

fn get_service_log(name: &str) -> String {
    std::process::Command::new("journalctl")
        .args(["-u", name, "-n", "50", "--no-pager"])
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).to_string())
        .unwrap_or_else(|_| "Failed to read journal".to_string())
}

fn get_process_detail(pid_val: u32) -> Option<ProcessDetail> {
    let pid_str = pid_val.to_string();
    let proc_path = format!("/proc/{}", pid_str);
    if !Path::new(&proc_path).exists() { return None; }

    let status_content = fs::read_to_string(format!("{}/status", proc_path)).unwrap_or_default();
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

fn main() {
    let args: Vec<String> = env::args().collect();
    let port: u16 = args.get(1).and_then(|p| p.parse().ok()).unwrap_or(45678);
    let token_file = args.get(2);
    
    let token = if let Some(path) = token_file {
        let t = fs::read_to_string(path).unwrap_or_default().trim().to_string();
        let _ = fs::remove_file(path);
        if t.is_empty() { None } else { Some(t) }
    } else {
        None
    };

    let server = Server::http(format!("127.0.0.1:{}", port)).expect("Binding failed");
    
    let stats_ref = Arc::new(Mutex::new(None::<SysStats>));
    let all_procs_ref = Arc::new(Mutex::new(Vec::<ProcessItem>::new()));
    let last_request = Arc::new(AtomicI64::new(now_secs()));

    let stats_clone = stats_ref.clone();
    let all_procs_clone = all_procs_ref.clone();
    let last_req_clone = last_request.clone();
    
    thread::spawn(move || {
        let mut sys = System::new_all();
        let mut networks = Networks::new_with_refreshed_list();
        let mut disks = Disks::new_with_refreshed_list();

        loop {
            thread::sleep(Duration::from_secs(1));
            let idle_secs = now_secs() - last_req_clone.load(Ordering::Relaxed);
            if idle_secs > 30 { std::process::exit(0); }

            sys.refresh_all();
            networks.refresh();
            disks.refresh();

            let cpu_pct = sys.global_cpu_info().cpu_usage();
            let mem_total = sys.total_memory();
            
            let mut all_processes = Vec::new();
            let mut procs: Vec<_> = sys.processes().iter().collect();
            
            procs.sort_by(|a, b| {
                let cpu_cmp = b.1.cpu_usage().partial_cmp(&a.1.cpu_usage()).unwrap_or(std::cmp::Ordering::Equal);
                if cpu_cmp == std::cmp::Ordering::Equal {
                    a.0.as_u32().cmp(&b.0.as_u32())
                } else { cpu_cmp }
            });
            
            for (pid, p) in procs.iter() {
                let cmd = p.cmd().join(" ");
                let p_mem = format!("{:.1}%", (p.memory() as f32 / mem_total as f32) * 100.0);
                all_processes.push(ProcessItem {
                    pid: pid.as_u32(),
                    user: p.user_id().map(|u| u.to_string()).unwrap_or_else(|| "root".to_string()),
                    mem: p_mem,
                    cpu: format!("{:.1}%", p.cpu_usage()),
                    file: p.exe().map(|e| e.to_string_lossy().into_owned()).unwrap_or_default(),
                    cmd: if cmd.is_empty() { p.name().to_string() } else { cmd },
                });
            }

            let top_15 = all_processes.iter().take(15).cloned().collect();

            let mut best_rx = 0;
            let mut best_tx = 0;
            let mut best_iface = String::from("eth0");
            for (iface, data) in &networks {
                let rx = data.received();
                let tx = data.transmitted();
                if !iface.contains("lo") && rx + tx > best_rx + best_tx {
                    best_rx = rx; best_tx = tx; best_iface = iface.clone();
                }
            }

            let new_stats = SysStats {
                cpu_pct,
                mem_used: sys.used_memory(),
                mem_total,
                swap_used: sys.used_swap(),
                swap_total: sys.total_swap(),
                uptime: format_uptime(System::uptime()),
                load: System::load_average().one.to_string(),
                rx_speed: best_rx,
                tx_speed: best_tx,
                iface: best_iface,
                processes: top_15,
                disks: disks.iter().map(|d| DiskItem { 
                    path: d.mount_point().to_string_lossy().to_string(), 
                    avail: format_size(d.available_space()), 
                    size: format_size(d.total_space()) 
                }).collect(),
            };

            if let Ok(mut g) = stats_clone.lock() { *g = Some(new_stats); }
            if let Ok(mut g) = all_procs_clone.lock() { *g = all_processes; }
        }
    });

    for request in server.incoming_requests() {
        last_request.store(now_secs(), Ordering::Relaxed);
        
        // Token Verification
        if let Some(ref t) = token {
            let header_token = request.headers().iter()
                .find(|h| h.field.as_str() == "X-Monitor-Token")
                .map(|h| h.value.as_str());
            
            if header_token != Some(t) {
                let _ = request.respond(Response::from_string("Unauthorized").with_status_code(401));
                continue;
            }
        }

        let url = request.url().to_string();
        
        let response = if url == "/stats" {
            let data = {
                let g = stats_ref.lock().unwrap();
                serde_json::to_string(&*g).unwrap_or_else(|_| "{}".to_string())
            };
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url == "/all_processes" {
            let data = {
                let g = all_procs_ref.lock().unwrap();
                serde_json::to_string(&*g).unwrap_or_else(|_| "[]".to_string())
            };
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url.starts_with("/process_detail") {
            let pid = url.split('=').last().and_then(|p| p.parse::<u32>().ok()).unwrap_or(0);
            let data = serde_json::to_string(&get_process_detail(pid)).unwrap_or_else(|_| "null".to_string());
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url.starts_with("/killall") {
            let parts: Vec<&str> = url.split('?').nth(1).unwrap_or("").split('&').collect();
            let mut path_encoded = String::new();
            let mut sig = 15;
            for p in parts {
                if p.starts_with("path=") { path_encoded = p[5..].to_string(); }
                if p.starts_with("sig=") { sig = p[4..].parse::<i32>().unwrap_or(15); }
            }
            let path = url_decode(&path_encoded);
            let proc_name = Path::new(&path).file_name().and_then(|n| n.to_str()).unwrap_or(&path);
            let success = std::process::Command::new("killall").arg(format!("-{}", sig)).arg(proc_name).status().map(|s| s.success()).unwrap_or(false);
            Response::from_string(success.to_string())
        } else if url.starts_with("/kill") {
            let parts: Vec<&str> = url.split('?').nth(1).unwrap_or("").split('&').collect();
            let mut pid = 0;
            let mut sig = 15;
            for p in parts {
                if p.starts_with("pid=") { pid = p[4..].parse::<u32>().unwrap_or(0); }
                if p.starts_with("sig=") { sig = p[4..].parse::<i32>().unwrap_or(15); }
            }
            let success = std::process::Command::new("kill").arg(format!("-{}", sig)).arg(pid.to_string()).status().map(|s| s.success()).unwrap_or(false);
            Response::from_string(success.to_string())
        } else if url.starts_with("/wtmp") {
            let count: usize = url.split("count=").last().and_then(|c| c.parse().ok()).unwrap_or(0);
            let records = parse_utmp_file("/var/log/wtmp", true, if count > 0 { Some(count) } else { None });
            let data = serde_json::to_string(&records).unwrap_or_else(|_| "[]".to_string());
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url.starts_with("/btmp") {
            let count: usize = url.split("count=").last().and_then(|c| c.parse().ok()).unwrap_or(100);
            let records = parse_utmp_file("/var/log/btmp", false, Some(count));
            let data = serde_json::to_string(&records).unwrap_or_else(|_| "[]".to_string());
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url == "/services" {
            let services = get_systemd_services();
            let data = serde_json::to_string(&services).unwrap_or_else(|_| "[]".to_string());
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url.starts_with("/service_start") {
            let name = url_decode(url.split("name=").last().unwrap_or(""));
            let success = service_action(&name, "start");
            Response::from_string(success.to_string())
        } else if url.starts_with("/service_stop") {
            let name = url_decode(url.split("name=").last().unwrap_or(""));
            let success = service_action(&name, "stop");
            Response::from_string(success.to_string())
        } else if url.starts_with("/service_restart") {
            let name = url_decode(url.split("name=").last().unwrap_or(""));
            let success = service_action(&name, "restart");
            Response::from_string(success.to_string())
        } else if url.starts_with("/service_log") {
            let name = url_decode(url.split("name=").last().unwrap_or(""));
            let log = get_service_log(&name);
            Response::from_string(log).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"text/plain; charset=utf-8"[..]).unwrap())
        } else {
            Response::from_string("Not Found").with_status_code(404)
        };
        let _ = request.respond(response);
    }
}
