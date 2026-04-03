using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeWPFShell.Models;
using Renci.SshNet;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;

namespace FreeWPFShell.Repositories
{
    public class KeyRepository
    {
        private const string VAULT_RESOURCE_NAME = "FreeWPFShell_Keys";

        private readonly string _filePath;
        private readonly SettingsRepository _settingsRepo;
        private List<SshKeyInfo> _keys = new();

        public KeyRepository()
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keys.json");
            _settingsRepo = new SettingsRepository();
            Reload();
        }

        public void Reload()
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    string json = File.ReadAllText(_filePath);
                    _keys = JsonSerializer.Deserialize<List<SshKeyInfo>>(json) ?? new();
                }
                catch { _keys = new(); }
            }
        }

        private void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_keys, options));
        }

        public List<SshKeyInfo> GetAll() => _keys.ToList();

        public SshKeyInfo? FindById(string id) => _keys.FirstOrDefault(k => k.Id == id);

        /// <summary>
        /// 导入密钥文件。如果密钥有密码保护，passphrase 必须提供。
        /// </summary>
        public SshKeyInfo Import(string filePath, string name, string? passphrase)
        {
            byte[] keyContent = File.ReadAllBytes(filePath);

            // 验证密钥是否可用
            PrivateKeyFile keyFile;
            bool hasPassphrase = false;

            try
            {
                using var stream = new MemoryStream(keyContent);
                keyFile = new PrivateKeyFile(stream);
            }
            catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
            {
                // 密钥需要密码
                if (string.IsNullOrEmpty(passphrase))
                    throw new InvalidOperationException("此密钥有密码保护，请输入密钥密码。");

                using var stream = new MemoryStream(keyContent);
                keyFile = new PrivateKeyFile(stream, passphrase);
                hasPassphrase = true;
            }

            // 尝试获取密钥类型作为标识
            string? fingerprint = null;
            try
            {
                fingerprint = keyFile.Key?.ToString()?.Split('.').LastOrDefault() ?? "SSH Key";
            }
            catch { }

            var settings = _settingsRepo.Load();
            bool useVault = settings.UseWindowsHello;

            var keyInfo = new SshKeyInfo
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(filePath) : name,
                PrivateKeyBase64 = Convert.ToBase64String(keyContent),
                HasPassphrase = hasPassphrase,
                UseVault = useVault,
                Fingerprint = fingerprint,
                ImportedAt = DateTime.Now
            };

            // 保存 passphrase
            if (hasPassphrase && !string.IsNullOrEmpty(passphrase))
            {
                SavePassphrase(keyInfo, passphrase);
            }

            _keys.Add(keyInfo);
            Save();
            return keyInfo;
        }

        /// <summary>
        /// 保存 passphrase：Windows Hello 启用时存 PasswordVault，否则 DPAPI。
        /// </summary>
        private void SavePassphrase(SshKeyInfo keyInfo, string passphrase)
        {
            if (keyInfo.UseVault)
            {
                // 存入 Windows 凭据保险箱
                keyInfo.ProtectedPassphrase = null;
                var vault = new PasswordVault();
                RemoveFromVault(keyInfo.Id);
                vault.Add(new PasswordCredential(VAULT_RESOURCE_NAME, keyInfo.Id, passphrase));
            }
            else
            {
                // DPAPI 加密
                RemoveFromVault(keyInfo.Id);
                byte[] passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
                byte[] encrypted = ProtectedData.Protect(passphraseBytes, null, DataProtectionScope.CurrentUser);
                keyInfo.ProtectedPassphrase = Convert.ToBase64String(encrypted);
            }
        }

        private void RemoveFromVault(string id)
        {
            try { new PasswordVault().Remove(new PasswordVault().Retrieve(VAULT_RESOURCE_NAME, id)); }
            catch { }
        }

        /// <summary>
        /// 加载密钥为 PrivateKeyFile，自动解密 passphrase。
        /// 如果使用 Windows Hello，需要先调用 LoadPrivateKeyFileAsync。
        /// </summary>
        public PrivateKeyFile LoadPrivateKeyFile(string keyId)
        {
            var keyInfo = FindById(keyId) ?? throw new Exception($"未找到密钥 ID: {keyId}");
            byte[] keyContent = Convert.FromBase64String(keyInfo.PrivateKeyBase64);

            if (!keyInfo.HasPassphrase)
            {
                using var stream = new MemoryStream(keyContent);
                return new PrivateKeyFile(stream);
            }

            // 解密 passphrase
            string passphrase = DecryptPassphrase(keyInfo);
            using var stream2 = new MemoryStream(keyContent);
            return new PrivateKeyFile(stream2, passphrase);
        }

        /// <summary>
        /// 异步加载密钥（支持 Windows Hello 验证）。
        /// </summary>
        public async Task<PrivateKeyFile> LoadPrivateKeyFileAsync(string keyId)
        {
            var keyInfo = FindById(keyId) ?? throw new Exception($"未找到密钥 ID: {keyId}");
            byte[] keyContent = Convert.FromBase64String(keyInfo.PrivateKeyBase64);

            if (!keyInfo.HasPassphrase)
            {
                using var stream = new MemoryStream(keyContent);
                return new PrivateKeyFile(stream);
            }

            // 如果是 Windows Hello 保护的，先验证身份
            if (keyInfo.UseVault)
            {
                bool verified = await RequestAuthenticationAsync($"验证身份以解密密钥 \"{keyInfo.Name}\" 的密码");
                if (!verified)
                    throw new UnauthorizedAccessException("身份验证失败或被取消。");
            }

            string passphrase = await Task.Run(() => DecryptPassphrase(keyInfo));
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(keyContent);
                return new PrivateKeyFile(stream, passphrase);
            });
        }

        private string DecryptPassphrase(SshKeyInfo keyInfo)
        {
            if (keyInfo.UseVault)
            {
                try
                {
                    var vault = new PasswordVault();
                    var cred = vault.Retrieve(VAULT_RESOURCE_NAME, keyInfo.Id);
                    cred.RetrievePassword();
                    return cred.Password;
                }
                catch { throw new Exception("Windows 凭据管理器中未找到该密钥的密码记录。"); }
            }
            else
            {
                if (string.IsNullOrEmpty(keyInfo.ProtectedPassphrase))
                    throw new Exception("密钥密码数据丢失。");

                byte[] encrypted = Convert.FromBase64String(keyInfo.ProtectedPassphrase);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
        }

        public void Delete(string id)
        {
            var key = FindById(id);
            if (key != null)
            {
                RemoveFromVault(key.Id);
                _keys.Remove(key);
                Save();
            }
        }

        // ─── Windows Hello 验证 ────────────────────────────────────
        // 和 HostRepository 保持一致的验证逻辑

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDUI_INFO
        {
            public int cbSize;
            public IntPtr hwndParent;
            public string pszMessageText;
            public string pszCaptionText;
            public IntPtr hbmBanner;
        }

        [DllImport("credui.dll", CharSet = CharSet.Unicode)]
        private static extern uint CredUIPromptForWindowsCredentials(
            ref CREDUI_INFO pUiInfo, int authError, ref uint pulAuthPackage,
            IntPtr pvInAuthBuffer, uint ulInAuthBufferSize,
            out IntPtr ppvOutAuthBuffer, out uint pulOutAuthBufferSize,
            ref bool pfSave, int flags);

        private async Task<bool> RequestAuthenticationAsync(string prompt)
        {
            try
            {
                var availability = await UserConsentVerifier.CheckAvailabilityAsync();
                if (availability == UserConsentVerifierAvailability.Available)
                {
                    var result = await UserConsentVerifier.RequestVerificationAsync(prompt);
                    if (result == UserConsentVerificationResult.Verified) return true;
                    if (result == UserConsentVerificationResult.Canceled) return false;
                }
            }
            catch { }
            return await Task.Run(() =>
            {
                int authError = 0;
                while (true)
                {
                    var uiInfo = new CREDUI_INFO { cbSize = Marshal.SizeOf(typeof(CREDUI_INFO)), hwndParent = GetConsoleWindow(), pszMessageText = prompt };
                    uint authPackage = 0; IntPtr outBuffer; uint outSize; bool save = false;
                    uint result = CredUIPromptForWindowsCredentials(ref uiInfo, authError, ref authPackage, IntPtr.Zero, 0, out outBuffer, out outSize, ref save, 0x1);
                    if (result == 1223) return false;
                    if (result == 0) { if (outBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(outBuffer); return true; }
                    authError = (int)result;
                }
            });
        }
    }
}
