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
        /// 若存在 WPF UI 调度器，则在 UI 线程上执行；否则（无 Application 或已在 UI 线程）直接执行。
        /// 避免在测试或非 UI 上下文（Application.Current 为 null）时抛空引用。
        /// </summary>
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
