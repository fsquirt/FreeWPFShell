using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.Share;
using FreeWPFShell.UserForm;
using Microsoft.Terminal.Wpf;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FreeWPFShell.Views
{
    public partial class TerminalAndSFTP : UserControl
    {
        private readonly ObservableCollection<RemoteFile> _remoteFiles = new();
        private string _currentPath = "/";
        private readonly Stack<string> _backHistory = new(), _forwardHistory = new();
        private int _activeTransfers, _totalFiles, _completedFiles;
        private string _currentTransferName = "";
        private double _currentTransferProgress;

        public SshSessionService Session { get; }
        private SftpClient Sftp => Session.SftpClient!;
        private SshClient Ssh => Session.MasterClient!;

        public TerminalAndSFTP(SshSessionService session)
        {
            InitializeComponent();
            Session = session;

            // 监听连接状态和模式变更以更新 UI
            Session.PropertyChanged += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (e.PropertyName == nameof(SshSessionService.ConnectionStatus))
                    {
                        TxtConnStatus.Text = Session.ConnectionStatus;
                    }
                    else if (e.PropertyName == nameof(SshSessionService.IsAppCursorMode))
                    {
                        TxtCursorMode.Text = Session.IsAppCursorMode ? "APP MODE" : "NORMAL MODE";
                        //TxtCursorMode.Foreground = Session.IsAppCursorMode ? Brushes.Gold : Brushes.LightGray;
                    }
                });
            };
        }

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            var settings = new Repositories.SettingsRepository().Load();
            var bgColorStr = settings.TerminalBackground ?? "#1E3047";
            uint bgColorUint = 0x0047301E; // Default #1E3047 in terminal uint format (0x00BBGGRR)

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
            TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line; TxtStatusIcon.ToolTip = "Connecting...";
        }

        public void BindSession()
        {
            if (Session == null || !Session.IsConnected)
            { TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CloseCircleLine; TxtStatusIcon.Foreground = Brushes.Red; return; }

            _currentPath = Sftp.WorkingDirectory ?? "/";
            FileGrid.ItemsSource = _remoteFiles;
            LoadPath(_currentPath);
            TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CheckboxCircleLine; TxtStatusIcon.Foreground = Brushes.LimeGreen; TxtStatusIcon.ToolTip = "当前没有传输任务";
            
            Terminal.Connection = Session.TerminalConnection;

            // 监听模式变更以更新 UI
            Session.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SshSessionService.IsAppCursorMode))
                {
                    Dispatcher.Invoke(() => {
                        TxtCursorMode.Text = Session.IsAppCursorMode ? "APP MODE" : "NORMAL MODE";
                    });
                }
            };

            // 强制同步一次 PTY 尺寸，弥补线程池饥饿可能吞掉的首次 WM_SIZE
            Dispatcher.InvokeAsync(() => {
                Session.TerminalConnection?.Resize(
                    (uint)Terminal.Rows, (uint)Terminal.Columns);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            Terminal.Focus();
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

        private void LoadPath(string path, bool isHistory = false)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            try
            {
                var files = Sftp.ListDirectory(path);
                if (!isHistory && _currentPath != path) { _backHistory.Push(_currentPath); _forwardHistory.Clear(); }
                _currentPath = path; TxtCurrentPath.Text = path;
                _remoteFiles.Clear();
                foreach (var f in files.Where(f => f.Name != "." && f.Name != "..").OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name))
                    _remoteFiles.Add(new RemoteFile { Icon = f.IsDirectory ? "FolderFill" : "FileTextLine", Name = f.Name, Size = f.IsDirectory ? "" : FormatSize(f.Length), Type = f.IsDirectory ? "文件夹" : "文件", Date = f.LastWriteTime.ToString("yyyy/MM/dd HH:mm"), Perms = GetPerms(f), Owner = $"{f.UserId}::{f.GroupId}", IsDirectory = f.IsDirectory, FullName = f.FullName });
            }
            catch (Exception ex) { ModernMessageBox.Show("访问失败: " + ex.Message); }
        }

        private static string GetPerms(ISftpFile f) => (f.IsDirectory ? "d" : "-") + (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-") + (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-") + (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-");
        private static string FormatSize(long b) { string[] e = { "B", "KB", "MB", "GB", "TB" }; int i = 0; double d = b; while (d >= 1024 && i < e.Length - 1) { d /= 1024; i++; } return $"{d:0.##} {e[i]}"; }

        private void BtnBack_Click(object sender, RoutedEventArgs e) { if (_backHistory.Count > 0) { _forwardHistory.Push(_currentPath); LoadPath(_backHistory.Pop(), true); } }
        private void BtnForward_Click(object sender, RoutedEventArgs e) { if (_forwardHistory.Count > 0) { _backHistory.Push(_currentPath); LoadPath(_forwardHistory.Pop(), true); } }
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadPath(_currentPath, true);
        private void BtnUp_Click(object sender, RoutedEventArgs e) { if (_currentPath != "/") { int i = _currentPath.TrimEnd('/').LastIndexOf('/'); LoadPath(i > 0 ? _currentPath.Substring(0, i) : "/"); } }
        private void BtnNewFolder_Click(object sender, RoutedEventArgs e) { try { Sftp.CreateDirectory(_currentPath == "/" ? "/NewFolder" : _currentPath.TrimEnd('/') + "/NewFolder"); LoadPath(_currentPath, true); } catch (Exception ex) { ModernMessageBox.Show("新建文件夹失败: " + ex.Message); } }
        private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) 
        { 
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                if (f.IsDirectory) LoadPath(f.FullName);
                else { _ = Session.EditRemoteFileAsync(f.FullName, "code"); }
            }
        }

        private void CtxGridEditVSCode_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f && !f.IsDirectory)
                _ = Session.EditRemoteFileAsync(f.FullName, "code");
        }

        private void CtxGridEditNotepad_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f && !f.IsDirectory)
                _ = Session.EditRemoteFileAsync(f.FullName, "notepad");
        }

        private void UpdateStatus() => Dispatcher.InvokeAsync(() => { if (_activeTransfers > 0) { TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line; TxtStatusIcon.ToolTip = $"传输中... ({_completedFiles}/{_totalFiles}) [{_currentTransferProgress:0}%] - {_currentTransferName}"; } else { TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CheckboxCircleLine; TxtStatusIcon.ToolTip = "当前没有传输任务"; } });

        private void DownloadItemAsync(RemoteFile item, string localDir)
        {
            _activeTransfers++; _totalFiles++;
            Task.Run(() =>
            {
                try
                {
                    string localPath = GetUniqueLocalPath(Path.Combine(localDir, item.Name));
                    if (item.IsDirectory) { Directory.CreateDirectory(localPath); foreach (var c in Sftp.ListDirectory(item.FullName).Where(c => c.Name != "." && c.Name != "..")) DownloadItemSync(c, localPath); }
                    else { using var s = File.Create(localPath); _currentTransferName = item.Name; Sftp.DownloadFile(item.FullName, s); }
                }
                catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"下载失败 {item.Name}: " + ex.Message)); }
                finally { _completedFiles++; _activeTransfers--; UpdateStatus(); }
            });
        }

        private void DownloadItemSync(ISftpFile item, string localDir)
        {
            string lp = Path.Combine(localDir, item.Name);
            if (item.IsDirectory) { Directory.CreateDirectory(lp); foreach (var c in Sftp.ListDirectory(item.FullName).Where(c => c.Name != "." && c.Name != "..")) DownloadItemSync(c, lp); }
            else { using var s = File.Create(lp); Sftp.DownloadFile(item.FullName, s); }
        }

        private void UploadLocalItemAsync(string localPath, string remoteDir)
        {
            _activeTransfers++; _totalFiles++;
            Task.Run(() =>
            {
                try
                {
                    bool isDir = (File.GetAttributes(localPath) & FileAttributes.Directory) == FileAttributes.Directory;
                    string name = Path.GetFileName(localPath.TrimEnd('\\', '/')), rp = remoteDir.TrimEnd('/') + "/" + name;
                    if (isDir) { if (!Sftp.Exists(rp)) Sftp.CreateDirectory(rp); UploadDirSync(localPath, rp); }
                    else { using var s = File.OpenRead(localPath); _currentTransferName = name; Sftp.UploadFile(s, rp); }
                }
                catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"上传失败: " + ex.Message)); }
                finally { _completedFiles++; _activeTransfers--; UpdateStatus(); Dispatcher.InvokeAsync(() => { if (_activeTransfers == 0) LoadPath(_currentPath, true); }); }
            });
        }

        private void UploadDirSync(string localDir, string remoteDir)
        {
            foreach (var f in Directory.GetFiles(localDir)) { using var s = File.OpenRead(f); Sftp.UploadFile(s, remoteDir.TrimEnd('/') + "/" + Path.GetFileName(f)); }
            foreach (var d in Directory.GetDirectories(localDir)) { string rp = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(d); if (!Sftp.Exists(rp)) Sftp.CreateDirectory(rp); UploadDirSync(d, rp); }
        }

        private static string GetUniqueLocalPath(string p) { if (!File.Exists(p) && !Directory.Exists(p)) return p; string dir = Path.GetDirectoryName(p) ?? "", name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p); int c = 1; while (File.Exists(p) || Directory.Exists(p)) { p = Path.Combine(dir, $"{name} ({c}){ext}"); c++; } return p; }

        private void BtnUpload_Click(object sender, RoutedEventArgs e) => BtnUpload.ContextMenu.IsOpen = true;
        private void CtxUploadFile_Click(object sender, RoutedEventArgs e) { var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" }; if (dlg.ShowDialog() == true) foreach (string f in dlg.FileNames) UploadLocalItemAsync(f, _currentPath); }
        private void CtxUploadFolder_Click(object sender, RoutedEventArgs e) { var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要上传的文件夹" }; if (dlg.ShowDialog() == true) UploadLocalItemAsync(dlg.FolderName, _currentPath); }
        private void BtnDownload_Click(object sender, RoutedEventArgs e) => TriggerDownloadSelected();
        private void BtnSshTunnel_Click(object sender, RoutedEventArgs e) { (Application.Current.MainWindow as Views.MainForm)?.OpenSshTunnelManager(); }
        private void BtnTraceroute_Click(object sender, RoutedEventArgs e) { (Application.Current.MainWindow as Views.MainForm)?.OpenTraceroutePage(Session?.HostInfo?.IpAddress); }
        private void BtnProcess_Click(object sender, RoutedEventArgs e) { (Application.Current.MainWindow as Views.MainForm)?.OpenProcessPage(Session); }
        private void BtnSysManagement_Click(object sender, RoutedEventArgs e) { (Application.Current.MainWindow as Views.MainForm)?.OpenSystemManagementPage(Session); }
        private void CtxGridDownload_Click(object sender, RoutedEventArgs e) => TriggerDownloadSelected();

        private void TriggerDownloadSelected()
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList(); if (items.Count == 0) return;
            string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FreeWPFShell"); Directory.CreateDirectory(saveDir);
            foreach (var item in items) DownloadItemAsync(item, saveDir);
        }

        private async void CtxGridDelete_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList(); if (items.Count == 0) return;
            if (ModernMessageBox.Show($"确认删除选中的 {items.Count} 个项吗？\n文件夹将会被强行递归删除！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _activeTransfers++;
                await Task.Run(() =>
                {
                    try { foreach (var item in items) { if (item.IsDirectory) RecursiveDelete(item.FullName); else Sftp.DeleteFile(item.FullName); } }
                    catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"删除失败: " + ex.Message)); }
                    finally { _activeTransfers--; Dispatcher.InvokeAsync(() => LoadPath(_currentPath, true)); }
                });
            }
        }

        private void RecursiveDelete(string dir) { foreach (var f in Sftp.ListDirectory(dir)) { if (f.Name != "." && f.Name != "..") { if (f.IsDirectory) RecursiveDelete(f.FullName); else Sftp.DeleteFile(f.FullName); } } Sftp.DeleteDirectory(dir); }

        private void CtxGridRename_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                var dlg = new UserForm.RenameDialog(f.Name); dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) { try { Sftp.RenameFile(f.FullName, $"{_currentPath.TrimEnd('/')}/{dlg.NewName}"); LoadPath(_currentPath, true); } catch (Exception ex) { ModernMessageBox.Show("重命名失败: " + ex.Message); } }
            }
        }

        private void CtxGridCopy_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList(); if (items.Count == 0) return;
            Clipboard.SetText($"FreeWPFRemoteCopy|{Session?.HostInfo?.Id}|" + string.Join("|", items.Select(x => x.FullName)));
        }

        private async void CtxGridPaste_Click(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsFileDropList()) { foreach (string? f in Clipboard.GetFileDropList()) { if (f != null) UploadLocalItemAsync(f, _currentPath); } }
            else if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText() ?? "";
                if (text.StartsWith($"FreeWPFRemoteCopy|{Session?.HostInfo?.Id}|"))
                {
                    _activeTransfers++; TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line; TxtStatusIcon.ToolTip = "正在服务器端复制...";
                    await Task.Run(() =>
                    {
                        try { foreach (var src in text.Split('|').Skip(2)) Ssh.CreateCommand($"cp -a \"{src}\" \"{_currentPath}/\"").Execute(); }
                        catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"粘贴失败: " + ex.Message)); }
                        finally { _activeTransfers--; UpdateStatus(); Dispatcher.InvokeAsync(() => LoadPath(_currentPath, true)); }
                    });
                }
                else if (text.StartsWith("FreeWPFRemoteCopy|")) ModernMessageBox.Show("抱歉，目前不允许跨服务器进行一键复制粘贴。", "提示");
            }
        }
    }
}
