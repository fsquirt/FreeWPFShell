using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YouShell.Models;
using YouShell.Repositories;
using Renci.SshNet.Common;

namespace YouShell.UserForm
{
    /// <summary>
    /// SSH 密钥管理对话框（WinUI 3 ContentDialog）。提供密钥导入/删除。
    /// </summary>
    public sealed partial class KeyManagerWindow : ContentDialog
    {
        private readonly KeyRepository _keyRepo = new();

        public KeyManagerWindow()
        {
            InitializeComponent();
            XamlRoot = ModernMessageBox.Root;
            RefreshList();
        }

        private void RefreshList()
        {
            _keyRepo.Reload();
            KeyList.ItemsSource = _keyRepo.GetAll();
        }

        private async void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            string? filePath = await PickerHelper.PickSingleFileAsync("*");
            if (string.IsNullOrEmpty(filePath)) return;

            string defaultName = System.IO.Path.GetFileName(filePath);

            // 先尝试无密码导入
            try
            {
                var key = _keyRepo.Import(filePath, defaultName, null);
                await ModernMessageBox.ShowAsync($"密钥 \"{key.Name}\" 导入成功！", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshList();
                return;
            }
            catch (InvalidOperationException) { }
            catch (SshPassPhraseNullOrEmptyException) { }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 密钥有密码保护，弹窗输入
            var passphraseDlg = new PassphraseDialog();
            if (await passphraseDlg.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                var key = _keyRepo.Import(filePath, defaultName, passphraseDlg.Passphrase);
                await ModernMessageBox.ShowAsync($"密钥 \"{key.Name}\" 导入成功！\n密钥密码已加密保存。", "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshList();
            }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync($"导入失败: {ex.Message}\n\n可能是密钥密码错误。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if (KeyList.SelectedItem is not SshKeyInfo key) return;

            var result = await ModernMessageBox.ShowAsync(
                $"确定要删除密钥 \"{key.Name}\" 吗？\n\n删除后使用此密钥的连接将无法登录。",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            _keyRepo.Delete(key.Id);
            RefreshList();
        }
    }
}
