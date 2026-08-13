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
using YouShell.Terminal;
using YouShell.UserForm;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// 终端 + SFTP 页。SFTP 逻辑在 TerminalViewModel，Code-behind 负责终端控件与视图对接。
    /// </summary>
    public sealed partial class TerminalAndSFTP : UserControl
    {
        public TerminalViewModel ViewModel { get; }
        public SshSessionService? Session { get; private set; }

        private PropertyChangedEventHandler? _sessionPropertyChangedHandler;

        // 由 MainWindow 注入的导航回调
        public Action? OpenSshTunnelRequested { get; set; }
        public Action<string?>? OpenTracerouteRequested { get; set; }
        public Action<SshSessionService>? OpenSystemManagementRequested { get; set; }
        public Action<TerminalAndSFTP>? CloseTabRequested { get; set; }

        public TerminalAndSFTP(SshSessionService session)
        {
            InitializeComponent();
            Session = session;
            ViewModel = new TerminalViewModel(session);
            DataContext = ViewModel;

            FileGrid.ItemsSource = ViewModel.Files;

            ViewModel.ShowMessage = (msg, title) => _ = ModernMessageBox.ShowAsync(msg, title);
            ViewModel.Confirm = (msg, title) => ConfirmAsync(msg, title);
            ViewModel.TransferStateChanged = UpdateStatusIcon;

            _sessionPropertyChangedHandler = (s, e) =>
            {
                var sess = Session;
                if (sess == null) return;
                YouShell.Core.UiDispatcher.Run(() =>
                {
                    if (e.PropertyName == nameof(SshSessionService.ConnectionStatus))
                        TxtConnStatus.Text = sess.ConnectionStatus;
                    else if (e.PropertyName == nameof(SshSessionService.IsAppCursorMode))
                        TxtCursorMode.Text = sess.IsAppCursorMode ? "APP MODE" : "NORMAL MODE";
                    else if (e.PropertyName == nameof(SshSessionService.IsSftpConnected) && sess.IsSftpConnected)
                    {
                        ViewModel.BindSftp();
                        UpdateStatusIcon();
                    }
                });
            };
            Session.PropertyChanged += _sessionPropertyChangedHandler;
        }

        private async System.Threading.Tasks.Task<bool> ConfirmAsync(string msg, string title)
            => await ModernMessageBox.ShowAsync(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        /// <summary>关 Tab 时必须调用，断开引用链并释放资源。</summary>
        public void Cleanup()
        {
            if (Session != null && _sessionPropertyChangedHandler != null)
            {
                Session.PropertyChanged -= _sessionPropertyChangedHandler;
                _sessionPropertyChangedHandler = null;
            }
            if (Session?.TerminalConnection != null)
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;

            Terminal.Connection = null;
            Terminal.DisposeHost();
            ViewModel.CancelAllTransfersCommand.Execute(null);
            FileGrid.ItemsSource = null;
            ViewModel.Files.Clear();
            Session = null;
        }

        // ── 终端 ─────────────────────────────────────────────────

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            var settings = new Repositories.SettingsRepository().Load();
            uint bg = ParseHexColor(settings.TerminalBackground);

            if (settings.UseImageBackground && !string.IsNullOrEmpty(settings.ImageBackgroundPath))
            {
                Terminal.PixelShaderImagePath = settings.ImageBackgroundPath;
                Terminal.PixelShaderPath = Path.Combine(AppContext.BaseDirectory, "background_blur.hlsl");
                Terminal.PixelShaderImageStretchMode = (YouShell.Terminal.PixelShaderImageStretchMode)settings.ImageStretchMode;
            }
            else
            {
                Terminal.ClearPixelShaderBackground();
            }

            Terminal.SetTheme(new TerminalTheme
            {
                DefaultBackground = bg,
                DefaultForeground = 0x00ffffff,
                DefaultSelectionBackground = 0x00ffffff,
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = new uint[16]
                {
                    0x000c0c0c, 0x001f0fc5, 0x000ea113, 0x00009cc1, 0x00da3700, 0x00981788, 0x00dd963a, 0x00cccccc,
                    0x00767676, 0x005648e7, 0x000cc616, 0x00a5f1f9, 0x00ff783b, 0x009e00b4, 0x00d6d661, 0x00f2f2f2
                }
            }, settings.TerminalFont ?? "Cascadia Code", (short)(settings.TerminalFontSize > 0 ? settings.TerminalFontSize : 10));

            TxtStatusIcon.Glyph = ""; // 同步图标：连接中
            ToolTipService.SetToolTip(TxtStatusIcon, "Connecting...");
        }

        private static uint ParseHexColor(string? s)
        {
            s = s?.Trim().TrimStart('#') ?? "";
            if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return v; // 0xRRGGBB
            return 0x1E3047;
        }

        public void BindSession()
        {
            if (Session?.IsConnected != true)
            {
                TxtStatusIcon.Glyph = ""; // 错误/取消
                TxtStatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                ToolTipService.SetToolTip(TxtStatusIcon, "未连接");
                return;
            }

            Terminal.Connection = Session.TerminalConnection;

            if (Session.TerminalConnection != null)
            {
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;
                Session.TerminalConnection.ConnectionLost += TerminalConnection_ConnectionLost;
            }

            Terminal.FocusTerminal();

            if (Session.IsSftpConnected)
            {
                ViewModel.BindSftp();
                UpdateStatusIcon();
            }
            else
            {
                TxtStatusIcon.Glyph = "";
                TxtStatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                ToolTipService.SetToolTip(TxtStatusIcon, "SFTP 连接中...");
            }
        }

        private void BtnTermCopy_Click(object sender, RoutedEventArgs e)
        {
            string selectedText = Terminal.GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                var dp = new DataPackage();
                dp.SetText(selectedText);
                Clipboard.SetContent(dp);
            }
        }

        private void TerminalConnection_ConnectionLost(object? sender, EventArgs e)
        {
            Session?.TerminalConnection?.ConnectionLost -= TerminalConnection_ConnectionLost;
            HandleConnectionLost();
        }

        private async void HandleConnectionLost()
        {
            await ModernMessageBox.ShowAsync("与服务器的连接已断开，将关闭当前终端窗口。", "连接已断开", MessageBoxButton.OKCancel, MessageBoxImage.Error);
            CloseTabRequested?.Invoke(this);
        }

        // ── SFTP 视图对接 ───────────────────────────────────────

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

        private void BtnSshTunnel_Click(object sender, RoutedEventArgs e) => OpenSshTunnelRequested?.Invoke();
        private void BtnTraceroute_Click(object sender, RoutedEventArgs e) => OpenTracerouteRequested?.Invoke(Session?.HostInfo?.IpAddress);
        private void BtnSysManagement_Click(object sender, RoutedEventArgs e)
        {
            if (Session != null) OpenSystemManagementRequested?.Invoke(Session);
        }
    }
}
