using System.Windows;
using MicaWPF.Controls;
using FreeWPFShell.Share;
using System.Diagnostics;

namespace FreeWPFShell.Pages
{
    public partial class SettingsWindow : MicaWindow
    {
        private SshManager.SshConnectionManager _sshManager;

        public SettingsWindow()
        {
            InitializeComponent();
            _sshManager = new SshManager.SshConnectionManager();
            TogVault.IsChecked = _sshManager.Settings.UseWindowsHello;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (TogVault.IsChecked.HasValue)
            {
                _sshManager.Settings.UseWindowsHello = TogVault.IsChecked.Value;
                _sshManager.SaveSettings();
            }
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
