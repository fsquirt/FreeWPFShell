using FreeWPFShell.Models;
using Renci.SshNet;

namespace FreeWPFShell.Services.Abstractions
{
    /// <summary>
    /// 会话级 SSH 隧道管理。负责注册、停止、清理本会话关联的所有隧道
    /// （含跳板机、监控探针、手动创建的本地/远程转发）。
    /// </summary>
    public interface ITunnelService : IDisposable
    {
        /// <summary>注册一个隧道到本会话，并同步到全局隧道管理器。</summary>
        void RegisterTunnel(SshTunnelInfo tunnel);

        /// <summary>幂等清理本会话所有隧道（Stop 端口 + 从全局表移除）。</summary>
        void CleanupTunnels();
    }
}
