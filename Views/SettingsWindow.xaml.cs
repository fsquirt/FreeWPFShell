using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MicaWPF.Controls;
using MicaWPF.Core.Extensions;
using FreeWPFShell.Repositories;

namespace FreeWPFShell.Views
{
    public partial class SettingsWindow : MicaWindow
    {
        private readonly SettingsRepository _settingsRepo = new();

        public SettingsWindow()
        {
            InitializeComponent();
            var settings = _settingsRepo.Load();
            TogVault.IsChecked = settings.UseWindowsHello;
            TogLinuxMonitor.IsChecked = settings.UseLinuxMonitor;
            TxtTerminalBg.Text = settings.TerminalBackground;
            TogImageBg.IsChecked = settings.UseImageBackground;
            TxtImagePath.Text = settings.ImageBackgroundPath;
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
            settings.TerminalBackground = TxtTerminalBg.Text.Trim();
            settings.UseImageBackground = TogImageBg.IsChecked ?? false;
            settings.ImageBackgroundPath = TxtImagePath.Text;
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

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); e.Handled = true; } catch { }
        }
    }
}
