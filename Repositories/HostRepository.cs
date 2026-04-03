using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;
using FreeWPFShell.Models;

namespace FreeWPFShell.Repositories
{
    public class HostRepository
    {
        private const string VAULT_RESOURCE_NAME = "MySecureSshManager";
        private readonly string _filePath;
        private readonly SettingsRepository _settingsRepo;
        private List<SshConnectionInfo> _hosts = new();

        public HostRepository(SettingsRepository settingsRepo)
        {
            _settingsRepo = settingsRepo;
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosts.json");
            Reload();
        }

        public void Reload()
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                _hosts = JsonSerializer.Deserialize<List<SshConnectionInfo>>(json) ?? new List<SshConnectionInfo>();
            }
        }

        private void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_hosts, options));
        }

        private string GenerateShortId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        public List<SshConnectionInfo> GetAll() => _hosts.ToList();

        public SshConnectionInfo? FindById(string id) => _hosts.FirstOrDefault(h => h.Id == id);

        public async Task AddAsync(SshConnectionInfo host, string sshSecret)
        {
            await Task.Run(() =>
            {
                do { host.Id = GenerateShortId(); } while (_hosts.Any(h => h.Id == host.Id));
                SaveSecret(host, sshSecret);
                _hosts.Add(host);
                Save();
            });
        }

        public async Task UpdateAsync(string id, SshConnectionInfo updated, string? newSecret = null)
        {
            await Task.Run(() =>
            {
                var existing = FindById(id) ?? throw new Exception("未找到指定的主机 ID");
                existing.HostName = updated.HostName;
                existing.IpAddress = updated.IpAddress;
                existing.SshPort = updated.SshPort;
                existing.SshUser = updated.SshUser;
                existing.AuthMethod = updated.AuthMethod;
                existing.UseProxy = updated.UseProxy;
                existing.Proxy = updated.Proxy;
                if (!string.IsNullOrEmpty(newSecret))
                    SaveSecret(existing, newSecret);
                Save();
            });
        }

        public void Delete(string id)
        {
            var host = FindById(id);
            if (host != null)
            {
                _hosts.Remove(host);
                RemoveSecretFromVault(id);
                Save();
            }
        }

        public async Task<SshConnectionInfo> GetAndDecryptAsync(string id)
        {
            var host = FindById(id) ?? throw new Exception("未在配置文件中找到该主机。");
            var settings = _settingsRepo.Load();

            // 密钥认证不需要解密主机密码（密钥密码由 KeyRepository 管理）
            if (host.AuthMethod == SshAuthMethod.PrivateKey)
            {
                return host;
            }

            if (host.UseVault)
            {
                bool verified = await RequestAuthenticationAsync($"验证身份以解密并连接至 {host.HostName}");
                if (!verified) throw new UnauthorizedAccessException("身份验证失败或被取消。");

                return await Task.Run(() =>
                {
                    var vault = new PasswordVault();
                    try
                    {
                        var cred = vault.Retrieve(VAULT_RESOURCE_NAME, id);
                        cred.RetrievePassword();
                        host.DecryptedSshSecret = cred.Password;
                        return host;
                    }
                    catch { throw new Exception("Windows凭据管理器中未找到该主机的密码记录。"); }
                });
            }
            else
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(host.ProtectedSecret))
                        {
                            byte[] encrypted = Convert.FromBase64String(host.ProtectedSecret);
                            byte[] secret = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                            host.DecryptedSshSecret = Encoding.UTF8.GetString(secret);
                        }
                        return host;
                    }
                    catch (Exception ex) { throw new Exception("凭据解密失败：" + ex.Message); }
                });
            }
        }

        private void SaveSecret(SshConnectionInfo host, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return;
            var settings = _settingsRepo.Load();
            host.UseVault = settings.UseWindowsHello;

            if (host.UseVault)
            {
                host.ProtectedSecret = null;
                var vault = new PasswordVault();
                RemoveSecretFromVault(host.Id);
                vault.Add(new PasswordCredential(VAULT_RESOURCE_NAME, host.Id, secret));
            }
            else
            {
                RemoveSecretFromVault(host.Id);
                byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
                byte[] encrypted = ProtectedData.Protect(secretBytes, null, DataProtectionScope.CurrentUser);
                host.ProtectedSecret = Convert.ToBase64String(encrypted);
            }
        }

        private void RemoveSecretFromVault(string id)
        {
            try { new PasswordVault().Remove(new PasswordVault().Retrieve(VAULT_RESOURCE_NAME, id)); }
            catch { }
        }

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
