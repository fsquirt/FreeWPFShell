using FreeWPFShell.Services;
using System.Windows;

namespace FreeWPFShell.UserForm
{
    public partial class PassphraseDialog
    {
        public string Passphrase { get; private set; } = string.Empty;

        public PassphraseDialog()
        {
            InitializeComponent();

            var settingsRepo = new Repositories.SettingsRepository();
            BackdropService.ApplyToAllWindows(settingsRepo.Load().BackdropType);

            TxtPassphrase.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Passphrase = TxtPassphrase.Password;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
