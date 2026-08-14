using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YouShell.UserForm
{
    /// <summary>
    /// 重命名对话框：等价于 WPF 的 RenameDialog，底层用 WinUI 3 ContentDialog。
    /// 调用方：<c>var dlg = new RenameDialog(oldName);
    /// if (await dlg.ShowAsync() == ContentDialogResult.Primary) use(dlg.NewName);</c>
    /// </summary>
    public sealed partial class RenameDialog : ContentDialog
    {
        public string NewName { get; private set; } = string.Empty;

        public RenameDialog(string oldName)
        {
            InitializeComponent();
            XamlRoot = ModernMessageBox.Root;
            ModernMessageBox.SyncTheme(this);
            TxtNewName.Text = oldName;
            TxtNewName.SelectAll();
            TxtNewName.Focus(FocusState.Programmatic);
        }

        private void RenameDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(TxtNewName.Text))
            {
                args.Cancel = true;
                _ = ModernMessageBox.ShowAsync("新文件名不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtNewName.Focus(FocusState.Programmatic);
                return;
            }
            NewName = TxtNewName.Text;
        }
    }
}
