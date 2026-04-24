use std::process::{Command, Stdio};
use std::io::Write;
use serde::Serialize;

#[derive(Serialize, Clone)]
pub struct CronJobItem {
    pub line_index: usize,
    pub schedule: String,
    pub command: String,
    pub enabled: bool,
    pub raw: String,
}

fn read_crontab_lines() -> Vec<String> {
    match Command::new("crontab").arg("-l").output() {
        Ok(o) if o.status.success() => {
            String::from_utf8_lossy(&o.stdout)
                .lines()
                .map(|s| s.to_string())
                .collect()
        }
        _ => Vec::new(),
    }
}

fn write_crontab_lines(lines: &[String]) -> bool {
    let content = lines.join("\n") + "\n";
    let mut child = match Command::new("crontab").arg("-").stdin(Stdio::piped()).spawn() {
        Ok(c) => c,
        Err(_) => return false,
    };
    if let Some(mut stdin) = child.stdin.take() {
        let _ = stdin.write_all(content.as_bytes());
    }
    child.wait().map(|s| s.success()).unwrap_or(false)
}

fn is_cron_line(line: &str) -> bool {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return false;
    }
    if trimmed.starts_with('#') {
        let after = trimmed[1..].trim();
        if after.is_empty() || after.starts_with('#') {
            return false;
        }
        let parts: Vec<&str> = after.split_whitespace().collect();
        return parts.len() >= 5;
    }
    let parts: Vec<&str> = trimmed.split_whitespace().collect();
    parts.len() >= 5
}

pub fn list_cron_jobs() -> Vec<CronJobItem> {
    let lines = read_crontab_lines();
    let mut items = Vec::new();
    let mut idx = 0usize;
    for raw in lines {
        if !is_cron_line(&raw) {
            continue;
        }
        let trimmed = raw.trim();
        let enabled = !trimmed.starts_with('#');
        let content = if enabled {
            trimmed
        } else {
            trimmed[1..].trim()
        };
        let parts: Vec<&str> = content.split_whitespace().collect();
        if parts.len() >= 5 {
            let schedule = format!("{} {} {} {} {}", parts[0], parts[1], parts[2], parts[3], parts[4]);
            let command = parts[5..].join(" ");
            items.push(CronJobItem {
                line_index: idx,
                schedule,
                command,
                enabled,
                raw: raw.clone(),
            });
            idx += 1;
        }
    }
    items
}

pub fn add_cron_job(raw_line: &str) -> bool {
    let mut lines = read_crontab_lines();
    lines.push(raw_line.trim().to_string());
    write_crontab_lines(&lines)
}

pub fn remove_cron_job(line_index: usize) -> bool {
    let lines = read_crontab_lines();
    let mut result = Vec::new();
    let mut idx = 0usize;
    let mut found = false;
    for raw in lines {
        if is_cron_line(&raw) {
            if idx == line_index {
                found = true;
                idx += 1;
                continue;
            }
            idx += 1;
        }
        result.push(raw);
    }
    if !found {
        return false;
    }
    write_crontab_lines(&result)
}

pub fn toggle_cron_job(line_index: usize, enabled: bool) -> bool {
    let lines = read_crontab_lines();
    let mut result = Vec::new();
    let mut idx = 0usize;
    let mut found = false;
    for raw in lines {
        if is_cron_line(&raw) {
            if idx == line_index {
                found = true;
                let trimmed = raw.trim();
                let was_enabled = !trimmed.starts_with('#');
                if was_enabled == enabled {
                    result.push(raw);
                } else if enabled {
                    result.push(trimmed[1..].trim().to_string());
                } else {
                    result.push(format!("# {}", trimmed));
                }
                idx += 1;
                continue;
            }
            idx += 1;
        }
        result.push(raw);
    }
    if !found {
        return false;
    }
    write_crontab_lines(&result)
}

pub fn get_cron_service_status() -> String {
    for svc in &["cron", "crond"] {
        if let Ok(o) = Command::new("systemctl").args(&["is-active", svc]).output() {
            if o.status.success() {
                let s = String::from_utf8_lossy(&o.stdout).trim().to_string();
                if s == "active" { return "运行中".to_string(); }
                if s == "inactive" { return "已停止".to_string(); }
                return s;
            }
        }
    }
    if let Ok(o) = Command::new("pgrep").args(&["-x", "crond"]).output() {
        if o.status.success() { return "运行中".to_string(); }
    }
    if let Ok(o) = Command::new("pgrep").args(&["-x", "cron"]).output() {
        if o.status.success() { return "运行中".to_string(); }
    }
    "未知".to_string()
}
