using System;
using System.Linq;
using System.Threading.Tasks;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;

namespace FreeWPFShell.Services
{
    /// <summary>
    /// 在两种密码存储方式之间批量迁移：
    /// 1) Windows 凭据管理器（PasswordVault）存储，读取前要求 Windows Hello 验证；
    /// 2) DPAPI（ProtectedData.CurrentUser）加密后写进 hosts.json / keys.json。
    /// 凭据管理器读取本身不弹窗（Hello 是应用层门禁），因此需要验证时只在开头验证一次。
    /// </summary>
    public static class CredentialMigrationService
    {
        /// <summary>迁移到 Windows Hello（凭据管理器）保护。凭据来源是 DPAPI，无需验证。</summary>
        public static async Task<(int Hosts, int Keys)> MigrateToVaultAsync()
        {
            return await Task.Run(() =>
            {
                int hosts = new HostRepository(new SettingsRepository()).MigrateSecrets(true);
                int keys = new KeyRepository().MigratePassphrases(true);
                return (hosts, keys);
            });
        }

        /// <summary>迁移到 DPAPI 保护。凭据来源是凭据管理器，需要先验证一次身份。</summary>
        public static async Task<(int Hosts, int Keys)> MigrateToDpapiAsync()
        {
            var hostRepo = new HostRepository(new SettingsRepository());
            var keyRepo = new KeyRepository();

            bool needVerify = hostRepo.GetAll().Any(h => h.AuthMethod == SshAuthMethod.Password && h.UseVault)
                              || keyRepo.GetAll().Any(k => k.HasPassphrase && k.UseVault);

            if (needVerify)
            {
                bool verified = await CredentialPromptService.RequestAuthenticationAsync(
                    "验证身份以把全部密码迁移到DPAPI保护");
                if (!verified)
                    throw new UnauthorizedAccessException("身份验证失败或被取消，已中止迁移。");
            }

            return await Task.Run(() =>
            {
                int hosts = hostRepo.MigrateSecrets(false);
                int keys = keyRepo.MigratePassphrases(false);
                return (hosts, keys);
            });
        }
    }
}
