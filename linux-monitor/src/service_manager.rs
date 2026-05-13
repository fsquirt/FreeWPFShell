use crate::models::ServiceItem;
use std::fs;
use std::collections::HashMap;
use std::sync::OnceLock;
use tokio::runtime::Runtime;

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

pub async fn list_systemd_services() -> Result<Vec<ServiceItem>, Box<dyn std::error::Error>> {
    let connection = zbus::Connection::system().await?;
    let proxy = zbus::Proxy::new(
        &connection,
        "org.freedesktop.systemd1",
        "/org/freedesktop/systemd1",
        "org.freedesktop.systemd1.Manager",
    ).await?;

    let units: Vec<(String, String, String, String, String, String, zbus::zvariant::OwnedObjectPath, u32, String, zbus::zvariant::OwnedObjectPath)> =
        proxy.call_method("ListUnits", &()).await?.body().deserialize()?;

    let passwd_cache = get_passwd_cache();
    let group_cache = get_group_cache();

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

pub async fn get_service_main_pid(connection: &zbus::Connection, path: &zbus::zvariant::OwnedObjectPath) -> Result<u32, Box<dyn std::error::Error>> {
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

pub async fn do_service_action(name: &str, action: &str) -> Result<bool, Box<dyn std::error::Error>> {
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

    let _: zbus::zvariant::OwnedObjectPath = proxy.call_method(method, &(name, "replace")).await?.body().deserialize()?;
    Ok(true)
}

fn get_runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| tokio::runtime::Runtime::new().unwrap())
}

pub fn get_systemd_services() -> Vec<ServiceItem> {
    get_runtime().block_on(async {
        list_systemd_services().await.unwrap_or_default()
    })
}

pub fn service_action(name: &str, action: &str) -> bool {
    get_runtime().block_on(async {
        do_service_action(name, action).await.unwrap_or(false)
    })
}

pub fn get_service_log(name: &str) -> String {
    std::process::Command::new("journalctl")
        .args(["-u", name, "-n", "50", "--no-pager"])
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).to_string())
        .unwrap_or_else(|_| "Failed to read journal".to_string())
}
