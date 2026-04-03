using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace FreeWPFShell.UserForm
{
    public class BoolToLockConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? "🔒" : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public partial class KeyManagerWindow
    {
        private readonly KeyRepository _keyRepo = new();

        public KeyManagerWindow()
        {
            InitializeComponent();

            var settingsRepo = new Repositories.SettingsRepository();
            BackdropService.ApplyToAllWindows(settingsRepo.Load().BackdropType);

            RefreshList();
        }

        private void RefreshList()
        {
            _keyRepo.Reload();
            KeyGrid.ItemsSource = _keyRepo.GetAll();
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "SSH 密钥文件|id_rsa;id_ed25519;id_ecdsa;*.pem;*.key|所有文件|*.*",
                Title = "选择 SSH 私钥文件"
            };
            if (dlg.ShowDialog() != true) return;

            string filePath = dlg.FileName;
            string defaultName = System.IO.Path.GetFileName(filePath);

            // 先尝试无密码导入
            try
            {
                var key = _keyRepo.Import(filePath, defaultName, null);
                ModernMessageBox.Show($"✅ 密钥 \"{key.Name}\" 导入成功！", "导入成功");
                RefreshList();
                return;
            }
            catch (InvalidOperationException)
            {
                // 需要密码，继续
            }
            catch (Renci.SshNet.Common.SshPassPhraseNullOrEmptyException)
            {
                // 需要密码，继续
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"❌ 导入失败: {ex.Message}", "错误");
                return;
            }

            // 密钥有密码保护，弹窗输入
            var passphraseDlg = new PassphraseDialog();
            passphraseDlg.Owner = this;
            if (passphraseDlg.ShowDialog() != true) return;

            try
            {
                var key = _keyRepo.Import(filePath, defaultName, passphraseDlg.Passphrase);
                ModernMessageBox.Show($"✅ 密钥 \"{key.Name}\" 导入成功！\n密钥密码已加密保存。", "导入成功");
                RefreshList();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"❌ 导入失败: {ex.Message}\n\n可能是密钥密码错误。", "错误");
            }
        }

        private void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if (KeyGrid.SelectedItem is SshKeyInfo key)
            {
                if (ModernMessageBox.Show(
                    $"确定要删除密钥 \"{key.Name}\" 吗？\n\n删除后使用此密钥的连接将无法登录。",
                    "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _keyRepo.Delete(key.Id);
                    RefreshList();
                }
            }
        }
    }
}
