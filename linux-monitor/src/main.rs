mod models;
mod utils;
mod utmp_parser;
mod service_manager;
mod stats_collector;
mod cron_manager;

use models::{SysStats, ProcessItem, DiskItem};
use utils::{format_size, format_uptime, now_secs, url_decode};
use utmp_parser::parse_utmp_file;
use service_manager::{get_systemd_services, service_action, get_service_log};
use stats_collector::{get_process_detail, get_net_conns};
use cron_manager::{list_cron_jobs, add_cron_job, remove_cron_job, toggle_cron_job, get_cron_service_status};

use sysinfo::{Disks, Networks, System};
use tiny_http::{Response, Server};
use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicI64, Ordering};
use std::thread;
use std::time::Duration;
use std::env;
use std::fs;
use std::path::Path;

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
            let data = serde_json::to_string(&services).unwrap_or_else(|_| "[]".to_string()) ;
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url == "/net_conns" {
            let conns = get_net_conns();
            let data = serde_json::to_string(&conns).unwrap_or_else(|_| "[]".to_string());
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
        } else if url == "/cron_list" {
            let jobs = list_cron_jobs();
            let data = serde_json::to_string(&jobs).unwrap_or_else(|_| "[]".to_string());
            Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap())
        } else if url.starts_with("/cron_add") {
            let raw = url_decode(url.split("raw=").last().unwrap_or(""));
            let success = add_cron_job(&raw);
            Response::from_string(success.to_string())
        } else if url.starts_with("/cron_remove") {
            let line = url.split("line=").last().and_then(|p| p.parse::<usize>().ok()).unwrap_or(0);
            let success = remove_cron_job(line);
            Response::from_string(success.to_string())
        } else if url.starts_with("/cron_toggle") {
            let parts: Vec<&str> = url.split('?').nth(1).unwrap_or("").split('&').collect();
            let mut line = 0usize;
            let mut enabled = true;
            for p in parts {
                if p.starts_with("line=") { line = p[5..].parse::<usize>().unwrap_or(0); }
                if p.starts_with("enabled=") { enabled = p[8..].parse::<bool>().unwrap_or(true); }
            }
            let success = toggle_cron_job(line, enabled);
            Response::from_string(success.to_string())
        } else if url == "/cron_status" {
            let status = get_cron_service_status();
            Response::from_string(status).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"text/plain; charset=utf-8"[..]).unwrap())
        } else {
            Response::from_string("Not Found").with_status_code(404)
        };
        let _ = request.respond(response);
    }
}
