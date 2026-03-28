using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;
using System.Runtime.InteropServices;

namespace FreeWPFShell.Share
{
    public class SshManager
    {
        public enum SshAuthMethod { Password, PrivateKey }
        public enum ProxyType { None, Http, Socks5 }

        public class ProxyInfo
        {
            public ProxyType Type { get; set; } = ProxyType.None;
            public string ServerAddress { get; set; } = string.Empty;
            public int Port { get; set; }
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }

        public class AppSettings
        {
            public bool UseWindowsHello { get; set; } = false;
            public bool UseLinuxMonitor { get; set; } = true;
            public string BackdropType { get; set; } = "Acrylic";
        }

        public class SshConnectionInfo
        {
            public string Id { get; set; } = string.Empty;
            public string HostName { get; set; } = string.Empty;
            public string IpAddress { get; set; } = string.Empty;
            public int SshPort { get; set; } = 22;
            public string SshUser { get; set; } = string.Empty;
            public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
            public bool UseProxy { get; set; } = false;
            public ProxyInfo? Proxy { get; set; }

            // Whether this specific host was saved using Vault (Windows Hello)
            public bool UseVault { get; set; } = false;

            // DPAPI Base64 Encrypted Secret
            public string? ProtectedSecret { get; set; }

            [JsonIgnore]
            public string? DecryptedSshSecret { get; set; }

            [JsonIgnore]
            public string? SimpleIpGEO { get; set; }
        }

        public class SshConnectionManager
        {
            private const string VAULT_RESOURCE_NAME = "MySecureSshManager";
            private readonly string _jsonFilePath;
            private readonly string _settingsFilePath;
            private List<SshConnectionInfo> _hosts;
            
            public AppSettings Settings { get; private set; }

            public SshConnectionManager()
            {
                _jsonFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosts.json");
                _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                _hosts = new List<SshConnectionInfo>();
                Settings = new AppSettings();
                LoadSettings();
                LoadJson();
            }

            private void LoadSettings()
            {
                if (File.Exists(_settingsFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_settingsFilePath);
                        Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    }
                    catch { Settings = new AppSettings(); }
                }
            }

            public void SaveSettings()
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(Settings, options));
            }

            private void LoadJson()
            {
                if (File.Exists(_jsonFilePath))
                {
                    string json = File.ReadAllText(_jsonFilePath);
                    _hosts = JsonSerializer.Deserialize<List<SshConnectionInfo>>(json) ?? new List<SshConnectionInfo>();
                }
            }

            private void SaveJson()
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_hosts, options);
                File.WriteAllText(_jsonFilePath, json);
            }

            private string GenerateShortId()
            {
                return Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            public List<SshConnectionInfo> GetAllHosts()
            {
                return _hosts.ToList();
            }

            private void SaveSecret(SshConnectionInfo host, string secret)
            {
                if (string.IsNullOrEmpty(secret)) return;

                host.UseVault = Settings.UseWindowsHello;

                if (host.UseVault)
                {
                    host.ProtectedSecret = null;
                    SaveSecretToVault(host.Id, secret);
                }
                else
                {
                    RemoveSecretFromVault(host.Id);
                    byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
                    byte[] encryptedBytes = ProtectedData.Protect(secretBytes, null, DataProtectionScope.CurrentUser);
                    host.ProtectedSecret = Convert.ToBase64String(encryptedBytes);
                }
            }

            public void AddHost(SshConnectionInfo host, string sshSecret)
            {
                do
                {
                    host.Id = GenerateShortId();
                } while (_hosts.Any(h => h.Id == host.Id));

                SaveSecret(host, sshSecret);
                _hosts.Add(host);
                SaveJson();
            }

            public void EditHost(string id, SshConnectionInfo updatedHost, string? newSshSecret = null)
            {
                var existing = _hosts.FirstOrDefault(h => h.Id == id)
                    ?? throw new Exception("未找到指定的主机 ID");

                existing.HostName = updatedHost.HostName;
                existing.IpAddress = updatedHost.IpAddress;
                existing.SshPort = updatedHost.SshPort;
                existing.SshUser = updatedHost.SshUser;
                existing.AuthMethod = updatedHost.AuthMethod;
                existing.UseProxy = updatedHost.UseProxy;
                existing.Proxy = updatedHost.Proxy;

                if (!string.IsNullOrEmpty(newSshSecret))
                {
                    SaveSecret(existing, newSshSecret);
                }
                else if (Settings.UseWindowsHello != existing.UseVault)
                {
                    // If settings changed but no new secret was provided, we cannot migrate easily
                    // because we would need the plaintext secret to re-encrypt.
                    // So we only update when they provide the secret (e.g., they re-type it).
                    // This is acceptable for an MVP.
                }

                SaveJson();
            }

            public void DeleteHost(string id)
            {
                var host = _hosts.FirstOrDefault(h => h.Id == id);
                if (host != null)
                {
                    _hosts.Remove(host);
                    RemoveSecretFromVault(id);
                    SaveJson();
                }
            }

            public async Task<SshConnectionInfo> GetHostAndDecryptAsync(string id)
            {
                var host = _hosts.FirstOrDefault(h => h.Id == id)
                    ?? throw new Exception("未在配置文件中找到该主机。");

                if (host.UseVault)
                {
                    bool verified = await RequestAuthenticationAsync($"验证身份以解密并连接至 {host.HostName}");
                    if (!verified)
                        throw new UnauthorizedAccessException("身份验证失败或被取消。");

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
                        catch (Exception)
                        {
                            throw new Exception("Windows凭据管理器中未找到该主机的密码记录，可能已丢失或未设置 Windows Hello。");
                        }
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
                                byte[] encryptedBytes = Convert.FromBase64String(host.ProtectedSecret);
                                byte[] secretBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                                host.DecryptedSshSecret = Encoding.UTF8.GetString(secretBytes);
                            }
                            return host;
                        }
                        catch (Exception ex)
                        {
                            throw new Exception("凭据解密失败，可能是配置在另一台电脑复制过来的或者密钥已损坏：" + ex.Message);
                        }
                    });
                }
            }

            // --- Vault Helpers ---
            private void SaveSecretToVault(string id, string secret)
            {
                var vault = new PasswordVault();
                RemoveSecretFromVault(id);
                var cred = new PasswordCredential(VAULT_RESOURCE_NAME, id, secret);
                vault.Add(cred);
            }

            private void RemoveSecretFromVault(string id)
            {
                var vault = new PasswordVault();
                try
                {
                    vault.Remove(vault.Retrieve(VAULT_RESOURCE_NAME, id));
                }
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
                ref CREDUI_INFO pUiInfo,
                int authError,
                ref uint pulAuthPackage,
                IntPtr pvInAuthBuffer,
                uint ulInAuthBufferSize,
                out IntPtr ppvOutAuthBuffer,
                out uint pulOutAuthBufferSize,
                ref bool pfSave,
                int flags);

            private async Task<bool> RequestAuthenticationAsync(string promptMessage)
            {
                try
                {
                    var availability = await UserConsentVerifier.CheckAvailabilityAsync();
                    if (availability == UserConsentVerifierAvailability.Available)
                    {
                        var result = await UserConsentVerifier.RequestVerificationAsync(promptMessage);
                        if (result == UserConsentVerificationResult.Verified) return true;
                        if (result == UserConsentVerificationResult.Canceled) return false;
                    }
                }
                catch { }

                return await PromptWindowsPasswordAsync(promptMessage);
            }

            private async Task<bool> PromptWindowsPasswordAsync(string promptMessage)
            {
                return await Task.Run(() =>
                {
                    int authError = 0;
                    while (true)
                    {
                        var uiInfo = new CREDUI_INFO
                        {
                            cbSize = Marshal.SizeOf(typeof(CREDUI_INFO)),
                            hwndParent = GetConsoleWindow(),
                            pszMessageText = promptMessage,
                        };

                        uint authPackage = 0;
                        IntPtr outAuthBuffer;
                        uint outAuthBufferSize;
                        bool save = false;

                        uint result = CredUIPromptForWindowsCredentials(
                            ref uiInfo,
                            authError,
                            ref authPackage,
                            IntPtr.Zero,
                            0,
                            out outAuthBuffer,
                            out outAuthBufferSize,
                            ref save,
                            0x1); 

                        if (result == 1223) return false; // Canceled

                        if (result == 0)
                        {
                            if (outAuthBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(outAuthBuffer);
                            return true;
                        }

                        authError = (int)result;
                    }
                });
            }
        }
    }
}
