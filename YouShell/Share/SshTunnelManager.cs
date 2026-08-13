using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using YouShell.Models;
using Renci.SshNet;

namespace YouShell.Share
{
    public class SshTunnelManager
    {
        private static readonly Lazy<SshTunnelManager> _instance = new(() => new SshTunnelManager());
        public static SshTunnelManager Instance => _instance.Value;

        private readonly object _lock = new();

        // Observable collection to allow real-time UI binding
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

        // Clean up tunnels by host, e.g. when session tab closes
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

        /// <summary>
        /// 若在 UI 线程则同步执行，否则 marshal 到 WinUI 3 的 DispatcherQueue 执行。
        /// 未初始化（无 UI 或测试上下文）时直接同步执行，避免空引用。
        /// </summary>
        private static void RunOnUiThread(Action action)
            => YouShell.Core.UiDispatcher.Run(action);
    }
}
