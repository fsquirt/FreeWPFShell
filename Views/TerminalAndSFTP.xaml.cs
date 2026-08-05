using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.UserForm;
using FreeWPFShell.ViewModels;
using Microsoft.Terminal.Wpf;
using Renci.SshNet;
using SshNetException = Renci.SshNet.Common.SshException;

namespace FreeWPFShell.Views
{
    /// <summary>
    /// 终端 + SFTP 页。SFTP 数据与传输逻辑已迁移到 TerminalViewModel，
    /// Code-behind 保留终端原生控件（Microsoft.Terminal.Wpf）交互与 SFTP 视图对接。
    /// </summary>
    public partial class TerminalAndSFTP : UserControl
    {
        public TerminalViewModel ViewModel { get; }

        public SshSessionService? Session { get; private set; }

        private PropertyChangedEventHandler? _sessionPropertyChangedHandler;

        public TerminalAndSFTP(SshSessionService session)
        {
            InitializeComponent();
            Session = session;
            ViewModel = new TerminalViewModel(session);
            DataContext = ViewModel;

            FileGrid.ItemsSource = ViewModel.Files;

            // 注入 UI 回调
            ViewModel.ShowMessage = (msg, title) => ModernMessageBox.Show(msg, title);
            ViewModel.Confirm = (msg, title) => ModernMessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            ViewModel.TransferStateChanged = UpdateStatusIcon;

            _sessionPropertyChangedHandler = (s, e) =>
            {
                var sess = Session;
                if (sess == null) return;
                Dispatcher.Invoke(() =>
                {
                    if (e.PropertyName == nameof(SshSessionService.ConnectionStatus))
                    {
                        TxtConnStatus.Text = sess.ConnectionStatus;
                    }
                    else if (e.PropertyName == nameof(SshSessionService.IsAppCursorMode))
                    {
                        TxtCursorMode.Text = sess.IsAppCursorMode ? "APP MODE" : "NORMAL MODE";
                    }
                    else if (e.PropertyName == nameof(SshSessionService.IsSftpConnected))
                    {
                        if (sess.IsSftpConnected)
                        {
                            // SFTP 已连接：加载远程文件列表，并清除"SFTP 连接中"转圈状态
                            ViewModel.BindSftp();
                            UpdateStatusIcon();
                        }
                    }
                });
            };
            Session.PropertyChanged += _sessionPropertyChangedHandler;
        }

        /// <summary>关 Tab 时必须调用，断开引用链并释放资源。</summary>
        public void Cleanup()
        {
            if (Session != null && _sessionPropertyChangedHandler != null)
            {
                Session.PropertyChanged -= _sessionPropertyChangedHandler;
                _sessionPropertyChangedHandler = null;
            }

            if (Session?.TerminalConnection != null)
            {
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;
            }

            Terminal.Connection = null;
            ViewModel.CancelAllTransfersCommand.Execute(null);
            FileGrid.ItemsSource = null;
            ViewModel.Files.Clear();
            Session = null;
        }

        // ── 终端 ─────────────────────────────────────────────────

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            var settings = new Repositories.SettingsRepository().Load();
            var bgColorStr = settings.TerminalBackground ?? "#1E3047";
            uint bgColorUint = 0x0047301E;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(bgColorStr);
                Terminal.Background = new SolidColorBrush(color);
                bgColorUint = (uint)((color.B << 16) | (color.G << 8) | color.R);
            }
            catch { Terminal.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x30, 0x47)); }

            if (settings.UseImageBackground && !string.IsNullOrEmpty(settings.ImageBackgroundPath))
            {
                Terminal.PixelShaderImagePath = settings.ImageBackgroundPath;
                Terminal.PixelShaderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "background_blur.hlsl");
                Terminal.PixelShaderImageStretchMode = (Microsoft.Terminal.Wpf.PixelShaderImageStretchMode)settings.ImageStretchMode;
                Terminal.ShowScrollBar = false;
            }
            else
            {
                Terminal.ClearPixelShaderBackground();
                Terminal.ShowScrollBar = true;
            }

            Terminal.SetTheme(new()
            {
                DefaultBackground = bgColorUint, DefaultForeground = 0x00ffffff,
                DefaultSelectionBackground = 0x00ffffff, CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = new uint[16] { 0x000c0c0c,0x001f0fc5,0x000ea113,0x00009cc1,0x00da3700,0x00981788,0x00dd963a,0x00cccccc,0x00767676,0x005648e7,0x000cc616,0x00a5f1f9,0x00ff783b,0x009e00b4,0x00d6d661,0x00f2f2f2 }
            }, settings.TerminalFont ?? "Cascadia Code", (short)(settings.TerminalFontSize > 0 ? settings.TerminalFontSize : 10));

            TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line;
            TxtStatusIcon.Spin = true;
            StatusIconContainer.ToolTip = "Connecting...";
        }

        public void BindSession()
        {
            if (Session?.IsConnected != true)
            {
                TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CloseCircleLine;
                TxtStatusIcon.Spin = false;
                TxtStatusIcon.Foreground = Brushes.Red;
                StatusIconContainer.ToolTip = "未连接";
                return;
            }

            Terminal.Connection = Session.TerminalConnection;

            if (Session.TerminalConnection != null)
            {
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;
                Session.TerminalConnection.ConnectionLost += TerminalConnection_ConnectionLost;
            }

            Dispatcher.InvokeAsync(() => {
                Session.TerminalConnection?.Resize((uint)Terminal.Rows, (uint)Terminal.Columns);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            Terminal.Focus();

            if (Session.IsSftpConnected)
            {
                ViewModel.BindSftp();
                UpdateStatusIcon();
            }
            else
            {
                TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line;
                TxtStatusIcon.Spin = true;
                TxtStatusIcon.Foreground = Brushes.Orange;
                StatusIconContainer.ToolTip = "SFTP 连接中...";
            }
        }

        private void Terminal_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool isApp = Session?.IsAppCursorMode ?? false;
            string? input = e.Key switch
            {
                Key.Tab => "\t",
                Key.Up => isApp ? "\x1bOA" : "\x1b[A",
                Key.Down => isApp ? "\x1bOB" : "\x1b[B",
                Key.Right => isApp ? "\x1bOC" : "\x1b[C",
                Key.Left => isApp ? "\x1bOD" : "\x1b[D",
                _ => null
            };
            if (input != null) { e.Handled = true; Session?.TerminalConnection?.WriteInput(input); }
        }

        private void BtnTermCopy_Click(object sender, RoutedEventArgs e)
        {
            string selectedText = Terminal.GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                try { Clipboard.SetText(selectedText); }
                catch (Exception ex) { ModernMessageBox.Show("复制失败: " + ex.Message); }
            }
        }

        private void TerminalConnection_ConnectionLost(object? sender, System.EventArgs e)
        {
            Session?.TerminalConnection?.ConnectionLost -= TerminalConnection_ConnectionLost;
            HandleConnectionLost();
        }

        private void HandleConnectionLost()
        {
            Dispatcher.InvokeAsync(() =>
            {
                ModernMessageBox.Show(
                    "与服务器的连接已断开，将关闭当前终端窗口。",
                    "连接已断开",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Error);

                var mainWindow = Application.Current.MainWindow as MainForm;
                if (mainWindow != null) mainWindow.CloseTab(this);
            });
        }

        // ── SFTP 视图对接 ───────────────────────────────────────

        private void UpdateStatusIcon()
        {
            Dispatcher.InvokeAsync(() =>
            {
                bool transferring = ViewModel.IsTransferring;
                if (transferring)
                {
                    TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line;
                    TxtStatusIcon.Spin = true;
                    TxtStatusIcon.Foreground = Brushes.Gold;
                }
                else
                {
                    TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CheckboxCircleLine;
                    TxtStatusIcon.Spin = false;
                    TxtStatusIcon.Foreground = Brushes.LimeGreen;
                }
                StatusIconContainer.ToolTip = ViewModel.StatusText;
            });
        }

        private void TxtStatusIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && (ViewModel.IsTransferring))
            {
                if (ModernMessageBox.Show("确定要中断当前所有的传输任务吗？", "中断传输", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    ViewModel.CancelAllTransfersCommand.Execute(null);
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => ViewModel.GoBackCommand.Execute(null);
        private void BtnForward_Click(object sender, RoutedEventArgs e) => ViewModel.GoForwardCommand.Execute(null);
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshCommand.Execute(null);
        private void BtnUp_Click(object sender, RoutedEventArgs e) => ViewModel.GoUpCommand.Execute(null);
        private void BtnNewFolder_Click(object sender, RoutedEventArgs e) => ViewModel.NewFolderCommand.Execute(null);

        private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void FileGrid_Drop(object sender, DragEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files == null || files.Length == 0) return;
            foreach (var file in files) ViewModel.UploadLocalItem(file, ViewModel.CurrentPath);
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
            if (ModernMessageBox.Show($"确认删除选中的 {items.Count} 个项吗？\n文件夹将会被强行递归删除！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                ViewModel.Delete(items);
        }

        private void CtxGridRename_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                string? newName = RenameDialog(f.Name);
                if (newName != null) ViewModel.Rename(f, newName);
            }
        }

        private string? RenameDialog(string original)
        {
            var dlg = new RenameDialog(original) { Owner = Window.GetWindow(this) };
            return dlg.ShowDialog() == true ? dlg.NewName : null;
        }

        private void CtxGridCopy_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList();
            if (items.Count == 0) return;
            try { Clipboard.SetText(ViewModel.BuildCopyText(items)); }
            catch (Exception ex) { ModernMessageBox.Show("复制到剪切板失败: " + ex.Message); }
        }

        private async void CtxGridPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string? f in Clipboard.GetFileDropList())
                    {
                        if (f != null) ViewModel.UploadLocalItem(f, ViewModel.CurrentPath);
                    }
                }
                else if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText() ?? "";
                    if (text.StartsWith("FreeWPFRemoteCopy|") && !text.StartsWith($"FreeWPFRemoteCopy|{Session?.HostInfo?.Id}|"))
                    {
                        ModernMessageBox.Show("抱歉，目前不允许跨服务器进行一键复制粘贴。", "提示");
                    }
                    else
                    {
                        ViewModel.Paste(text);
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("访问剪切板失败: " + ex.Message);
            }
        }

        private void BtnUpload_Click(object sender, RoutedEventArgs e) => BtnUpload.ContextMenu.IsOpen = true;

        private void CtxUploadFile_Click(object sender, RoutedEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
            if (dlg.ShowDialog() == true) foreach (string f in dlg.FileNames) ViewModel.UploadLocalItem(f, ViewModel.CurrentPath);
        }

        private void CtxUploadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Session?.SftpClient == null || !Session.SftpClient.IsConnected) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要上传的文件夹" };
            if (dlg.ShowDialog() == true) ViewModel.UploadLocalItem(dlg.FolderName, ViewModel.CurrentPath);
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e)
            => _ = ViewModel.DownloadAsync(FileGrid.SelectedItems.Cast<RemoteFile>().ToList(), GetDownloadDir());

        private void BtnSshTunnel_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainForm)?.OpenSshTunnelManager();
        private void BtnTraceroute_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainForm)?.OpenTraceroutePage(Session?.HostInfo?.IpAddress);
        private void BtnSysManagement_Click(object sender, RoutedEventArgs e) { if (Session != null) (Application.Current.MainWindow as MainForm)?.OpenSystemManagementPage(Session); }
    }
}
