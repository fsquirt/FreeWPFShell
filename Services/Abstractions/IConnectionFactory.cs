using FreeWPFShell.Models;
using Renci.SshNet;

namespace FreeWPFShell.Services.Abstractions
{
    /// <summary>
    /// 根据连接信息构建 SSH/SFTP 客户端。封装认证方式、代理配置、
    /// 跳板机转发端口等连接参数解析逻辑，供连接门面复用。
    /// </summary>
    public interface IConnectionFactory
    {
        /// <summary>构建主 SSH 客户端。若处于跳板机隧道模式，走本地转发端口连接。</summary>
        SshClient BuildSshClient(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort);

        /// <summary>构建 SFTP 客户端。</summary>
        SftpClient BuildSftpClient(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort);

        /// <summary>构建跳板机 SSH 客户端（SSH 代理隧道场景）。</summary>
        SshClient BuildJumpClient(SshConnectionInfo info, PrivateKeyFile? jumpKey);
    }
}
