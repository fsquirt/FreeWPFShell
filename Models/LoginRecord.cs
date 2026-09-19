using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class LoginRecord
    {
        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = string.Empty;

        [JsonPropertyName("time")]
        public string Time { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }


        public string Geo { get; set; } = string.Empty;
    }
}
