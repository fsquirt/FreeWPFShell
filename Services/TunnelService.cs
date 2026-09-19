using System;
using System.Collections.Generic;
using FreeWPFShell.Models;
using FreeWPFShell.Services.Abstractions;
using FreeWPFShell.Share;

namespace FreeWPFShell.Services
{

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
