using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.Share;

namespace FreeWPFShell.Views
{
    public partial class SystemManagementPage : UserControl
    {
        private readonly SshSessionService _session;
        private readonly ObservableCollection<LoginRecord> _wtmpRecords = new();
        private readonly ObservableCollection<LoginRecord> _btmpRecords = new();

        public SystemManagementPage(SshSessionService session)
        {
            InitializeComponent();
            _session = session;
            WtmpGrid.ItemsSource = _wtmpRecords;
            BtmpGrid.ItemsSource = _btmpRecords;

            _ = LoadWtmpAsync();
            _ = LoadBtmpAsync();
        }

        private async Task LoadWtmpAsync()
        {
            _wtmpRecords.Clear();
            try
            {
                if (!int.TryParse(TxtWtmpCount.Text, out int count) || count <= 0) count = 100;
                var records = await _session.GetLoginRecordsAsync($"/wtmp?count={count}");
                FillGeoAndAdd(records, _wtmpRecords);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取登录记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadBtmpAsync()
        {
            _btmpRecords.Clear();
            try
            {
                if (!int.TryParse(TxtBtmpCount.Text, out int count) || count <= 0) count = 100;
                var records = await _session.GetLoginRecordsAsync($"/btmp?count={count}");
                FillGeoAndAdd(records, _btmpRecords);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"读取登录失败记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FillGeoAndAdd(List<LoginRecord> records, ObservableCollection<LoginRecord> target)
        {
            var geoService = IpGeoService.Instance;
            foreach (var r in records)
            {
                // timestamp 是 UTC，转为本地时区显示
                if (r.Timestamp > 0)
                    r.Time = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                if (!string.IsNullOrEmpty(r.Ip) && r.Ip != "(本地)")
                {
                    try { r.Geo = geoService.Query(r.Ip).SimpleGeo; } catch { r.Geo = ""; }
                }
                else
                {
                    r.Geo = "本地";
                }
                target.Add(r);
            }
        }

        private void BtnRefreshWtmp_Click(object sender, RoutedEventArgs e) => _ = LoadWtmpAsync();
        private void BtnRefreshBtmp_Click(object sender, RoutedEventArgs e) => _ = LoadBtmpAsync();

        private async void BtnExportWtmp_Click(object sender, RoutedEventArgs e) => await ExportCsvAsync("/wtmp", "登录记录");
        private async void BtnExportBtmp_Click(object sender, RoutedEventArgs e) => await ExportCsvAsync("/btmp", "登录失败记录");

        private async Task ExportCsvAsync(string endpoint, string title)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}",
                Title = $"导出{title}"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                // 导出全部记录，不传count参数
                var records = await _session.GetLoginRecordsAsync(endpoint);
                var geoService = IpGeoService.Instance;
                var sb = new StringBuilder();
                sb.AppendLine("登录时间,登录用户,登录来源(IP),IP归属地");
                foreach (var r in records)
                {
                    string time = r.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : r.Time;
                    string geo;
                    if (!string.IsNullOrEmpty(r.Ip) && r.Ip != "(本地)")
                    {
                        try { geo = geoService.Query(r.Ip).SimpleGeo; } catch { geo = ""; }
                    }
                    else
                    {
                        geo = "本地";
                    }
                    sb.AppendLine($"{EscapeCsv(time)},{EscapeCsv(r.User)},{EscapeCsv(r.Ip)},{EscapeCsv(geo)}");
                }
                await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), Encoding.UTF8);
                UserForm.ModernMessageBox.Show($"已导出 {records.Count} 条记录到:\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string? field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }
    }
}
