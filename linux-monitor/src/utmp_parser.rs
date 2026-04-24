use crate::models::LoginRecord;

pub fn extract_ip_from_host(host: &str) -> String {
    if host.is_empty() { return "(本地)".to_string(); }
    let trimmed = host.trim();
    let possible_ip = trimmed.split(|c: char| c == ' ' || c == ':').next().unwrap_or(trimmed);
    if possible_ip.parse::<std::net::IpAddr>().is_ok() {
        return possible_ip.to_string();
    }
    trimmed.to_string()
}

pub fn format_offset_datetime(dt: &time::OffsetDateTime) -> String {
    format!("{}-{:02}-{:02} {:02}:{:02}:{:02}",
        dt.year(), u8::from(dt.month()), dt.day(),
        dt.hour(), dt.minute(), dt.second())
}

pub fn parse_utmp_file(path: &str, filter_user_process: bool, max_count: Option<usize>) -> Vec<LoginRecord> {
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
