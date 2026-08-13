using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using YouShell.Models;
using YouShell.Services;
using YouShell.UserForm;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// SFTP 文件管理页。复用终端页共享的 <see cref="TerminalViewModel"/>（SFTP 逻辑），
    /// Code-behind 负责文件列表与传输状态视图对接。
    /// </summary>
    public sealed partial class SftpPage : UserControl
    {
        public TerminalViewModel ViewModel { get; }
        public SshSessionService? Session { get; private set; }

        private PropertyChangedEventHandler? _sessionPropertyChangedHandler;

        public SftpPage(SshSessionService session, TerminalViewModel viewModel)
        {
            InitializeComponent();
            Session = session;
            ViewModel = viewModel;
            DataContext = viewModel;
            FileGrid.ItemsSource = viewModel.Files;

            viewModel.ShowMessage = (msg, title) => _ = ModernMessageBox.ShowAsync(msg, title);
            viewModel.Confirm = (msg, title) => ConfirmAsync(msg, title);
            viewModel.TransferStateChanged += UpdateStatusIcon;

            _sessionPropertyChangedHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(SshSessionService.IsSftpConnected) && session.IsSftpConnected)
                {
                    YouShell.Core.UiDispatcher.Run(() =>
                    {
                        ViewModel.BindSftp();
                        UpdateStatusIcon();
                    });
                }
            };
            session.PropertyChanged += _sessionPropertyChangedHandler;

            // 打开时若 SFTP 已就绪则立即加载
            if (session.IsSftpConnected)
            {
                ViewModel.BindSftp();
                UpdateStatusIcon();
            }
        }

        private async System.Threading.Tasks.Task<bool> ConfirmAsync(string msg, string title)
            => await ModernMessageBox.ShowAsync(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        /// <summary>关 Tab 时解除绑定（共享 VM 与 Session 由终端页负责销毁）。</summary>
        public void Cleanup()
        {
            if (Session != null && _sessionPropertyChangedHandler != null)
            {
                Session.PropertyChanged -= _sessionPropertyChangedHandler;
                _sessionPropertyChangedHandler = null;
            }
            ViewModel.TransferStateChanged -= UpdateStatusIcon;
            FileGrid.ItemsSource = null;
            Session = null;
        }

        // ── 状态图标 ─────────────────────────────────────────────

        private void UpdateStatusIcon()
        {
            YouShell.Core.UiDispatcher.Run(() =>
            {
                if (ViewModel.IsTransferring)
                {
                    TxtStatusIcon.Glyph = "";
                    TxtStatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold);
                }
                else
                {
                    TxtStatusIcon.Glyph = ""; // CheckMark
                    TxtStatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                }
                ToolTipService.SetToolTip(TxtStatusIcon, ViewModel.StatusText);
            });
        }

        private async void StatusIcon_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ViewModel.IsTransferring)
            {
                var r = await ModernMessageBox.ShowAsync("确定要中断当前所有的传输任务吗？", "中断传输", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) ViewModel.CancelAllTransfersCommand.Execute(null);
            }
        }

        // ── 工具栏 ───────────────────────────────────────────────

        private void BtnBack_Click(object sender, RoutedEventArgs e) => ViewModel.GoBackCommand.Execute(null);
        private void BtnForward_Click(object sender, RoutedEventArgs e) => ViewModel.GoForwardCommand.Execute(null);
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshCommand.Execute(null);
        private void BtnUp_Click(object sender, RoutedEventArgs e) => ViewModel.GoUpCommand.Execute(null);
        private void BtnNewFolder_Click(object sender, RoutedEventArgs e) => ViewModel.NewFolderCommand.Execute(null);

        private void FileGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                if (f.IsDirectory) ViewModel.LoadPath(f.FullName);
                else ViewModel.Edit(f, "code");
            }
        }

        private void FileGrid_DragOver(object sender, DragEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy : DataPackageOperation.None;
        }

        private async void FileGrid_Drop(object sender, DragEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected) return;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items) ViewModel.UploadLocalItem(item.Path, ViewModel.CurrentPath);
        }

        private void CtxGridEditVSCode_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f && !f.IsDirectory) ViewModel.Edit(f, "code");
        }

        private void CtxGridEditNotepad_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f && !f.IsDirectory) ViewModel.Edit(f, "notepad");
        }

        private void CtxGridDownload_Click(object sender, RoutedEventArgs e)
            => _ = ViewModel.DownloadAsync(FileGrid.SelectedItems.Cast<RemoteFile>().ToList(), GetDownloadDir());

        private static string GetDownloadDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FreeWPFShell");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private async void CtxGridDelete_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList();
            if (items.Count == 0) return;
            var r = await ModernMessageBox.ShowAsync($"确认删除选中的 {items.Count} 个项吗？\n文件夹将会被强行递归删除！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) ViewModel.Delete(items);
        }

        private async void CtxGridRename_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                var dlg = new RenameDialog(f.Name);
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                    ViewModel.Rename(f, dlg.NewName);
            }
        }

        private void CtxGridCopy_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList();
            if (items.Count == 0) return;
            var dp = new DataPackage();
            dp.SetText(ViewModel.BuildCopyText(items));
            Clipboard.SetContent(dp);
        }

        private async void CtxGridPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await content.GetStorageItemsAsync();
                    foreach (var item in items) ViewModel.UploadLocalItem(item.Path, ViewModel.CurrentPath);
                }
                else if (content.Contains(StandardDataFormats.Text))
                {
                    string text = await content.GetTextAsync() ?? "";
                    if (text.StartsWith("FreeWPFRemoteCopy|") && !text.StartsWith($"FreeWPFRemoteCopy|{Session?.HostInfo?.Id}|"))
                        await ModernMessageBox.ShowAsync("抱歉，目前不允许跨服务器进行一键复制粘贴。", "提示");
                    else
                        ViewModel.Paste(text);
                }
            }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync("访问剪切板失败: " + ex.Message);
            }
        }

        private async void CtxUploadFile_Click(object sender, RoutedEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected) return;
            var files = await PickerHelper.PickMultipleFilesAsync("*");
            if (files == null) return;
            foreach (var f in files) ViewModel.UploadLocalItem(f, ViewModel.CurrentPath);
        }

        private async void CtxUploadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected) return;
            var folder = await PickerHelper.PickFolderAsync();
            if (folder == null) return;
            ViewModel.UploadLocalItem(folder, ViewModel.CurrentPath);
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
            => _ = ViewModel.DownloadAsync(FileGrid.SelectedItems.Cast<RemoteFile>().ToList(), GetDownloadDir());
    }
}
