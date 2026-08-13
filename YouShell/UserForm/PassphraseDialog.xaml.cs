using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YouShell.UserForm
{
    /// <summary>
    /// SSH 密钥密码输入对话框（WinUI 3 ContentDialog）。
    /// </summary>
    public sealed partial class PassphraseDialog : ContentDialog
    {
        public string Passphrase { get; private set; } = string.Empty;

        public PassphraseDialog()
        {
            InitializeComponent();
            TxtPassphrase.Focus(FocusState.Programmatic);
        }

        private void PassphraseDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Passphrase = TxtPassphrase.Password;
        }
    }
}
