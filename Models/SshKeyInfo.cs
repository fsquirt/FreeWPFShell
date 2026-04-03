using System.Text.Json.Serialization;

namespace FreeWPFShell.Models
{
    public class SshKeyInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>密钥文件内容，Base64 编码</summary>
        public string PrivateKeyBase64 { get; set; } = string.Empty;

        /// <summary>是否有 passphrase</summary>
        public bool HasPassphrase { get; set; }

        /// <summary>是否使用 Windows Hello 凭据保险箱</summary>
        public bool UseVault { get; set; }

        /// <summary>DPAPI 加密后的 passphrase (Base64)，UseVault=true 时为 null</summary>
        public string? ProtectedPassphrase { get; set; }

        /// <summary>导入时间</summary>
        public DateTime ImportedAt { get; set; } = DateTime.Now;

        /// <summary>密钥指纹（用于展示）</summary>
        public string? Fingerprint { get; set; }

        [JsonIgnore]
        public string DisplayText => string.IsNullOrEmpty(Fingerprint)
            ? $"{Name}{(HasPassphrase ? " 🔒" : "")}"
            : $"{Name} ({Fingerprint}){(HasPassphrase ? " 🔒" : "")}";
    }
}
