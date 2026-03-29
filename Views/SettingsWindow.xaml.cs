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

            foreach (ComboBoxItem item in CmbBackdrop.Items)
                if (item.Tag?.ToString() == settings.BackdropType) { CmbBackdrop.SelectedItem = item; break; }
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
            _settingsRepo.Save(settings);
            base.OnClosing(e);
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); e.Handled = true; } catch { }
        }
    }
}
