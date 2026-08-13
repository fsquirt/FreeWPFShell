using System;
using System.Collections.Generic;
using YouShell.Models;
using YouShell.Services.Abstractions;
using YouShell.Share;

namespace YouShell.Services
{
    /// <summary>
    /// 会话级 SSH 隧道管理器（单一职责）。
    /// 负责注册、停止、清理本会话关联的所有隧道，避免残留隧道占用端口或导致后续连接异常。
    /// 端口创建由各调用方（连接/跳板机/监控/隧道页）完成并调用 RegisterTunnel 登记。
    /// </summary>
    public class TunnelService : ITunnelService
    {
        private readonly string _hostId;
        private readonly string _hostName;
        private readonly List<SshTunnelInfo> _tunnels = new();
        private readonly object _lock = new();
        private bool _cleanupDone;

        public TunnelService(string hostId, string hostName)
        {
            _hostId = hostId;
            _hostName = hostName;
        }

        public string HostId => _hostId;
        public string HostName => _hostName;

        public void RegisterTunnel(SshTunnelInfo tunnel)
        {
            lock (_lock) { _tunnels.Add(tunnel); }
            SshTunnelManager.Instance.RegisterTunnel(tunnel);
        }

        public void CleanupTunnels()
        {
            bool needClean;
            lock (_lock)
            {
                if (_cleanupDone) return;
                _cleanupDone = true;
                needClean = _tunnels.Count > 0;
            }
            if (!needClean) return;

            try
            {
                lock (_lock)
                {
                    foreach (var tunnel in _tunnels)
                    {
                        try
                        {
                            if (tunnel.PortConfig != null && tunnel.PortConfig.IsStarted)
                                tunnel.PortConfig.Stop();
                            SshTunnelManager.Instance.UnregisterTunnel(tunnel.Id);
                        }
                        catch { }
                    }
                    _tunnels.Clear();
                }
            }
            catch { }
        }

        public void Dispose() => CleanupTunnels();
    }
}
