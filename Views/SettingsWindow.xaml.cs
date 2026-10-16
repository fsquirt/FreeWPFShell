using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicaWPF.Controls;
using MicaWPF.Core.Extensions;
using FreeWPFShell.Repositories;

namespace FreeWPFShell.Views
{
    public partial class SettingsWindow : MicaWindow
    {
        private readonly SettingsRepository _settingsRepo = new();

        private const int DebugOpenClickCount = 5;
        private static readonly TimeSpan DebugClickInterval = TimeSpan.FromSeconds(1.5);

        private int _sysTitleClickCount;
        private DateTime _sysTitleLastClick = DateTime.MinValue;

        public SettingsWindow()
        {
            InitializeComponent();
            var settings = _settingsRepo.Load();
            TogVault.IsChecked = settings.UseWindowsHello;
            TogLinuxMonitor.IsChecked = settings.UseLinuxMonitor;
            TogInjectLocale.IsChecked = settings.InjectChineseLocale;
            TxtTerminalBg.Text = settings.TerminalBackground;
            TogImageBg.IsChecked = settings.UseImageBackground;
            TxtImagePath.Text = settings.ImageBackgroundPath;
            TxtImageBrightness.Text = settings.ImageBrightnessReduce.ToString();
            TxtTraceTimeout.Text = settings.TracerouteTimeout.ToString();
            TxtTraceMaxHops.Text = settings.TracerouteMaxHops.ToString();
            TxtTerminalFont.Text = settings.TerminalFont;
            TxtTerminalFontSize.Text = settings.TerminalFontSize.ToString();

            foreach (ComboBoxItem item in CmbBackdrop.Items)
                if (item.Tag?.ToString() == settings.BackdropType) { CmbBackdrop.SelectedItem = item; break; }

            foreach (ComboBoxItem item in CmbStretch.Items)
                if (item.Tag?.ToString() == settings.ImageStretchMode.ToString()) { CmbStretch.SelectedItem = item; break; }
        }

        private void BtnSelectImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.jpg;*.png;*.bmp;*.jpeg",
                Title = "选择终端背景图片"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtImagePath.Text = dlg.FileName;
            }
        }

        private async void BtnExportData_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出数据",
                Filter = "文本文件|*.txt",
                DefaultExt = ".txt",
                FileName = $"FreeWPFShell_导出_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string report = await Services.DataExportService.BuildReportAsync();
                await System.IO.File.WriteAllTextAsync(dlg.FileName, report, System.Text.Encoding.UTF8);
                UserForm.ModernMessageBox.Show($"已导出到:\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (UnauthorizedAccessException)
            {
                // 用户取消或身份验证失败，直接中止
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnMigrateHello_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (hosts, keys) = await Services.CredentialMigrationService.MigrateToVaultAsync();
                UserForm.ModernMessageBox.Show(BuildMigrationMessage(hosts, keys), "迁移完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show("迁移失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnMigrateDpapi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (hosts, keys) = await Services.CredentialMigrationService.MigrateToDpapiAsync();
                UserForm.ModernMessageBox.Show(BuildMigrationMessage(hosts, keys), "迁移完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (UnauthorizedAccessException)
            {
                // 用户取消或身份验证失败，直接中止
            }
            catch (Exception ex)
            {
                UserForm.ModernMessageBox.Show("迁移失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string BuildMigrationMessage(int hosts, int keys)
            => hosts == 0 && keys == 0
                ? "没有需要迁移的凭据。"
                : $"已迁移主机密码 {hosts} 个，密钥密码 {keys} 个。";

        private void CmbBackdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBackdrop.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var settings = _settingsRepo.Load();
                settings.BackdropType = tag;
                settings.TerminalBackground = TxtTerminalBg?.Text ?? "#1E3047";
                _settingsRepo.Save(settings);
                Services.BackdropService.ApplyToAllWindows(tag);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            var settings = _settingsRepo.Load();
            if (TogVault.IsChecked.HasValue) settings.UseWindowsHello = TogVault.IsChecked.Value;
            if (TogLinuxMonitor.IsChecked.HasValue) settings.UseLinuxMonitor = TogLinuxMonitor.IsChecked.Value;
            if (TogInjectLocale.IsChecked.HasValue) settings.InjectChineseLocale = TogInjectLocale.IsChecked.Value;
            settings.TerminalBackground = TxtTerminalBg.Text.Trim();
            settings.UseImageBackground = TogImageBg.IsChecked ?? false;
            settings.ImageBackgroundPath = TxtImagePath.Text;
            if (int.TryParse(TxtImageBrightness.Text, out int brightness)) settings.ImageBrightnessReduce = Math.Clamp(brightness, 0, 100);
            settings.TerminalFont = TxtTerminalFont.Text.Trim();
            if (int.TryParse(TxtTerminalFontSize.Text, out int fs)) settings.TerminalFontSize = fs;

            if (CmbStretch.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int mode))
            {
                settings.ImageStretchMode = mode;
            }

            if (int.TryParse(TxtTraceTimeout.Text, out int timeout)) settings.TracerouteTimeout = timeout;
            if (int.TryParse(TxtTraceMaxHops.Text, out int hops)) settings.TracerouteMaxHops = hops;

            _settingsRepo.Save(settings);
            base.OnClosing(e);
        }

        private void SysSettingsTitle_Click(object sender, MouseButtonEventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _sysTitleLastClick > DebugClickInterval) _sysTitleClickCount = 0;
            _sysTitleLastClick = now;
            _sysTitleClickCount++;

            if (_sysTitleClickCount < DebugOpenClickCount) return;

            _sysTitleClickCount = 0;

            var mainForm = Owner as MainForm ?? Application.Current.MainWindow as MainForm;
            if (mainForm == null) return;

            mainForm.OpenDebugConsolePage();
            Close();
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); e.Handled = true; } catch { }
        }
    }
}
