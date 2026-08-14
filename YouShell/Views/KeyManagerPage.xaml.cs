using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Renci.SshNet.Common;
using YouShell.Models;
using YouShell.Repositories;
using YouShell.UserForm;

namespace YouShell.Views
{
    /// <summary>
    /// SSH 密钥管理对话框（WinUI 3 ContentDialog，加宽）。提供密钥导入/删除。
    /// 反馈使用内联 InfoBar、密码输入使用内联面板，避免在 ContentDialog 内再弹 ContentDialog
    /// （WinUI 3 同一 XamlRoot 仅允许一个 ContentDialog 同时打开）。
    /// </summary>
    public sealed partial class KeyManagerPage : ContentDialog
    {
        private readonly KeyRepository _keyRepo = new();

        // 待导入（有密码保护）密钥的临时状态
        private string? _pendingImportPath;
        private string? _pendingImportName;

        public KeyManagerPage()
        {
            InitializeComponent();
            XamlRoot = ModernMessageBox.Root;
            ModernMessageBox.SyncTheme(this);
            RefreshList();
        }

        private void RefreshList()
        {
            _keyRepo.Reload();
            KeyList.ItemsSource = _keyRepo.GetAll();
        }

        private void ShowInfo(string title, string message, InfoBarSeverity severity)
        {
            FeedbackBar.Title = title;
            FeedbackBar.Message = message;
            FeedbackBar.Severity = severity;
            FeedbackBar.IsOpen = true;
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
                RefreshList();
                ShowInfo("成功", $"密钥 \"{key.Name}\" 导入成功！", InfoBarSeverity.Success);
                return;
            }
            catch (InvalidOperationException) { }
            catch (SshPassPhraseNullOrEmptyException) { }
            catch (Exception ex)
            {
                ShowInfo("错误", "导入失败: " + ex.Message, InfoBarSeverity.Error);
                return;
            }

            // 密钥有密码保护，显示内联密码输入
            _pendingImportPath = filePath;
            _pendingImportName = defaultName;
            TxtPassphrase.Password = "";
            pnlPassphrase.Visibility = Visibility.Visible;
        }

        private async void BtnPassphraseConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingImportPath == null || _pendingImportName == null) return;
            try
            {
                var key = _keyRepo.Import(_pendingImportPath, _pendingImportName, TxtPassphrase.Password);
                RefreshList();
                ShowInfo("成功", $"密钥 \"{key.Name}\" 导入成功！\n密钥密码已加密保存。", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowInfo("错误", "导入失败: " + ex.Message + "\n\n可能是密钥密码错误。", InfoBarSeverity.Error);
            }
            finally
            {
                _pendingImportPath = null;
                _pendingImportName = null;
                pnlPassphrase.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnPassphraseCancel_Click(object sender, RoutedEventArgs e)
        {
            _pendingImportPath = null;
            _pendingImportName = null;
            pnlPassphrase.Visibility = Visibility.Collapsed;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void CtxDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void DeleteSelected()
        {
            if (KeyList.SelectedItem is not SshKeyInfo key)
            {
                ShowInfo("提示", "请先在列表中选择要删除的密钥。", InfoBarSeverity.Informational);
                return;
            }

            _keyRepo.Delete(key.Id);
            RefreshList();
            ShowInfo("成功", $"已删除密钥 \"{key.Name}\"。", InfoBarSeverity.Success);
        }
    }
}
