using System.Windows;
using System.Windows.Controls;
using MicaWPF.Controls;
using MicaWPF.Core.Extensions;
using FreeWPFShell.Share;
using System.Diagnostics;

namespace FreeWPFShell.UserForm
{
    public partial class SettingsWindow : MicaWindow
    {
        private readonly SshManager.SshConnectionManager _sshManager;

        public SettingsWindow()
        {
            InitializeComponent();
            _sshManager = new SshManager.SshConnectionManager();
            TogVault.IsChecked = _sshManager.Settings.UseWindowsHello;
            TogLinuxMonitor.IsChecked = _sshManager.Settings.UseLinuxMonitor;

            // Load backdrop type
            string backdrop = _sshManager.Settings.BackdropType;
            foreach (ComboBoxItem item in CmbBackdrop.Items)
            {
                if (item.Tag?.ToString() == backdrop)
                {
                    CmbBackdrop.SelectedItem = item;
                    break;
                }
            }
        }

        private void CmbBackdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBackdrop.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _sshManager.Settings.BackdropType = tag;
                _sshManager.SaveSettings();
                ApplyBackdropToAllWindows(tag);
            }
        }

        private static void ApplyBackdropToAllWindows(string type)
        {
            try
            {
                var backdrop = type switch
                {
                    "Mica" => MicaWPF.Core.Enums.BackdropType.Mica,
                    "Acrylic" => MicaWPF.Core.Enums.BackdropType.Acrylic,
                    "Tabbed" => MicaWPF.Core.Enums.BackdropType.Tabbed,
                    _ => MicaWPF.Core.Enums.BackdropType.None
                };

                foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
                {
                    if (w is MicaWindow mw)
                    {
                        w.EnableBackdrop(backdrop);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] ApplyBackdrop error: {ex.Message}");
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (TogVault.IsChecked.HasValue)
            {
                _sshManager.Settings.UseWindowsHello = TogVault.IsChecked.Value;
            }
            if (TogLinuxMonitor.IsChecked.HasValue)
            {
                _sshManager.Settings.UseLinuxMonitor = TogLinuxMonitor.IsChecked.Value;
            }
            _sshManager.SaveSettings();
            base.OnClosing(e);
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch { }
        }
    }
}
