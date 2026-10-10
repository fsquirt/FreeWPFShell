using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;

namespace FreeWPFShell.Services
{
    /// <summary>
    /// 把全部主机连接信息与已导入的密钥导出为明文文本。
    /// 微软文档未给 UserConsentVerifier 提供任何验证结果缓存/会话复用机制（每次调用都会弹窗），
    /// 因此这里只在导出开始时主动验证一次，之后批量读取所有凭据，避免逐台主机弹 PIN。
    /// </summary>
    public static class DataExportService
    {
        public static async Task<string> BuildReportAsync()
        {
            var hostRepo = new HostRepository(new SettingsRepository());
            var keyRepo = new KeyRepository();

            var hosts = hostRepo.GetAll();
            var keys = keyRepo.GetAll();

            bool needVerify = hosts.Any(h => h.AuthMethod == SshAuthMethod.Password && h.UseVault)
                              || keys.Any(k => k.HasPassphrase && k.UseVault);

            if (needVerify)
            {
                bool verified = await CredentialPromptService.RequestAuthenticationAsync(
                    "验证身份以导出全部主机连接信息与密钥");
                if (!verified)
                    throw new UnauthorizedAccessException("身份验证失败或被取消，已中止导出。");
            }

            var keyPassphrases = await Task.Run(() => keyRepo.DecryptPassphrasesForExport());
            hostRepo.DecryptAllForExport();

            return BuildText(hosts, keys, keyPassphrases, needVerify);
        }

        private static string BuildText(List<SshConnectionInfo> hosts, List<SshKeyInfo> keys,
            IReadOnlyDictionary<string, string> keyPassphrases, bool verified)
        {
            var keyNames = new Dictionary<string, string>();
            foreach (var k in keys) keyNames[k.Id] = k.Name;

            var sb = new StringBuilder();
            sb.AppendLine("================ FreeWPFShell 数据导出 ================");
            sb.AppendLine($"导出时间 : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"主机数量 : {hosts.Count}");
            sb.AppendLine($"密钥数量 : {keys.Count}");
            sb.AppendLine($"身份验证 : {(verified ? "已通过 Windows Hello 验证一次" : "无需验证")}");
            sb.AppendLine("注意     : 本文件包含明文密码与私钥，请妥善保管。");
            sb.AppendLine();

            sb.AppendLine("########## 一、主机连接信息 ##########");
            sb.AppendLine();
            if (hosts.Count == 0) sb.AppendLine("(无)").AppendLine();
            for (int i = 0; i < hosts.Count; i++)
            {
                var h = hosts[i];
                sb.AppendLine($"[{i + 1}] {Safe(h.HostName)}");
                sb.AppendLine($"    IP 地址   : {Safe(h.IpAddress)}");
                sb.AppendLine($"    SSH 端口  : {h.SshPort}");
                sb.AppendLine($"    用户名    : {Safe(h.SshUser)}");
                sb.AppendLine($"    认证方式  : {(h.AuthMethod == SshAuthMethod.Password ? "密码登录" : "密钥登录")}");

                if (h.AuthMethod == SshAuthMethod.Password)
                {
                    sb.AppendLine($"    登录密码  : {(string.IsNullOrEmpty(h.DecryptedSshSecret) ? "(未保存或解密失败)" : h.DecryptedSshSecret)}");
                }
                else
                {
                    sb.AppendLine($"    使用的密钥: {DescribeKey(h.SshKeyId, keyNames)}");
                }

                sb.AppendLine($"    凭据存储  : {(h.UseVault ? "Windows 凭据管理器 (Windows Hello)" : "DPAPI 本地加密")}");
                if (!string.IsNullOrEmpty(h.LinuxDistro))
                    sb.AppendLine($"    系统发行版: {h.LinuxDistro}");

                AppendProxy(sb, h, keyNames);
                sb.AppendLine();
            }

            sb.AppendLine("########## 二、导入的密钥 ##########");
            sb.AppendLine();
            if (keys.Count == 0) sb.AppendLine("(无)").AppendLine();
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                sb.AppendLine($"[{i + 1}] {Safe(k.Name)}");
                sb.AppendLine($"    ID        : {k.Id}");
                sb.AppendLine($"    指纹      : {Safe(k.Fingerprint)}");
                sb.AppendLine($"    导入时间  : {k.ImportedAt:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"    密码保护  : {(k.HasPassphrase ? "是" : "否")}");

                if (k.HasPassphrase)
                {
                    sb.AppendLine($"    密钥密码  : {(keyPassphrases.TryGetValue(k.Id, out var pwd) ? pwd : "(解密失败)")}");
                }

                sb.AppendLine("    私钥内容  :");
                sb.AppendLine(DecodeKey(k.PrivateKeyBase64));
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static void AppendProxy(StringBuilder sb, SshConnectionInfo h,
            IReadOnlyDictionary<string, string> keyNames)
        {
            if (!h.UseProxy || h.Proxy == null)
            {
                sb.AppendLine("    代理      : 未启用");
                return;
            }

            var p = h.Proxy;
            string type = p.Type switch
            {
                ProxyType.Http => "HTTP",
                ProxyType.Socks4 => "SOCKS4",
                ProxyType.Socks5 => "SOCKS5",
                ProxyType.Ssh => "SSH 隧道(跳板机)",
                _ => p.Type.ToString()
            };

            sb.AppendLine($"    代理      : {type} {Safe(p.ServerAddress)}:{p.Port}");
            if (!string.IsNullOrEmpty(p.Username)) sb.AppendLine($"    代理用户  : {p.Username}");
            if (!string.IsNullOrEmpty(p.Password)) sb.AppendLine($"    代理密码  : {p.Password}");
            if (p.Type == ProxyType.Ssh && !string.IsNullOrEmpty(p.SshKeyId))
                sb.AppendLine($"    跳板机密钥: {DescribeKey(p.SshKeyId, keyNames)}");
        }

        private static string DescribeKey(string? keyId, IReadOnlyDictionary<string, string> keyNames)
        {
            if (string.IsNullOrEmpty(keyId)) return "(未指定)";
            return $"{(keyNames.TryGetValue(keyId, out var name) ? name : "未知密钥")} (ID: {keyId})";
        }

        private static string DecodeKey(string base64)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
            catch { return "(私钥内容无法解码)"; }
        }

        private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
