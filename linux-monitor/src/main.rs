mod models;
mod utils;
mod utmp_parser;
mod service_manager;
mod stats_collector;
mod cron_manager;

use models::{SysStats, ProcessItem, DiskItem};
use utils::{format_size, format_uptime, now_secs, now_secs_ms};
use utmp_parser::parse_utmp_file;
use service_manager::{get_systemd_services, service_action, get_service_log};
use stats_collector::{get_process_detail, get_net_conns};
use cron_manager::{list_cron_jobs, add_cron_job, remove_cron_job, toggle_cron_job, get_cron_service_status};

use axum::{
    Router,
    body::Bytes,
    extract::{Query, State, Request as AxumRequest, DefaultBodyLimit},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::{get, post},
    middleware::{self, Next},
};
use sysinfo::{Disks, Networks, System, RefreshKind, CpuRefreshKind, ProcessRefreshKind, MemoryRefreshKind, UpdateKind};
use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicI64, Ordering};
use std::thread;
use std::time::Duration;
use std::env;
use std::fs;
use std::fs::OpenOptions;
use std::os::unix::fs::FileExt;
use std::path::Path;
use std::collections::HashMap;

// ── 应用共享状态 ──────────────────────────────────────────
struct AppState {
    stats: Arc<Mutex<Option<SysStats>>>,
    all_procs: Arc<Mutex<Vec<ProcessItem>>>,
    token: Option<String>,
    last_request: Arc<AtomicI64>,
}

// ── 多线程分段文件读写（通过 HTTP 接口供隧道并行传输） ─────────

fn file_read_at(path: &str, offset: u64, len: u64) -> Vec<u8> {
    let f = match OpenOptions::new().read(true).open(path) {
        Ok(f) => f,
        Err(_) => return Vec::new(),
    };
    let mut buf = vec![0u8; len as usize];
    match f.read_at(&mut buf, offset) {
        Ok(n) => { buf.truncate(n); buf }
        Err(_) => Vec::new()
    }
}

fn file_write_at(path: &str, offset: u64, data: &[u8]) -> bool {
    let f = match OpenOptions::new().write(true).create(true).open(path) {
        Ok(f) => f,
        Err(_) => return false,
    };
    let mut written = 0usize;
    while written < data.len() {
        match f.write_at(&data[written..], offset + written as u64) {
            Ok(0) => return false,
            Ok(n) => written += n,
            Err(_) => return false,
        }
    }
    true
}

fn file_truncate(path: &str) -> bool {
    OpenOptions::new().write(true).create(true).truncate(true).open(path).is_ok()
}

/// 追加一行日志到远程 /tmp/YouShell/lm.log（含秒级时间戳）。失败静默忽略。
fn log_to_file(msg: &str) {
    use std::io::Write;
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open("/tmp/YouShell/lm.log") {
        let _ = writeln!(f, "{} {}", now_secs_ms(), msg);
    }
}

// ── 中间件：记录最近请求时间（空闲超时退出用） ─────────────
async fn touch_last_request(State(state): State<Arc<AppState>>, req: AxumRequest, next: Next) -> Response {
    state.last_request.store(now_secs(), Ordering::Relaxed);
    next.run(req).await
}

// ── Token 校验 helper ────────────────────────────────────
fn auth_failed(state: &AppState, headers: &HeaderMap) -> bool {
    if let Some(tok) = &state.token {
        let provided = headers
            .get("x-monitor-token")
            .and_then(|v| v.to_str().ok())
            .unwrap_or("");
        return provided != tok;
    }
    false
}

// ── Handler：文件分段读写（隧道路径，性能关键） ────────────

async fn handle_file_read(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let path = q.get("path").cloned().unwrap_or_default();
    let offset = q.get("offset").and_then(|v| v.parse().ok()).unwrap_or(0);
    let len = q.get("len").and_then(|v| v.parse().ok()).unwrap_or(0);

    let t0 = now_secs_ms();
    let data = match thread::Builder::new().name("file_read".into()).spawn(move || file_read_at(&path, offset, len)) {
        Ok(h) => h.join().unwrap_or_default(),
        Err(_) => Vec::new(),
    };
    let t1 = now_secs_ms();
    let msg = format!("file_read offset={} len={} read_took={}ms got={}B", offset, len, t1 - t0, data.len());
    eprintln!("[lm] {}", msg);
    log_to_file(&msg);
    (StatusCode::OK, data).into_response()
}

async fn handle_file_write(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>, body: Bytes) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let path = q.get("path").cloned().unwrap_or_default();
    let offset = q.get("offset").and_then(|v| v.parse().ok()).unwrap_or(0);
    let data = body.to_vec();
    let data_len = data.len();

    let t0 = now_secs_ms();
    let ok = match thread::Builder::new().name("file_write".into()).spawn(move || file_write_at(&path, offset, &data)) {
        Ok(h) => h.join().unwrap_or(false),
        Err(_) => false,
    };
    let t1 = now_secs_ms();
    let msg = format!("file_write offset={} len={} write_took={}ms ok={}", offset, data_len, t1 - t0, ok);
    eprintln!("[lm] {}", msg);
    log_to_file(&msg);
    (StatusCode::OK, ok.to_string()).into_response()
}

async fn handle_file_truncate(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let path = q.get("path").cloned().unwrap_or_default();

    let t0 = now_secs_ms();
    let ok = match thread::Builder::new().name("file_truncate".into()).spawn(move || file_truncate(&path)) {
        Ok(h) => h.join().unwrap_or(false),
        Err(_) => false,
    };
    let t1 = now_secs_ms();
    let msg = format!("file_truncate took={}ms ok={}", t1 - t0, ok);
    eprintln!("[lm] {}", msg);
    log_to_file(&msg);
    (StatusCode::OK, ok.to_string()).into_response()
}

// ── Handler：监控数据 ─────────────────────────────────────

async fn handle_stats(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let data = {
        let g = state.stats.lock().unwrap();
        serde_json::to_string(&*g).unwrap_or_else(|_| "{}".to_string())
    };
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_all_processes(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let data = {
        let g = state.all_procs.lock().unwrap();
        serde_json::to_string(&*g).unwrap_or_else(|_| "[]".to_string())
    };
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_process_detail(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let pid = q.get("pid").and_then(|p| p.parse::<u32>().ok()).unwrap_or(0);
    let data = serde_json::to_string(&get_process_detail(pid)).unwrap_or_else(|_| "null".to_string());
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_wtmp(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let count = q.get("count").and_then(|c| c.parse().ok()).unwrap_or(0);
    let records = parse_utmp_file("/var/log/wtmp", true, if count > 0 { Some(count) } else { None });
    let data = serde_json::to_string(&records).unwrap_or_else(|_| "[]".to_string());
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_btmp(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let count = q.get("count").and_then(|c| c.parse().ok()).unwrap_or(100);
    let records = parse_utmp_file("/var/log/btmp", false, Some(count));
    let data = serde_json::to_string(&records).unwrap_or_else(|_| "[]".to_string());
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_services(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let services = get_systemd_services();
    let data = serde_json::to_string(&services).unwrap_or_else(|_| "[]".to_string());
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_net_conns(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let conns = get_net_conns();
    let data = serde_json::to_string(&conns).unwrap_or_else(|_| "[]".to_string());
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_service_start(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let name = q.get("name").cloned().unwrap_or_default();
    let success = service_action(&name, "start");
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_service_stop(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let name = q.get("name").cloned().unwrap_or_default();
    let success = service_action(&name, "stop");
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_service_restart(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let name = q.get("name").cloned().unwrap_or_default();
    let success = service_action(&name, "restart");
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_service_log(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let name = q.get("name").cloned().unwrap_or_default();
    let log = get_service_log(&name);
    (StatusCode::OK, log).into_response()
}

async fn handle_killall(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let path = q.get("path").cloned().unwrap_or_default();
    let sig = q.get("sig").and_then(|s| s.parse::<i32>().ok()).unwrap_or(15);
    let proc_name = Path::new(&path).file_name().and_then(|n| n.to_str()).unwrap_or(&path);
    let success = std::process::Command::new("killall").arg(format!("-{}", sig)).arg(proc_name).status().map(|s| s.success()).unwrap_or(false);
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_kill(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let pid = q.get("pid").and_then(|p| p.parse::<u32>().ok()).unwrap_or(0);
    let sig = q.get("sig").and_then(|s| s.parse::<i32>().ok()).unwrap_or(15);
    let success = std::process::Command::new("kill").arg(format!("-{}", sig)).arg(pid.to_string()).status().map(|s| s.success()).unwrap_or(false);
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_cron_list(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let jobs = list_cron_jobs();
    let data = serde_json::to_string(&jobs).unwrap_or_else(|_| "[]".to_string());
    (StatusCode::OK, axum::Json(data)).into_response()
}

async fn handle_cron_add(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let raw = q.get("raw").cloned().unwrap_or_default();
    let success = add_cron_job(&raw);
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_cron_remove(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let line = q.get("line").and_then(|p| p.parse::<usize>().ok()).unwrap_or(0);
    let success = remove_cron_job(line);
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_cron_toggle(State(state): State<Arc<AppState>>, headers: HeaderMap, Query(q): Query<HashMap<String, String>>) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let line = q.get("line").and_then(|p| p.parse::<usize>().ok()).unwrap_or(0);
    let enabled = q.get("enabled").and_then(|e| e.parse::<bool>().ok()).unwrap_or(true);
    let success = toggle_cron_job(line, enabled);
    (StatusCode::OK, success.to_string()).into_response()
}

async fn handle_cron_status(State(state): State<Arc<AppState>>, headers: HeaderMap) -> Response {
    if auth_failed(&state, &headers) { return (StatusCode::UNAUTHORIZED, "Unauthorized").into_response(); }
    let status = get_cron_service_status();
    (StatusCode::OK, status).into_response()
}

#[tokio::main]
async fn main() {
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

    let stats = Arc::new(Mutex::new(None::<SysStats>));
    let all_procs = Arc::new(Mutex::new(Vec::<ProcessItem>::new()));
    let last_request = Arc::new(AtomicI64::new(now_secs()));

    // 后台统计采集线程
    {
        let stats_clone = stats.clone();
        let all_procs_clone = all_procs.clone();
        let last_req_clone = last_request.clone();
        let _ = thread::Builder::new().name("stats".into()).spawn(move || {
            let mut sys = System::new_with_specifics(
                RefreshKind::new()
                    .with_cpu(CpuRefreshKind::everything())
                    .with_memory(MemoryRefreshKind::everything())
                    .with_processes(ProcessRefreshKind::new().with_cpu().with_memory().with_exe(UpdateKind::Always).with_cmd(UpdateKind::Always))
            );
            let mut networks = Networks::new_with_refreshed_list();
            let mut disks = Disks::new_with_refreshed_list();
            let mut all_processes = Vec::new();
            let mut disk_items = Vec::new();

            loop {
                thread::sleep(Duration::from_secs(1));
                let idle_secs = now_secs() - last_req_clone.load(Ordering::Relaxed);
                if idle_secs > 30 { std::process::exit(0); }

                sys.refresh_specifics(
                    RefreshKind::new()
                        .with_cpu(CpuRefreshKind::everything())
                        .with_memory(MemoryRefreshKind::everything())
                        .with_processes(ProcessRefreshKind::new().with_cpu().with_memory().with_exe(UpdateKind::Always).with_cmd(UpdateKind::Always))
                );
                networks.refresh();
                disks.refresh();

                let cpu_pct = sys.global_cpu_info().cpu_usage();
                let mem_total = sys.total_memory();

                all_processes.clear();
                {
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
                }

                let top_15: Vec<ProcessItem> = all_processes.iter().take(15).cloned().collect();

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

                disk_items.clear();
                disk_items.extend(disks.iter().map(|d| DiskItem {
                    path: d.mount_point().to_string_lossy().to_string(),
                    avail: format_size(d.available_space()),
                    size: format_size(d.total_space())
                }));

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
                    disks: disk_items.clone(),
                };

                if let Ok(mut g) = stats_clone.lock() { *g = Some(new_stats); }
                if let Ok(mut g) = all_procs_clone.lock() { std::mem::swap(&mut *g, &mut all_processes); }
            }
        });
    }

    let state = Arc::new(AppState {
        stats,
        all_procs,
        token,
        last_request: last_request.clone(),
    });

    let app = Router::new()
        .route("/file_read", get(handle_file_read))
        .route("/file_write", post(handle_file_write))
        .route("/file_truncate", get(handle_file_truncate))
        .route("/stats", get(handle_stats))
        .route("/all_processes", get(handle_all_processes))
        .route("/process_detail", get(handle_process_detail))
        .route("/wtmp", get(handle_wtmp))
        .route("/btmp", get(handle_btmp))
        .route("/services", get(handle_services))
        .route("/net_conns", get(handle_net_conns))
        .route("/service_start", get(handle_service_start))
        .route("/service_stop", get(handle_service_stop))
        .route("/service_restart", get(handle_service_restart))
        .route("/service_log", get(handle_service_log))
        .route("/killall", get(handle_killall))
        .route("/kill", get(handle_kill))
        .route("/cron_list", get(handle_cron_list))
        .route("/cron_add", get(handle_cron_add))
        .route("/cron_remove", get(handle_cron_remove))
        .route("/cron_toggle", get(handle_cron_toggle))
        .route("/cron_status", get(handle_cron_status))
        // 解除请求体大小限制：分块上传每块可达数 MB，axum 默认 2MB 会返回 413。
        .layer(DefaultBodyLimit::max(256 * 1024 * 1024))
        .layer(middleware::from_fn_with_state(state.clone(), touch_last_request))
        .with_state(state);

    let addr = format!("127.0.0.1:{}", port);
    let listener = tokio::net::TcpListener::bind(&addr).await.expect("Binding failed");
    eprintln!("[lm] axum listening on {}", addr);
    axum::serve(listener, app).await.unwrap();
}
