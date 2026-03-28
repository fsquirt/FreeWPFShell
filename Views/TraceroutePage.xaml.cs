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
    public partial class TraceroutePage : UserControl
    {
        private readonly ObservableCollection<TracerouteHop> _hops = new();
        private CancellationTokenSource? _cts;

        public TraceroutePage() { InitializeComponent(); HopsGrid.ItemsSource = _hops; HopsGrid.SelectionChanged += HopsGrid_SelectionChanged; }

        private void HopsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HopsGrid.SelectedItem is TracerouteHop hop && hop.GeoDetail != null) TxtDetail.Text = hop.GeoDetail.DetailText;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            string target = TxtTarget.Text.Trim(); if (string.IsNullOrEmpty(target)) return;
            _hops.Clear(); BtnStart.IsEnabled = false; BtnCancel.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource(); TxtDetail.Text = $"正在追踪到 {target} 的路由...\n";

            try
            {
                var destAddresses = await Dns.GetHostAddressesAsync(target);
                if (destAddresses.Length == 0) { TxtDetail.Text = $"无法解析 {target}"; return; }
                IPAddress destIp = destAddresses[0];
                const int maxHops = 30, timeoutMs = 3000, probesPerHop = 30;
                var buffer = new byte[32];

                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var probeTasks = Enumerable.Range(0, probesPerHop).Select(_ => Task.Run(async () => { try { using var p = new Ping(); return await p.SendPingAsync(destIp, timeoutMs, buffer, new PingOptions { Ttl = ttl, DontFragment = true }); } catch { return (PingReply?)null; } })).ToArray();
                    try { await Task.WhenAll(probeTasks); } catch (OperationCanceledException) { throw; } catch { }
                    _cts.Token.ThrowIfCancellationRequested();

                    string? bestIp = null; long bestLatency = -1; IPStatus bestStatus = IPStatus.Unknown; bool reached = false;
                    foreach (var t in probeTasks)
                    {
                        var reply = t.IsCompletedSuccessfully ? t.Result : null; if (reply == null) continue;
                        if (reply.Status == IPStatus.Success) { bestIp = reply.Address.ToString(); bestLatency = bestLatency < 0 ? reply.RoundtripTime : Math.Min(bestLatency, reply.RoundtripTime); bestStatus = IPStatus.Success; reached = true; }
                        else if (reply.Status == IPStatus.TtlExpired) { bestIp = reply.Address.ToString(); bestLatency = bestLatency < 0 ? reply.RoundtripTime : Math.Min(bestLatency, reply.RoundtripTime); if (bestStatus == IPStatus.Unknown) bestStatus = IPStatus.TtlExpired; }
                        else if (bestStatus == IPStatus.Unknown) bestStatus = reply.Status;
                    }

                    var geo = IpGeoService.Instance.Query(bestIp ?? "*");
                    var hopData = new TracerouteHop { Hop = ttl, Ip = bestIp ?? "*", SimpleGeo = geo.SimpleGeo, Latency = bestLatency >= 0 ? $"{bestLatency}ms" : "*", Status = reached ? "已到达" : bestStatus == IPStatus.TtlExpired ? "中转节点" : bestStatus == IPStatus.TimedOut ? "超时" : bestStatus.ToString(), GeoDetail = geo };
                    _hops.Add(hopData); HopsGrid.ScrollIntoView(hopData);
                    if (reached) { TxtDetail.Text = $"追踪完成，共 {ttl} 跳，到达 {destIp}。"; break; }
                    if (ttl == maxHops) TxtDetail.Text = $"已达最大跳数 ({maxHops})，未到达目标 {destIp}。";
                }
            }
            catch (OperationCanceledException) { TxtDetail.Text = $"路由追踪已取消，已探测 {_hops.Count} 跳。"; }
            catch (Exception ex) { TxtDetail.Text = $"追踪出错: {ex.Message}"; }
            finally { _cts?.Dispose(); _cts = null; BtnStart.IsEnabled = true; BtnCancel.Visibility = Visibility.Collapsed; }
        }
    }
}
