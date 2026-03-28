using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Share;

namespace FreeWPFShell.Pages
{
    public class TracerouteHop
    {
        public int Hop { get; set; }
        public string Ip { get; set; } = string.Empty;
        public string SimpleGeo { get; set; } = string.Empty;
        public string Latency { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public IpGeoResult? GeoDetail { get; set; }
    }

    public partial class TraceroutePage : UserControl
    {
        private readonly ObservableCollection<TracerouteHop> _hops = new();
        private CancellationTokenSource? _cts;

        public TraceroutePage()
        {
            InitializeComponent();
            HopsGrid.ItemsSource = _hops;
            HopsGrid.SelectionChanged += HopsGrid_SelectionChanged;
        }

        private void HopsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HopsGrid.SelectedItem is TracerouteHop hop && hop.GeoDetail != null)
            {
                TxtDetail.Text = hop.GeoDetail.DetailText;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            string target = TxtTarget.Text.Trim();
            if (string.IsNullOrEmpty(target)) return;

            _hops.Clear();
            BtnStart.IsEnabled = false;
            BtnCancel.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();
            TxtDetail.Text = $"正在追踪到 {target} 的路由...\n";

            try
            {
                var destAddresses = await Dns.GetHostAddressesAsync(target);
                if (destAddresses.Length == 0)
                {
                    TxtDetail.Text = $"无法解析 {target}";
                    return;
                }

                IPAddress destIp = destAddresses[0];
                Debug.WriteLine($"[Traceroute] Target: {target} -> {destIp}");

                const int maxHops = 30;
                const int timeoutMs = 3000;
                const int probesPerHop = 30;
                var buffer = new byte[32];

                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    // Fire all probes in parallel, each with its own Ping instance
                    var probeTasks = Enumerable.Range(0, probesPerHop).Select(_ => Task.Run(async () =>
                    {
                        try
                        {
                            using var p = new Ping();
                            return await p.SendPingAsync(destIp, timeoutMs, buffer,
                                new PingOptions { Ttl = ttl, DontFragment = true });
                        }
                        catch { return (PingReply?)null; }
                    })).ToArray();

                    // Wait for all probes, checking cancel between completions
                    try
                    {
                        await Task.WhenAll(probeTasks);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Some probes may throw, that's fine
                    }

                    _cts.Token.ThrowIfCancellationRequested();

                    // Collect best result from all probes
                    string? bestIp = null;
                    long bestLatency = -1;
                    IPStatus bestStatus = IPStatus.Unknown;
                    bool reached = false;

                    foreach (var t in probeTasks)
                    {
                        var reply = t.IsCompletedSuccessfully ? t.Result : null;
                        if (reply == null) continue;

                        if (reply.Status == IPStatus.Success)
                        {
                            bestIp = reply.Address.ToString();
                            bestLatency = bestLatency < 0 ? reply.RoundtripTime : Math.Min(bestLatency, reply.RoundtripTime);
                            bestStatus = IPStatus.Success;
                            reached = true;
                        }
                        else if (reply.Status == IPStatus.TtlExpired)
                        {
                            bestIp = reply.Address.ToString();
                            bestLatency = bestLatency < 0 ? reply.RoundtripTime : Math.Min(bestLatency, reply.RoundtripTime);
                            if (bestStatus == IPStatus.Unknown)
                                bestStatus = IPStatus.TtlExpired;
                        }
                        else if (bestStatus == IPStatus.Unknown)
                        {
                            bestStatus = reply.Status;
                        }
                    }

                    string ip = bestIp ?? "*";
                    string latency = bestLatency >= 0 ? $"{bestLatency}ms" : "*";
                    string status;
                    if (reached)
                        status = "已到达";
                    else if (bestStatus == IPStatus.TtlExpired)
                        status = "中转节点";
                    else if (bestStatus == IPStatus.TimedOut)
                        status = "超时";
                    else
                        status = bestStatus.ToString();

                    var geo = IpGeoService.Instance.Query(ip);

                    var hopData = new TracerouteHop
                    {
                        Hop = ttl,
                        Ip = ip,
                        SimpleGeo = geo.SimpleGeo,
                        Latency = latency,
                        Status = status,
                        GeoDetail = geo
                    };
                    _hops.Add(hopData);
                    HopsGrid.ScrollIntoView(hopData);

                    Debug.WriteLine($"[Traceroute] Hop {ttl}: {ip} {latency} {status} {geo.SimpleGeo}");

                    if (reached)
                    {
                        TxtDetail.Text = $"追踪完成，共 {ttl} 跳，到达 {destIp}。点击上方行查看详细信息。";
                        break;
                    }

                    if (ttl == maxHops)
                    {
                        TxtDetail.Text = $"已达最大跳数 ({maxHops})，未到达目标 {destIp}。";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                TxtDetail.Text = $"路由追踪已取消，已探测 {_hops.Count} 跳。";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Traceroute] Exception: {ex}");
                TxtDetail.Text = $"追踪出错: {ex.Message}";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                BtnStart.IsEnabled = true;
                BtnCancel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
