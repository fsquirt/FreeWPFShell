use crate::models::LoginRecord;
use crate::utils::{epoch_to_string, now_secs};

/// 手写 utmp/wtmp/btmp 二进制解析（替代 utmp-rs crate）。
///
/// 记录大小随平台而异（实测存在两种布局）：
/// - 384 字节：32 位布局，tv_sec 为 i32 @340（常见于 32 位用户态/armhf 等）
/// - 400 字节：64 位布局，tv_sec 为 i64 @344（x86_64/aarch64 glibc、musl）
///
/// 两种布局的公共字段偏移一致：ut_type: i16 @0, ut_user[32] @44, ut_host[256] @76。
/// 文件尾部可能有写了一半的残留记录（如 btmp 异常截断），检测时不能只看整除。
const RECORD_32: usize = 384;
const RECORD_64: usize = 400;
const TYPE_LOGIN_PROCESS: i16 = 6;
const TYPE_USER_PROCESS: i16 = 7;

fn read_tv_sec(rec: &[u8], is32: bool) -> i64 {
    if is32 {
        i32::from_le_bytes(rec[340..344].try_into().unwrap_or([0; 4])) as i64
    } else {
        i64::from_le_bytes(rec[344..352].try_into().unwrap_or([0; 8]))
    }
}

/// 检测记录布局：优先按文件大小整除判断；均不整除或均有歧义时，
/// 抽样统计时间戳合理率（解析发生在数据不可信的前提下，需容错）。
fn detect_layout(data: &[u8], now: i64) -> (usize, bool) {
    let div32 = data.len() % RECORD_32 == 0;
    let div64 = data.len() % RECORD_64 == 0;
    if div32 && !div64 { return (RECORD_32, true); }
    if div64 && !div32 { return (RECORD_64, false); }

    let mut best = (RECORD_64, false);
    let mut best_score = -1.0f64;
    for &(size, is32) in &[(RECORD_32, true), (RECORD_64, false)] {
        let n = data.len() / size;
        if n == 0 { continue; }
        let sample = n.min(50);
        let mut sane = 0usize;
        for i in 0..sample {
            let rec = &data[i * size..(i + 1) * size];
            let ts = read_tv_sec(rec, is32);
            if ts > 0 && ts <= now + 86400 { sane += 1; }
        }
        let score = sane as f64 / sample as f64;
        if score > best_score { best_score = score; best = (size, is32); }
    }
    best
}

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

/// filter_user_process=true 时只取 USER_PROCESS（wtmp 登录成功记录），
/// false 时也包含 LOGIN_PROCESS（btmp 失败登录记录，实测其记录类型为 6），与原 utmp-rs 版行为一致。
pub fn parse_utmp_file(path: &str, filter_user_process: bool, max_count: Option<usize>) -> Vec<LoginRecord> {
    let data = match std::fs::read(path) {
        Ok(d) => d,
        Err(_) => return Vec::new(),
    };
    if data.len() < RECORD_32 { return Vec::new(); }

    let (record_size, is32) = detect_layout(&data, now_secs());

    let n = data.len() / record_size;
    if n == 0 { return Vec::new(); }

    let mut records = Vec::new();
    // 文件按时间追加，倒序取最新记录
    for i in (0..n).rev() {
        if let Some(max) = max_count {
            if records.len() >= max { break; }
        }
        let rec = &data[i * record_size..(i + 1) * record_size];
        let utype = i16::from_le_bytes([rec[0], rec[1]]);
        if utype != TYPE_USER_PROCESS && !(utype == TYPE_LOGIN_PROCESS && !filter_user_process) {
            continue;
        }

        let user = read_cstr(&rec[44..76]);
        let host = read_cstr(&rec[76..332]);
        if user.is_empty() && host.is_empty() { continue; }

        // 时间戳异常的记录跳过：下限 1e9（2001-09 之前）基本可断定是半损坏/残留数据
        // （实测 btmp 在截断后残留 1996 年时间戳的脏记录），上限防未来值
        let secs = read_tv_sec(rec, is32);
        if secs < 1_000_000_000 || secs > now_secs() + 86400 { continue; }

        records.push(LoginRecord {
            user: if user.is_empty() { "(未知)".to_string() } else { user },
            ip: extract_ip_from_host(&host),
            time: epoch_to_string(secs),
            timestamp: secs,
        });
    }
    records
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 用真实服务器下载的 wtmp/btmp 验证布局检测与解析（文件不在时跳过）
    #[test]
    fn parse_real_wtmp_btmp() {
        let wtmp = std::path::Path::new("../wtmp");
        if !wtmp.exists() { return; }

        let records = parse_utmp_file("../wtmp", true, None);
        assert!(records.len() > 100, "wtmp 应解析出大量登录记录，实际 {}", records.len());
        let now = now_secs();
        assert!(
            records.iter().all(|r| r.timestamp > 1_600_000_000 && r.timestamp <= now + 86400),
            "wtmp 时间戳应全部合理，不得出现 1970/1984 之类的错位值"
        );
        assert!(records.iter().all(|r| !r.user.is_empty()));

        if std::path::Path::new("../btmp").exists() {
            let btmp = parse_utmp_file("../btmp", false, Some(10));
            assert_eq!(btmp.len(), 10, "btmp 应按上限返回 10 条");
            assert!(
                btmp.iter().all(|r| r.timestamp > 1_600_000_000 && r.timestamp <= now + 86400),
                "btmp 时间戳应全部合理"
            );
        }
    }
}
