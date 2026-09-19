using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using FreeWPFShell.Models;
using Renci.SshNet;

namespace FreeWPFShell.Share
{
    public class SshTunnelManager
    {
        private static readonly Lazy<SshTunnelManager> _instance = new(() => new SshTunnelManager());
        public static SshTunnelManager Instance => _instance.Value;

        private readonly object _lock = new();


        public ObservableCollection<SshTunnelInfo> ActiveTunnels { get; } = new ObservableCollection<SshTunnelInfo>();

        public void RegisterTunnel(SshTunnelInfo tunnelInfo)
        {
            RunOnUiThread(() =>
            {
                lock (_lock) { ActiveTunnels.Add(tunnelInfo); }
            });
        }

        public void UnregisterTunnel(string tunnelId)
        {
            RunOnUiThread(() =>
            {
                lock (_lock)
                {
                    var target = ActiveTunnels.FirstOrDefault(t => t.Id == tunnelId);
                    if (target != null)
                    {
                        ActiveTunnels.Remove(target);
                    }
                }
            });
        }


        public void UnregisterTunnelsByHost(string hostId)
        {
            RunOnUiThread(() =>
            {
                lock (_lock)
                {
                    var toRemove = ActiveTunnels.Where(t => t.HostId == hostId).ToList();
                    foreach (var t in toRemove)
                    {
                        ActiveTunnels.Remove(t);
                    }
                }
            });
        }


        private static void RunOnUiThread(Action action)
        {
            var app = System.Windows.Application.Current;
            Dispatcher? dispatcher = app?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        }
    }
}
