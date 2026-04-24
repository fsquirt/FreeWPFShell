using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class ServiceItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("active_state")]
        public string ActiveState { get; set; } = string.Empty;

        [JsonPropertyName("sub_state")]
        public string SubState { get; set; } = string.Empty;

        [JsonPropertyName("load_state")]
        public string LoadState { get; set; } = string.Empty;

        [JsonPropertyName("pid")]
        public uint Pid { get; set; }

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("group")]
        public string Group { get; set; } = string.Empty;
    }
}
