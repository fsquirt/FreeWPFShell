using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;
using System.IO;

namespace FreeWPFShell.Share
{
    class SshManager
    {
        // ==========================================
        // 1. 数据模型与枚举定义
        // ==========================================
        public enum SshAuthMethod { Password, PrivateKey }
        public enum ProxyType { None, Http, Socks5 }

        public class ProxyInfo
        {
            public ProxyType Type { get; set; } = ProxyType.None;
            public string ServerAddress { get; set; } = string.Empty;
            public int Port { get; set; }
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty; // 明文保存在 JSON 中
        }

        public class SshConnectionInfo
        {
            public string Id { get; set; } = string.Empty; // 短 ID
            public string HostName { get; set; } = string.Empty;
            public string IpAddress { get; set; } = string.Empty;
            public int SshPort { get; set; } = 22;
            public string SshUser { get; set; } = string.Empty;
            public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
            public bool UseProxy { get; set; } = false;
            public ProxyInfo? Proxy { get; set; }

            // 确保内存中解密后的密码绝不会被序列化到 JSON 硬盘文件中
            [JsonIgnore]
            public string? DecryptedSshSecret { get; set; }
        }

        // ==========================================
        // 2. 主机配置与安全连接管理器
        // ==========================================
        public class SshConnectionManager
        {
            private const string VAULT_RESOURCE_NAME = "MySecureSshManager";
            private readonly string _jsonFilePath;
            private List<SshConnectionInfo> _hosts;

            public SshConnectionManager()
            {
                // 配置 JSON 文件保存在程序运行目录下
                _jsonFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosts.json");
                _hosts = new List<SshConnectionInfo>();
                LoadJson();
            }

            // ------------------------------------------
            // JSON 维护与 CRUD 接口
            // ------------------------------------------

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
                // 生成 8 位短 ID (例如 a1b2c3d4)
                return Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            // 获取所有保存的主机列表（不包含敏感密码）
            public List<SshConnectionInfo> GetAllHosts()
            {
                return _hosts.ToList();
            }

            // 添加主机，确保 ID 唯一，并将 SSH 密码存入凭据管理器
            public void AddHost(SshConnectionInfo host, string sshSecret)
            {
                // 确保 ID 唯一
                do
                {
                    host.Id = GenerateShortId();
                } while (_hosts.Any(h => h.Id == host.Id));

                _hosts.Add(host);
                SaveJson(); // 保存非敏感信息到 JSON

                // 保存敏感信息到 TPM/凭据管理器
                SaveSecretToVault(host.Id, sshSecret);
            }

            // 编辑主机配置。如果提供了 newSshSecret，则同步更新凭据管理器。
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

                SaveJson();

                // 如果修改了密码，则更新凭据管理器
                if (!string.IsNullOrEmpty(newSshSecret))
                {
                    SaveSecretToVault(id, newSshSecret);
                }
            }

            // 删除主机（同时清理 JSON 和 凭据管理器）
            public void DeleteHost(string id)
            {
                var host = _hosts.FirstOrDefault(h => h.Id == id);
                if (host != null)
                {
                    _hosts.Remove(host);
                    SaveJson();
                }

                // 从凭据管理器移除
                RemoveSecretFromVault(id);
            }

            // ------------------------------------------
            // 解密与安全获取接口
            // ------------------------------------------

            //获取主机信息并拉起验证解密 SSH 密码
            public async Task<SshConnectionInfo> GetHostAndDecryptAsync(string id)
            {
                var host = _hosts.FirstOrDefault(h => h.Id == id)
                    ?? throw new Exception("未在配置文件中找到该主机。");

                // 拉起安全验证 (优先 Windows Hello，降级 Windows 密码)
                bool verified = await RequestAuthenticationAsync($"验证身份以解密并连接至 {host.HostName}");
                if (!verified)
                {
                    throw new UnauthorizedAccessException("身份验证失败或被取消。");
                }

                // 验证通过，从凭据管理器中提取秘密
                var vault = new PasswordVault();
                try
                {
                    var cred = vault.Retrieve(VAULT_RESOURCE_NAME, id);
                    cred.RetrievePassword();

                    // 将密码存入不会被序列化的属性中，供调用方在内存中使用
                    host.DecryptedSshSecret = cred.Password;
                    return host;
                }
                catch (Exception)
                {
                    throw new Exception("凭据管理器中未找到该主机的 SSH 密码记录，可能已丢失。");
                }
            }

            // ------------------------------------------
            // 内部安全与凭据操作实现
            // ------------------------------------------

            private void SaveSecretToVault(string id, string secret)
            {
                var vault = new PasswordVault();
                RemoveSecretFromVault(id); // 覆盖前先删除
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
                catch { /* 忽略不存在时的异常 */ }
            }

            /// 安全验证路由：判断使用 Windows Hello 还是退化到 Windows 密码输入框
            private async Task<bool> RequestAuthenticationAsync(string promptMessage)
            {
                try
                {
                    var availability = await UserConsentVerifier.CheckAvailabilityAsync();

                    if (availability == UserConsentVerifierAvailability.Available)
                    {
                        // 注意：在无 UI 线程的纯控制台程序中调用此 API 可能会抛出异常 (0x80070578 缺少窗口句柄)
                        var result = await UserConsentVerifier.RequestVerificationAsync(promptMessage);
                        if (result == UserConsentVerificationResult.Verified) return true;
                        if (result == UserConsentVerificationResult.Canceled) return false; // 用户主动取消
                    }
                }
                catch (Exception)
                {
                    // 若 Windows Hello 弹窗因为缺乏 UI 上下文等原因抛出异常，安全捕获并流转到降级方案
                }

                // 降级方案：弹出纯正的 Windows 账户密码框 (图二效果)
                return await PromptWindowsPasswordAsync(promptMessage);
            }

            // 降级方案：拉起 Windows 安全中心验证 
            private async Task<bool> PromptWindowsPasswordAsync(string promptMessage)
            {
                return await Task.Run(() =>
                {
                    int authError = 0; // 0 表示第一次弹窗，没有红字错误

                    while (true)
                    {
                        var uiInfo = new CREDUI_INFO
                        {
                            cbSize = Marshal.SizeOf(typeof(CREDUI_INFO)),
                            hwndParent = GetConsoleWindow(), // 【修复2】绑定当前控制台的句柄，强制弹窗保持在屏幕最前方！
                            pszMessageText = "首先，请验证你的帐户密码",
                        };

                        uint authPackage = 0;
                        IntPtr outAuthBuffer;
                        uint outAuthBufferSize;
                        bool save = false;

                        // 去掉了 0x20 标志！只保留 0x200 (CREDUIWIN_ENUMERATE_CURRENT_USER)。
                        // 这样就不会因为我们没有传入 InputBuffer 而导致底层 API 罢工了。
                        uint flags = 0x200;

                        uint result = CredUIPromptForWindowsCredentials(
                            ref uiInfo,
                            authError,
                            ref authPackage,
                            IntPtr.Zero,
                            0,
                            out outAuthBuffer,
                            out outAuthBufferSize,
                            ref save,
                            flags);

                        if (result == 1223) return false; // 用户点击了"取消"
                        if (result != 0) return false;    // API 发生其他级别错误

                        // 1. 解包获取输入的明文密码 (扩大了容量，防止溢出)
                        StringBuilder userBuf = new StringBuilder(256);
                        int userLen = 256;
                        StringBuilder domainBuf = new StringBuilder(256);
                        int domainLen = 256;
                        StringBuilder passBuf = new StringBuilder(256);
                        int passLen = 256;

                        bool unpacked = CredUnPackAuthenticationBuffer(
                            0, outAuthBuffer, outAuthBufferSize,
                            userBuf, ref userLen,
                            domainBuf, ref domainLen,
                            passBuf, ref passLen);

                        Marshal.FreeCoTaskMem(outAuthBuffer);

                        if (!unpacked) return false;

                        string username = userBuf.ToString();
                        string domain = domainBuf.ToString();
                        string password = passBuf.ToString();

                        // 2. 清洗“域”与“用户名”，防止 LogonUser 判断出错
                        if (string.IsNullOrEmpty(domain))
                        {
                            if (username.Contains("\\"))
                            {
                                var parts = username.Split(new[] { '\\' }, 2);
                                domain = parts[0];
                                username = parts[1];
                            }
                            else if (username.Contains("@"))
                            {
                                domain = null; // 微软账户不需要域
                            }
                            else
                            {
                                domain = Environment.MachineName; // 本地账户
                            }
                        }
                        else
                        {
                            if (username.Contains("\\"))
                            {
                                username = username.Substring(username.IndexOf('\\') + 1);
                            }
                        }

                        // 3. 校验密码是否正确
                        bool isValid = LogonUser(username, domain, password, 2 /* LOGON32_LOGON_INTERACTIVE */, 0, out IntPtr token);

                        if (isValid)
                        {
                            CloseHandle(token);
                            return true;        // 验证通过，放行解密！
                        }
                        else
                        {
                            // 密码错误：赋予 1326 错误码，下次循环弹窗会自动产生原生摇晃和报错红字！
                            authError = 1326;
                        }
                    }
                });
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct CREDUI_INFO
            {
                public int cbSize;
                public IntPtr hwndParent;
                public string pszMessageText;
                public string pszCaptionText;
                public IntPtr hbmBanner;
            }
            [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern uint CredUIPromptForWindowsCredentials(
                ref CREDUI_INFO pUiInfo,
                int dwAuthError,
                ref uint pulAuthPackage,
                IntPtr pvInAuthBuffer,
                uint cbInAuthBuffer,
                out IntPtr ppvOutAuthBuffer,
                out uint pcbOutAuthBuffer,
                ref bool pfSave,
                uint dwFlags);

            [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool CredUnPackAuthenticationBuffer(
                uint dwFlags,
                IntPtr pAuthBuffer,
                uint cbAuthBuffer,
                StringBuilder pszUserName,
                ref int pcchMaxUserName,
                StringBuilder pszDomainName,
                ref int pcchMaxDomainName,
                StringBuilder pszPassword,
                ref int pcchMaxPassword);

            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hHandle);
            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern IntPtr GetConsoleWindow();
        }
    }
}
