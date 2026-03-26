using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Renci.SshNet;

namespace FreeWPFShell.Share
{
    public class SshTunnelInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public ForwardedPort? PortConfig { get; set; }
        
        // Enum: "Local" or "Remote"
        public string Type { get; set; } = "Local"; 
        
        public string BindAddress { get; set; } = string.Empty;
        public uint BindPort { get; set; }
        public string DestAddress { get; set; } = string.Empty;
        public uint DestPort { get; set; }
        public string Remark { get; set; } = string.Empty;

        // Visual properties for UI binding
        public string PortMapping => $"{BindAddress}:{BindPort} -> {DestAddress}:{DestPort}";
    }

    public class SshTunnelManager
    {
        private static Lazy<SshTunnelManager> _instance = new Lazy<SshTunnelManager>(() => new SshTunnelManager());
        public static SshTunnelManager Instance => _instance.Value;

        // Observable collection to allow real-time UI binding
        public ObservableCollection<SshTunnelInfo> ActiveTunnels { get; } = new ObservableCollection<SshTunnelInfo>();

        public void RegisterTunnel(SshTunnelInfo tunnelInfo)
        {
            // Must be called from UI thread if bound to UI, but let's assume it's dispatched outside
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ActiveTunnels.Add(tunnelInfo);
            });
        }

        public void UnregisterTunnel(string tunnelId)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var target = ActiveTunnels.FirstOrDefault(t => t.Id == tunnelId);
                if (target != null)
                {
                    ActiveTunnels.Remove(target);
                }
            });
        }

        // Clean up tunnels by host, e.g. when session tab closes
        public void UnregisterTunnelsByHost(string hostId)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var toRemove = ActiveTunnels.Where(t => t.HostId == hostId).ToList();
                foreach (var t in toRemove)
                {
                    ActiveTunnels.Remove(t);
                }
            });
        }
    }
}
