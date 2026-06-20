using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Models;
using FreeWPFShell.Share;

namespace FreeWPFShell.Views
{
    public partial class TraceroutePage : UserControl, IDisposable
    {
        private readonly ObservableCollection<TracerouteHop> _hops = new();
        private CancellationTokenSource? _cts;
        private bool _isTracing;

        public TraceroutePage()
        {
            InitializeComponent();
            HopsGrid.ItemsSource = _hops;
            HopsGrid.SelectionChanged += HopsGrid_SelectionChanged;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _isTracing = false;
        }

        private void HopsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HopsGrid.SelectedItem is TracerouteHop hop && hop.GeoDetail != null)
                TxtDetail.Text = hop.GeoDetail.DetailText;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _isTracing = false;
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isTracing) return;

            string target = TxtTarget.Text.Trim();
            if (string.IsNullOrEmpty(target)) return;

            var settings = new Repositories.SettingsRepository().Load();
            int maxHops = settings.TracerouteMaxHops > 0 ? settings.TracerouteMaxHops : 30;
            int timeoutMs = (settings.TracerouteTimeout > 0 ? settings.TracerouteTimeout : 5) * 1000;

            _hops.Clear();
            BtnStart.IsEnabled = false;
            BtnCancel.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();
            _isTracing = true;
            TxtDetail.Text = $"正在追踪到 {target} 的路由 (并发探测模式)...\n";

            try
            {
                var destAddresses = await Dns.GetHostAddressesAsync(target);
                if (destAddresses.Length == 0) { TxtDetail.Text = $"无法解析 {target}"; return; }
                IPAddress destIp = destAddresses[0];

                const int parallelLimit = 10;
                var foundDest = false;

                for (int i = 1; i <= maxHops; i++)
                    _hops.Add(new TracerouteHop { Hop = i, Ip = "*", Latency = "*", Status = "探测中..." });

                for (int startTtl = 1; startTtl <= maxHops; startTtl += parallelLimit)
                {
                    if (foundDest || _cts.Token.IsCancellationRequested) break;

                    int batchSize = Math.Min(parallelLimit, maxHops - startTtl + 1);
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
                            while (_hops.Count > actualLastHop + 1) _hops.RemoveAt(_hops.Count - 1);
                            break;
                        }
                    }
                    if (foundDest) break;
                }

                if (!foundDest && !_cts.Token.IsCancellationRequested)
                    TxtDetail.Text = $"已达最大跳数 ({maxHops})，未到达目标 {destIp}。";
                else if (foundDest)
                    TxtDetail.Text = $"追踪完成，到达目标 {destIp}。每个节点正在后台持续测速...";
            }
            catch (OperationCanceledException) { TxtDetail.Text = $"路由追踪已取消。"; }
            catch (Exception ex) { TxtDetail.Text = $"追踪出错: {ex.Message}"; }
            finally
            {
                _isTracing = false;
                BtnStart.IsEnabled = true;
                BtnCancel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<bool> ProbeHopAsync(IPAddress destIp, int ttl, int timeoutMs, CancellationToken ct)
        {
            var hop = _hops[ttl - 1];
            // Ping 实例非线程安全，每个探测任务必须独立实例
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

                        Dispatcher.Invoke(() =>
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

                Dispatcher.Invoke(() => { hop.Status = "超时"; hop.Latency = "*"; });
            }
            catch { Dispatcher.Invoke(() => hop.Status = "错误"); }
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
                    Dispatcher.Invoke(() => hop.Latency = reply.Status == IPStatus.Success
                        ? $"{reply.RoundtripTime}ms" : "超时");
                }
                catch { Dispatcher.Invoke(() => hop.Latency = "错误"); }

                await Task.Delay(2000, ct);
            }
        }
    }
}
