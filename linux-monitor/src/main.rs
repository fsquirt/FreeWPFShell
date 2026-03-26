use serde::Serialize;
use sysinfo::{Disks, Networks, System};
use tiny_http::{Response, Server};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;
use std::env;

#[derive(Serialize, Clone)]
struct ProcessItem {
    #[serde(rename = "Mem")]
    mem: String,
    #[serde(rename = "Cpu")]
    cpu: String,
    #[serde(rename = "Cmd")]
    cmd: String,
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
    if gb >= 1.0 {
        format!("{:.1}G", gb)
    } else if mb >= 1.0 {
        format!("{:.1}M", mb)
    } else {
        format!("{:.1}K", kb)
    }
}

fn format_uptime(secs: u64) -> String {
    let days = secs / 86400;
    let hours = (secs % 86400) / 3600;
    let mins = (secs % 3600) / 60;
    if days > 0 {
        format!("{} days, {:02}:{:02}", days, hours, mins)
    } else {
        format!("{:02}:{:02}", hours, mins)
    }
}

fn main() {
    let port: u16 = env::args().nth(1).and_then(|p| p.parse().ok()).unwrap_or(45678);
    let server = Server::http(format!("127.0.0.1:{}", port)).expect("Binding failed");
    println!("Server running on 127.0.0.1:{}", port);

    let stats_ref = Arc::new(Mutex::new(None::<SysStats>));

    let stats_clone = stats_ref.clone();
    thread::spawn(move || {
        let mut sys = System::new_all();
        let mut networks = Networks::new_with_refreshed_list();
        let mut disks = Disks::new_with_refreshed_list();

        loop {
            // Wait 1 second before next refresh to compute metrics
            thread::sleep(Duration::from_secs(1));

            sys.refresh_cpu_usage();
            sys.refresh_memory();
            sys.refresh_processes();
            networks.refresh();
            
            // Only refresh disks every 60 iterations (like C# tick count logic) can be complex, so let's refresh them every 10 seconds or so.
            // But doing it every 1s is fine natively.
            disks.refresh();

            // Computations
            let cpu_pct = sys.global_cpu_info().cpu_usage();
            let mem_total = sys.total_memory();
            let mem_used = sys.used_memory();
            let swap_total = sys.total_swap();
            let swap_used = sys.used_swap();
            let uptime = format_uptime(System::uptime());
            
            let load = format!("{:.2}", System::load_average().one); // sysinfo exposes loadavg directly via System.
            // Use fallback "Unknown" for load if we don't have it.
            let load_str = System::load_average().one.to_string();

            // Network speeds
            let mut best_rx = 0;
            let mut best_tx = 0;
            let mut best_iface = String::from("eth0");
            
            for (iface, data) in &networks {
                let rx = data.received();
                let tx = data.transmitted();
                // Find the active interface (non-loopback with highest traffic)
                if !iface.contains("lo") && (rx > 0 || tx > 0) && rx + tx > best_rx + best_tx {
                    best_rx = rx;
                    best_tx = tx;
                    best_iface = iface.clone();
                }
            }

            // Processes
            let mut procs: Vec<_> = sys.processes().iter().collect();
            procs.sort_by(|a, b| b.1.cpu_usage().partial_cmp(&a.1.cpu_usage()).unwrap_or(std::cmp::Ordering::Equal));
            
            let mut processes = Vec::new();
            let mut seen_cmds = std::collections::HashSet::new();
            
            for (_pid, p) in procs.iter() {
                let mut cmd = p.cmd().join(" ");
                if cmd.is_empty() {
                    cmd = format!("[{}]", p.name());
                }
                if cmd.len() > 30 {
                    cmd = format!("{}...", &cmd[0..27]);
                }
                
                if seen_cmds.contains(&cmd) {
                    continue;
                }
                seen_cmds.insert(cmd.clone());

                let p_mem = format!("{:.1}%", (p.memory() as f32 / mem_total as f32) * 100.0);
                let p_cpu = format!("{:.1}%", p.cpu_usage());
                
                processes.push(ProcessItem {
                    mem: p_mem,
                    cpu: p_cpu,
                    cmd,
                });
                
                if processes.len() >= 10 {
                    break;
                }
            }

            // Disks
            let mut disk_list = Vec::new();
            for disk in &disks {
                let avail = format_size(disk.available_space());
                let size = format_size(disk.total_space());
                let path = disk.mount_point().to_string_lossy().to_string();
                disk_list.push(DiskItem { path, avail, size });
            }

            let new_stats = SysStats {
                cpu_pct,
                mem_used,
                mem_total,
                swap_used,
                swap_total,
                uptime,
                load: load_str, // Use system load average
                rx_speed: best_rx,
                tx_speed: best_tx,
                iface: best_iface,
                processes,
                disks: disk_list,
            };

            // Atomically update
            if let Ok(mut g) = stats_clone.lock() {
                *g = Some(new_stats);
            }
        }
    });

    for request in server.incoming_requests() {
        if request.url() == "/stats" {
            let data = {
                let g = stats_ref.lock().unwrap();
                if let Some(ref stats) = *g {
                    serde_json::to_string(stats).unwrap_or_else(|_| "{}".to_string())
                } else {
                    "{}".to_string()
                }
            };
            
            let response = Response::from_string(data).with_header(tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap());
            let _ = request.respond(response);
        } else {
            let response = Response::empty(404);
            let _ = request.respond(response);
        }
    }
}
