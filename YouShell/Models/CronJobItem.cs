using System.Text.Json.Serialization;

namespace YouShell.Models
{
    public class CronJobItem
    {
        [JsonPropertyName("line_index")]
        public int LineIndex { get; set; }

        [JsonPropertyName("schedule")]
        public string Schedule { get; set; } = string.Empty;

        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("raw")]
        public string Raw { get; set; } = string.Empty;

        [JsonIgnore]
        public string StatusText => Enabled ? "启用" : "禁用";

        [JsonIgnore]
        public string ScheduleDescription
        {
            get
            {
                var parts = Schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5) return Schedule;
                string min = parts[0], hour = parts[1], dom = parts[2], month = parts[3], dow = parts[4];

                // Exact matches
                if (min == "*" && hour == "*" && dom == "*" && month == "*" && dow == "*")
                    return "每分钟";
                if (min.StartsWith("*/") && hour == "*" && dom == "*" && month == "*" && dow == "*")
                    return $"每{min.Substring(2)}分钟";
                if (min == "0" && hour.StartsWith("*/") && dom == "*" && month == "*" && dow == "*")
                    return $"每{hour.Substring(2)}小时";
                if (min == "0" && hour == "0" && dom.StartsWith("*/") && month == "*" && dow == "*")
                    return $"每{dom.Substring(2)}天";
                if (min == "0" && hour == "0" && dom == "*" && month == "*" && dow == "*")
                    return "每天 00:00";
                if (min == "0" && hour == "0" && dom == "*" && month == "*" && dow == "0")
                    return "每周日 00:00";
                if (min == "0" && hour == "0" && dom == "1" && month == "*" && dow == "*")
                    return "每月1日 00:00";
                if (min == "0" && hour == "0" && dom == "1" && month == "1" && dow == "*")
                    return "每年1月1日 00:00";

                // Helper: format hour/min with step support like */2
                string fmtTime(string h, string m)
                {
                    string mm = m == "0" ? "00" : (m.StartsWith("*/") ? $"每{m.Substring(2)}分钟" : m.PadLeft(2, '0'));
                    string hh = h.StartsWith("*/") ? $"每{h.Substring(2)}小时" : h.PadLeft(2, '0');
                    if (h.StartsWith("*/") && m == "0") return hh;
                    return $"{hh}:{mm}";
                }

                if (dom == "*" && month == "*" && dow == "*")
                {
                    if (hour.StartsWith("*/") && min == "0")
                        return $"每{hour.Substring(2)}小时";
                    return $"每天 {fmtTime(hour, min)}";
                }
                if (dom == "*" && month == "*" && dow != "*")
                {
                    string[] weekdays = { "日", "一", "二", "三", "四", "五", "六" };
                    if (int.TryParse(dow, out int d) && d >= 0 && d <= 6)
                        return $"每周{weekdays[d]} {fmtTime(hour, min)}";
                    if (dow.StartsWith("*/"))
                        return $"每{dow.Substring(2)}周 {fmtTime(hour, min)}";
                }
                if (dom != "*" && month == "*" && dow == "*")
                {
                    if (dom.StartsWith("*/"))
                        return $"每{dom.Substring(2)}天 {fmtTime(hour, min)}";
                    return $"每月{dom}日 {fmtTime(hour, min)}";
                }
                if (dom != "*" && month != "*" && dow == "*")
                {
                    if (month.StartsWith("*/"))
                        return $"每{month.Substring(2)}月{dom}日 {fmtTime(hour, min)}";
                    return $"每年{month}月{dom}日 {fmtTime(hour, min)}";
                }

                return Schedule;
            }
        }
    }
}
