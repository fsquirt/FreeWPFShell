use crate::models::{ProcessDetail, ProcessItem};
use std::fs;
use std::path::Path;

pub fn get_process_detail(pid_val: u32) -> Option<ProcessDetail> {
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
