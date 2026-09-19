mod json;
mod models;
mod utils;
mod utmp_parser;
mod service_manager;
mod stats_collector;
mod cron_manager;

use json::Json;
use models::{ProcessItem, json_array};
use utils::now_secs;
use utmp_parser::parse_utmp_file;
use service_manager::{get_systemd_services, service_action, get_service_log};
use stats_collector::{get_process_detail, get_net_conns, Collector};
use cron_manager::{list_cron_jobs, add_cron_job, remove_cron_job, toggle_cron_job, get_cron_service_status};

use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicI64, Ordering};
use std::thread;
use std::time::Duration;
use std::env;
use std::fs;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::Path;


const MAX_FRAME_LEN: usize = 10 * 1024 * 1024;

const READ_TIMEOUT: Duration = Duration::from_secs(15);

fn read_frame(stream: &mut TcpStream) -> std::io::Result<Vec<u8>> {
    let mut len_buf = [0u8; 4];
    stream.read_exact(&mut len_buf)?;
    let len = u32::from_be_bytes(len_buf) as usize;
    if len == 0 || len > MAX_FRAME_LEN {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "invalid frame length"));
    }
    let mut buf = vec![0u8; len];
    stream.read_exact(&mut buf)?;
    Ok(buf)
}

fn write_frame(stream: &mut TcpStream, payload: &[u8]) -> std::io::Result<()> {
    stream.write_all(&(payload.len() as u32).to_be_bytes())?;
    stream.write_all(payload)?;
    stream.flush()
}

fn get_str<'a>(v: &'a Json, key: &str) -> Option<&'a str> {
    v.get(key).and_then(|x| x.as_str())
}

fn get_u64(v: &Json, key: &str) -> Option<u64> {
    v.get(key).and_then(|x| x.as_u64())
}

fn ok_body(success: bool) -> Vec<u8> {
    success.to_string().into_bytes()
}


fn handle_conn(
    mut stream: TcpStream,
    token: &Option<String>,
    stats_ref: &Arc<Mutex<Option<models::SysStats>>>,
    all_procs_ref: &Arc<Mutex<Vec<ProcessItem>>>,
) {
    let _ = stream.set_read_timeout(Some(READ_TIMEOUT));
    let _ = stream.set_nodelay(true);

    let req = match read_frame(&mut stream) {
        Ok(r) => r,
        Err(_) => return,
    };
    let envelope = match json::parse(&req) {
        Ok(v) => v,
        Err(_) => {
            let _ = write_frame(&mut stream, b"{\"err\":\"bad request\"}");
            return;
        }
    };


    if let Some(t) = token {
        if get_str(&envelope, "token") != Some(t.as_str()) {
            let _ = write_frame(&mut stream, b"{\"err\":\"unauthorized\"}");
            return;
        }
    }

    let op = get_str(&envelope, "op").unwrap_or("");

    let body: Vec<u8> = match op {

        "exit" => std::process::exit(0),
        "stats" => {
            let g = stats_ref.lock().unwrap();
            match &*g {
                Some(s) => s.json().into_bytes(),
                None => b"null".to_vec(),
            }
        }
        "all_processes" => {
            let g = all_procs_ref.lock().unwrap();
            json_array(&g, |p| p.json()).into_bytes()
        }
        "process_detail" => {
            let pid = get_u64(&envelope, "pid").unwrap_or(0) as u32;
            get_process_detail(pid)
                .map(|d| d.json())
                .unwrap_or_else(|| "null".to_string())
                .into_bytes()
        }
        "killall" => {
            let path = get_str(&envelope, "path").unwrap_or("");
            let sig = get_u64(&envelope, "sig").unwrap_or(15) as i32;

            let mut targets: Vec<u32> = Vec::new();
            if !path.is_empty() {
                if let Ok(entries) = fs::read_dir("/proc") {
                    for entry in entries.flatten() {
                        let pid: u32 = match entry.file_name().to_string_lossy().parse() {
                            Ok(p) => p,
                            Err(_) => continue,
                        };
                        if let Ok(exe) = fs::read_link(format!("/proc/{}/exe", pid)) {
                            if exe.to_string_lossy() == path {
                                targets.push(pid);
                            }
                        }
                    }
                }
            }
            let success = if !targets.is_empty() {
                for pid in targets {
                    let _ = std::process::Command::new("kill").arg(format!("-{}", sig)).arg(pid.to_string()).status();
                }
                true
            } else {

                let proc_name = Path::new(path).file_name().and_then(|n| n.to_str()).unwrap_or(path);
                std::process::Command::new("killall").arg(format!("-{}", sig)).arg(proc_name).status().map(|s| s.success()).unwrap_or(false)
            };
            ok_body(success)
        }
        "kill" => {
            let pid = get_u64(&envelope, "pid").unwrap_or(0) as u32;
            let sig = get_u64(&envelope, "sig").unwrap_or(15) as i32;
            let success = std::process::Command::new("kill").arg(format!("-{}", sig)).arg(pid.to_string()).status().map(|s| s.success()).unwrap_or(false);
            ok_body(success)
        }
        "login_records" => {

            let kind = get_str(&envelope, "kind").unwrap_or("wtmp");
            let count = get_u64(&envelope, "count").unwrap_or(if kind == "btmp" { 100 } else { 0 }) as usize;
            let (path, only_good) = if kind == "btmp" { ("/var/log/btmp", false) } else { ("/var/log/wtmp", true) };
            let records = parse_utmp_file(path, only_good, if count > 0 { Some(count) } else { None });
            json_array(&records, |r| r.json()).into_bytes()
        }
        "services" => {
            let services = get_systemd_services();
            json_array(&services, |s| s.json()).into_bytes()
        }
        "net_conns" => {
            let conns = get_net_conns();
            json_array(&conns, |c| c.json()).into_bytes()
        }
        "service_action" => {
            let name = get_str(&envelope, "name").unwrap_or("");
            let action = get_str(&envelope, "action").unwrap_or("start");
            ok_body(service_action(name, action))
        }
        "service_log" => {
            let name = get_str(&envelope, "name").unwrap_or("");
            get_service_log(name).into_bytes()
        }
        "cron_list" => {
            let jobs = list_cron_jobs();
            json_array(&jobs, |j| j.json()).into_bytes()
        }
        "cron_add" => {
            let raw = get_str(&envelope, "raw").unwrap_or("");
            ok_body(add_cron_job(raw))
        }
        "cron_remove" => {
            let line = get_u64(&envelope, "line").unwrap_or(0) as usize;
            ok_body(remove_cron_job(line))
        }
        "cron_toggle" => {
            let line = get_u64(&envelope, "line").unwrap_or(0) as usize;
            let enabled = envelope.get("enabled").and_then(|x| x.as_bool()).unwrap_or(true);
            ok_body(toggle_cron_job(line, enabled))
        }
        "cron_status" => get_cron_service_status().into_bytes(),
        _ => b"{\"err\":\"not found\"}".to_vec(),
    };

    let _ = write_frame(&mut stream, &body);
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

    let listener = TcpListener::bind(("127.0.0.1", port)).expect("Binding failed");

    let stats_ref = Arc::new(Mutex::new(None::<models::SysStats>));
    let all_procs_ref = Arc::new(Mutex::new(Vec::<ProcessItem>::new()));
    let last_request = Arc::new(AtomicI64::new(now_secs()));

    let stats_clone = stats_ref.clone();
    let all_procs_clone = all_procs_ref.clone();
    let last_req_clone = last_request.clone();

    thread::spawn(move || {
        let mut collector = Collector::new();
        loop {
            thread::sleep(Duration::from_secs(1));

            let idle_secs = now_secs() - last_req_clone.load(Ordering::Relaxed);
            if idle_secs > 30 { std::process::exit(0); }

            let (stats, all_processes) = collector.collect();
            if let Ok(mut g) = stats_clone.lock() { *g = Some(stats); }
            if let Ok(mut g) = all_procs_clone.lock() { *g = all_processes; }
        }
    });

    for stream in listener.incoming() {

        last_request.store(now_secs(), Ordering::Relaxed);
        if let Ok(stream) = stream {
            let stats_c = stats_ref.clone();
            let procs_c = all_procs_ref.clone();
            let token_c = token.clone();
            thread::spawn(move || handle_conn(stream, &token_c, &stats_c, &procs_c));
        }
    }
}
