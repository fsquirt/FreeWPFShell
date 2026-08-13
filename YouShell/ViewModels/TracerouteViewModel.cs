using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouShell.Core;
using YouShell.Models;
using YouShell.Repositories;
using YouShell.Share;

namespace YouShell.ViewModels
{
    /// <summary>
    /// 路由追踪页 ViewModel。负责解析目标、并发探测 TTL 跳数、
    /// 更新跳点列表与详情、取消追踪等业务逻辑，与 View 解耦。
    /// </summary>
    public partial class TracerouteViewModel : ObservableObject
    {
        private const int ParallelLimit = 10;

        private readonly SettingsRepository _settingsRepo;
        private CancellationTokenSource? _cts;

        public ObservableCollection<TracerouteHop> Hops { get; } = new();

        [ObservableProperty]
        private string _target = string.Empty;

        [ObservableProperty]
        private string _detailText = "输入目标 IP 或域名，点击「开始追踪」";

        [ObservableProperty]
        private bool _isTracing;

        public bool IsCancelVisible => IsTracing;

        public TracerouteViewModel(SettingsRepository? settingsRepo = null)
        {
            _settingsRepo = settingsRepo ?? new SettingsRepository();
        }

        private static void RunOnUiThread(Action action) => UiDispatcher.Run(action);

        [RelayCommand]
        private async Task StartAsync()
        {
            if (IsTracing) return;
            string target = Target.Trim();
            if (string.IsNullOrEmpty(target)) return;

            var settings = _settingsRepo.Load();
            int maxHops = settings.TracerouteMaxHops > 0 ? settings.TracerouteMaxHops : 30;
            int timeoutMs = (settings.TracerouteTimeout > 0 ? settings.TracerouteTimeout : 5) * 1000;

            Hops.Clear();
            IsTracing = true;
            DetailText = $"正在追踪到 {target} 的路由 (并发探测模式)...\n";
            _cts = new CancellationTokenSource();

            try
            {
                var destAddresses = await Dns.GetHostAddressesAsync(target);
                if (destAddresses.Length == 0) { DetailText = $"无法解析 {target}"; return; }
                IPAddress destIp = destAddresses[0];
                var foundDest = false;

                for (int i = 1; i <= maxHops; i++)
                    Hops.Add(new TracerouteHop { Hop = i, Ip = "*", Latency = "*", Status = "探测中..." });

                for (int startTtl = 1; startTtl <= maxHops && !foundDest && !_cts.IsCancellationRequested; startTtl += ParallelLimit)
                {
                    int batchSize = Math.Min(ParallelLimit, maxHops - startTtl + 1);
                    var batchTasks = new Task<bool>[batchSize];
                    for (int i = 0; i < batchSize; i++)
                        batchTasks[i] = ProbeHopAsync(destIp, startTtl + i, timeoutMs, _cts.Token);

                    var results = await Task.WhenAll(batchTasks);
                    for (int i = 0; i < results.Length; i++)
                    {
                        if (results[i])
                        {
                            foundDest = true;
                            int actualLastHop = startTtl + i;
                            while (Hops.Count > actualLastHop + 1) Hops.RemoveAt(Hops.Count - 1);
                            break;
                        }
                    }
                }

                if (!foundDest && !_cts.IsCancellationRequested)
                    DetailText = $"已达最大跳数 ({maxHops})，未到达目标 {destIp}。";
                else if (foundDest)
                    DetailText = $"追踪完成，到达目标 {destIp}。每个节点正在后台持续测速...";
            }
            catch (OperationCanceledException)
            {
                DetailText = "路由追踪已取消。";
            }
            catch (Exception ex)
            {
                DetailText = $"追踪出错: {ex.Message}";
            }
            finally
            {
                IsTracing = false;
            }
        }

        private async Task<bool> ProbeHopAsync(IPAddress destIp, int ttl, int timeoutMs, CancellationToken ct)
        {
            var hop = Hops[ttl - 1];
            using var ping = new Ping();
            try
            {
                var buffer = new byte[32];
                var options = new PingOptions(ttl, true);

                for (int i = 0; i < 3; i++)
                {
                    if (ct.IsCancellationRequested) return false;
                    var reply = await ping.SendPingAsync(destIp, timeoutMs, buffer, options);

                    if (reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.Success)
                    {
                        var ip = reply.Address.ToString();
                        var isFinal = reply.Status == IPStatus.Success;

                        RunOnUiThread(() =>
                        {
                            hop.Ip = ip;
                            hop.Status = isFinal ? "已到达" : "中转节点";
                            var geo = IpGeoService.Instance.Query(ip);
                            hop.GeoDetail = geo;
                            hop.SimpleGeo = geo.SimpleGeo;
                        });

                        _ = RunContinuousPingAsync(hop, ip, timeoutMs, ct);
                        return isFinal;
                    }
                }

                RunOnUiThread(() => { hop.Status = "超时"; hop.Latency = "*"; });
            }
            catch
            {
                RunOnUiThread(() => hop.Status = "错误");
            }
            return false;
        }

        private async Task RunContinuousPingAsync(TracerouteHop hop, string ip, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(ip) || ip == "*") return;
            using var ping = new Ping();
            var buffer = new byte[32];

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var reply = await ping.SendPingAsync(ip, timeoutMs, buffer);
                    RunOnUiThread(() => hop.Latency = reply.Status == IPStatus.Success
                        ? $"{reply.RoundtripTime}ms" : "超时");
                }
                catch
                {
                    RunOnUiThread(() => hop.Latency = "错误");
                }
                await Task.Delay(2000, ct);
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            _cts?.Cancel();
            IsTracing = false;
        }

        partial void OnIsTracingChanged(bool value) => OnPropertyChanged(nameof(IsCancelVisible));
    }
}
