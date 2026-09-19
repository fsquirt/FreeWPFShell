using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class SshKeyInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;


        public string PrivateKeyBase64 { get; set; } = string.Empty;


        public bool HasPassphrase { get; set; }


        public bool UseVault { get; set; }


        public string? ProtectedPassphrase { get; set; }


        public DateTime ImportedAt { get; set; } = DateTime.Now;


        public string? Fingerprint { get; set; }

        [JsonIgnore]
        public string DisplayText => string.IsNullOrEmpty(Fingerprint)
            ? $"{Name}"
            : $"{Name} ({Fingerprint})";
    }
}
