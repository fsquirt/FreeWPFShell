use crate::models::LoginRecord;
use crate::utils::{epoch_to_string, now_secs};

/// 手写 utmp/wtmp/btmp 二进制解析（替代 utmp-rs crate）。
/// x86_64 glibc/musl 的 struct utmp 固定 400 字节，关键字段偏移：
///   ut_type: i16 @0, ut_user[32] @44, ut_host[256] @76, ut_tv.tv_sec: i64 @348
const RECORD_SIZE: usize = 400;
const TYPE_LOGIN_PROCESS: i16 = 6;
const TYPE_USER_PROCESS: i16 = 7;

fn read_cstr(bytes: &[u8]) -> String {
    let end = bytes.iter().position(|&b| b == 0).unwrap_or(bytes.len());
    String::from_utf8_lossy(&bytes[..end]).trim().to_string()
}

pub fn extract_ip_from_host(host: &str) -> String {
    if host.is_empty() { return "(本地)".to_string(); }
    let trimmed = host.trim();
    let possible_ip = trimmed.split(|c: char| c == ' ' || c == ':').next().unwrap_or(trimmed);
    if possible_ip.parse::<std::net::IpAddr>().is_ok() {
        return possible_ip.to_string();
    }
    trimmed.to_string()
}

/// filter_user_process=true 时只取 USER_PROCESS（wtmp 登录记录），
/// false 时也包含 LOGIN_PROCESS（btmp 失败登录记录），与原 utmp-rs 版行为一致。
pub fn parse_utmp_file(path: &str, filter_user_process: bool, max_count: Option<usize>) -> Vec<LoginRecord> {
    let data = match std::fs::read(path) {
        Ok(d) => d,
        Err(_) => return Vec::new(),
    };

    let n = data.len() / RECORD_SIZE;
    if n == 0 { return Vec::new(); }

    let mut records = Vec::new();
    // 文件按时间追加，倒序取最新记录
    for i in (0..n).rev() {
        if let Some(max) = max_count {
            if records.len() >= max { break; }
        }
        let rec = &data[i * RECORD_SIZE..(i + 1) * RECORD_SIZE];
        let utype = i16::from_le_bytes([rec[0], rec[1]]);
        if utype != TYPE_USER_PROCESS && !(utype == TYPE_LOGIN_PROCESS && !filter_user_process) {
            continue;
        }

        let user = read_cstr(&rec[44..76]);
        let host = read_cstr(&rec[76..332]);
        if user.is_empty() && host.is_empty() { continue; }

        // tv_sec 为 0 的记录是残留/未完成条目，跳过
        let secs = i64::from_le_bytes(rec[348..356].try_into().unwrap_or([0; 8]));
        if secs <= 0 || secs > now_secs() + 86400 { continue; }

        records.push(LoginRecord {
            user: if user.is_empty() { "(未知)".to_string() } else { user },
            ip: extract_ip_from_host(&host),
            time: epoch_to_string(secs),
            timestamp: secs,
        });
    }
    records
}
