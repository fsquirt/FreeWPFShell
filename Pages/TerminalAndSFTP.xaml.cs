using Microsoft.Terminal.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using FreeWPFShell.Share;
using System.IO;
using System.Threading.Tasks;
using FreeWPFShell.UserForm;

namespace FreeWPFShell.Pages
{
    public class RemoteFile
    {
        public string Icon { get; set; }
        public string Name { get; set; }
        public string Size { get; set; }
        public string Type { get; set; }
        public string Date { get; set; }
        public string Perms { get; set; }
        public string Owner { get; set; }
        public bool IsDirectory { get; set; }
        public string FullName { get; set; }
    }

    public partial class TerminalAndSFTP : UserControl
    {
        private ObservableCollection<RemoteFile> _remoteFiles = new ObservableCollection<RemoteFile>();
        private string _currentPath = "/";
        private Stack<string> _backHistory = new Stack<string>();
        private Stack<string> _forwardHistory = new Stack<string>();

        // Transfer state
        private int _activeTransfers = 0;
        private int _totalFiles = 0;
        private int _completedFiles = 0;
        private string _currentTransferName = "";
        private double _currentTransferProgress = 0;
        
        // Connection Cancellation
        private System.Threading.CancellationTokenSource _connectCts = new System.Threading.CancellationTokenSource();

        public Share.SshSessionInstance Session { get; }
        public SshManager.SshConnectionInfo HostInfo => Session?.HostInfo;
        
        private SftpClient _sftpClient => Session?.SftpClient;
        private SshClient _sshCmdClient => Session?.MasterClient;

        public TerminalAndSFTP(Share.SshSessionInstance session)
        {
            InitializeComponent();
            Session = session;
        }

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {

            uint[] colorTable = new uint[16]
            {
                0x000c0c0c, 0x001f0fc5, 0x000ea113, 0x00009cc1,
                0x00da3700, 0x00981788, 0x00dd963a, 0x00cccccc,
                0x00767676, 0x005648e7, 0x000cc616, 0x00a5f1f9,
                0x00ff783b, 0x009e00b4, 0x00d6d661, 0x00f2f2f2
            };

            var theme = new TerminalTheme
            {
                DefaultBackground = 0x0047301E,
                DefaultForeground = 0x00ffffff,
                DefaultSelectionBackground = 0x00ffffff,
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = colorTable
            };

            Terminal.SetTheme(theme, "Cascadia Code", 10);
            TxtStatusIcon.Text = "⏳";
            TxtStatusIcon.ToolTip = "Connecting...";
        }

        public void BindSession()
        {
            if (Session == null || !Session.IsConnected)
            {
                TxtLoadingStatus.Text = "连接失败";
                BtnCancelConnect.Visibility = Visibility.Collapsed;
                TxtStatusIcon.Text = "❌";
                return;
            }

            PnlLoading.Visibility = Visibility.Collapsed;
            
            _currentPath = _sftpClient.WorkingDirectory ?? "/";
            FileGrid.ItemsSource = _remoteFiles;
            LoadPath(_currentPath);

            TxtStatusIcon.Text = "✅";
            TxtStatusIcon.ToolTip = "当前没有传输任务";

            Terminal.Connection = Session.TerminalConnection;
            Terminal.Focus();
            
            // Auto resize hack to fix TUI rendering glitches
            Dispatcher.InvokeAsync(async () => 
            {
                await Task.Delay(60);
                Terminal.Margin = new Thickness(-1, 0, 0, 0);
                await Task.Delay(10);
                Terminal.Margin = new Thickness(1, 0, 0, 0);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnCancelConnect_Click(object sender, RoutedEventArgs e)
        {
            TxtLoadingStatus.Text = "正在取消...";
            BtnCancelConnect.IsEnabled = false;
            
            // Programmatically request the MainForm to close this tab to dispose safely
            var mainForm = Window.GetWindow(this) as MainForm;
            if (mainForm != null && mainForm.SessionTabs.SelectedItem is TabItem currentTab)
            {
                // We use reflection or just search for the close button, or we can just call MainForm to close it
                // Finding the close button inside the tab header:
                if (currentTab.Header is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is Button closeBtn)
                {
                    closeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
            }
        }

        private void Terminal_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                Session?.TerminalConnection?.WriteInput("\t");
            }
            else if (e.Key == Key.Up)
            {
                e.Handled = true;
                Session?.TerminalConnection?.WriteInput("\x1b[A");
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                Session?.TerminalConnection?.WriteInput("\x1b[B");
            }
            else if (e.Key == Key.Right)
            {
                e.Handled = true;
                Session?.TerminalConnection?.WriteInput("\x1b[C");
            }
            else if (e.Key == Key.Left)
            {
                e.Handled = true;
                Session?.TerminalConnection?.WriteInput("\x1b[D");
            }
        }

        private void LoadPath(string path, bool isHistory = false)
        {
            if (_sftpClient == null || !_sftpClient.IsConnected) return;
            try
            {
                var files = _sftpClient.ListDirectory(path);
                
                if (!isHistory && _currentPath != path)
                {
                    _backHistory.Push(_currentPath);
                    _forwardHistory.Clear();
                }
                
                _currentPath = path;
                TxtCurrentPath.Text = path;
                
                var list = new List<RemoteFile>();
                foreach (var f in files)
                {
                    if (f.Name == "." || f.Name == "..") continue;
                    
                    list.Add(new RemoteFile
                    {
                        Icon = f.IsDirectory ? "📁" : "📄",
                        Name = f.Name,
                        Size = f.IsDirectory ? "" : FormatSize(f.Length),
                        Type = f.IsDirectory ? "文件夹" : "文件",
                        Date = f.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                        Perms = GetPerms(f),
                        Owner = $"{f.UserId}::{f.GroupId}",
                        IsDirectory = f.IsDirectory,
                        FullName = f.FullName
                    });
                }
                
                _remoteFiles.Clear();
                foreach (var f in list.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name))
                {
                    _remoteFiles.Add(f);
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("访问失败: " + ex.Message);
            }
        }

        private string GetPerms(ISftpFile f)
        {
            string s = f.IsDirectory ? "d" : "-";
            s += f.OwnerCanRead ? "r" : "-";
            s += f.OwnerCanWrite ? "w" : "-";
            s += f.OwnerCanExecute ? "x" : "-";
            s += f.GroupCanRead ? "r" : "-";
            s += f.GroupCanWrite ? "w" : "-";
            s += f.GroupCanExecute ? "x" : "-";
            s += f.OthersCanRead ? "r" : "-";
            s += f.OthersCanWrite ? "w" : "-";
            s += f.OthersCanExecute ? "x" : "-";
            return s;
        }

        private string FormatSize(long bytes)
        {
            string[] exts = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double d = bytes;
            while (d >= 1024 && i < exts.Length - 1)
            {
                d /= 1024;
                i++;
            }
            return $"{d:0.##} {exts[i]}";
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_backHistory.Count > 0)
            {
                _forwardHistory.Push(_currentPath);
                LoadPath(_backHistory.Pop(), true);
            }
        }

        private void BtnForward_Click(object sender, RoutedEventArgs e)
        {
            if (_forwardHistory.Count > 0)
            {
                _backHistory.Push(_currentPath);
                LoadPath(_forwardHistory.Pop(), true);
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadPath(_currentPath, true);

        private void BtnUp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPath != "/")
            {
                int lastSlash = _currentPath.TrimEnd('/').LastIndexOf('/');
                string parent = lastSlash > 0 ? _currentPath.Substring(0, lastSlash) : "/";
                LoadPath(parent);
            }
        }

        private void BtnNewFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string newDir = _currentPath == "/" ? "/NewFolder" : _currentPath.TrimEnd('/') + "/NewFolder";
                _sftpClient.CreateDirectory(newDir);
                LoadPath(_currentPath, true);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("新建文件夹失败: " + ex.Message);
            }
        }

        private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile selectedFile && selectedFile.IsDirectory)
            {
                LoadPath(selectedFile.FullName);
            }
        }

        // ==========================================
        // SFTP Transfer Engine
        // ==========================================
        
        private void UpdateStatus()
        {
            Dispatcher.InvokeAsync(() => {
                if (_activeTransfers > 0)
                {
                    TxtStatusIcon.Text = "⏳";
                    TxtStatusIcon.ToolTip = $"传输中... ({_completedFiles}/{_totalFiles}) [{_currentTransferProgress:0}%] - {_currentTransferName}";
                }
                else
                {
                    TxtStatusIcon.Text = "✅";
                    TxtStatusIcon.ToolTip = "当前没有传输任务";
                    _totalFiles = 0;
                    _completedFiles = 0;
                    _currentTransferName = "";
                    _currentTransferProgress = 0;
                }
            });
        }

        private string GetUniqueLocalPath(string targetPath)
        {
            if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) return targetPath;
            string dir = Path.GetDirectoryName(targetPath);
            string name = Path.GetFileNameWithoutExtension(targetPath);
            string ext = Path.GetExtension(targetPath);
            int count = 1;
            while (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(dir, $"{name} ({count}){ext}");
                count++;
            }
            return targetPath;
        }

        private async void DownloadItemAsync(RemoteFile item, string localDir)
        {
            _activeTransfers++;
            _totalFiles++;
            
            await Task.Run(() => {
                try {
                    string localPath = GetUniqueLocalPath(Path.Combine(localDir, item.Name));
                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(localPath);
                        var children = _sftpClient.ListDirectory(item.FullName);
                        foreach(var child in children)
                        {
                            if (child.Name == "." || child.Name == "..") continue;
                            DownloadItemSync(child, localPath);
                        }
                    }
                    else
                    {
                        using (var stream = File.Create(localPath)) 
                        {
                            _currentTransferName = item.Name;
                            _sftpClient.DownloadFile(item.FullName, stream, (downloaded) => {
                                // approximation since item.Size is a string, we might not have exact byte length here without casting.
                                _currentTransferProgress = 50; // simple mock for now
                                UpdateStatus();
                            });
                        }
                    }
                } catch(Exception ex) {
                    Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"下载失败 {item.Name}: " + ex.Message));
                } finally {
                    _completedFiles++;
                    _activeTransfers--;
                    UpdateStatus();
                }
            });
        }
        
        // Recursive sync helper
        private void DownloadItemSync(ISftpFile item, string localDir)
        {
            string localPath = Path.Combine(localDir, item.Name);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(localPath);
                var children = _sftpClient.ListDirectory(item.FullName);
                foreach(var child in children)
                {
                    if (child.Name == "." || child.Name == "..") continue;
                    DownloadItemSync(child, localPath);
                }
            }
            else
            {
                using (var stream = File.Create(localPath)) 
                {
                    _currentTransferName = item.Name;
                    _sftpClient.DownloadFile(item.FullName, stream);
                }
            }
        }

        private async void UploadLocalItemAsync(string localPath, string remoteDir)
        {
            _activeTransfers++;
            _totalFiles++;
            
            await Task.Run(() => {
                try {
                    var isDir = (File.GetAttributes(localPath) & FileAttributes.Directory) == FileAttributes.Directory;
                    string name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
                    string remotePath = remoteDir.TrimEnd('/') + "/" + name;
                    
                    if (isDir)
                    {
                        if (!_sftpClient.Exists(remotePath)) _sftpClient.CreateDirectory(remotePath);
                        UploadDirectorySync(localPath, remotePath);
                    }
                    else
                    {
                        using (var stream = File.OpenRead(localPath)) 
                        {
                            _currentTransferName = name;
                            _sftpClient.UploadFile(stream, remotePath, (uploaded) => {
                                _currentTransferProgress = (double)uploaded / stream.Length * 100.0;
                                UpdateStatus();
                            });
                        }
                    }
                } catch(Exception ex) {
                    Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"上传失败: " + ex.Message));
                } finally {
                    _completedFiles++;
                    _activeTransfers--;
                    UpdateStatus();
                    Dispatcher.InvokeAsync(() => { if(_activeTransfers == 0) LoadPath(_currentPath, true); });
                }
            });
        }
        
        private void UploadDirectorySync(string localDir, string remoteDir)
        {
            foreach (var file in Directory.GetFiles(localDir))
            {
                string remotePath = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(file);
                using (var stream = File.OpenRead(file)) {
                     _currentTransferName = Path.GetFileName(file);
                    _sftpClient.UploadFile(stream, remotePath);
                }
            }
            foreach (var dir in Directory.GetDirectories(localDir))
            {
                string remotePath = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(dir);
                if (!_sftpClient.Exists(remotePath)) _sftpClient.CreateDirectory(remotePath);
                UploadDirectorySync(dir, remotePath);
            }
        }
        
        // ==========================================
        // Toolbar & Context Menu Event Handlers
        // ==========================================

        private void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            BtnUpload.ContextMenu.IsOpen = true;
        }

        private void CtxUploadFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
            if (dlg.ShowDialog() == true)
            {
                foreach(string file in dlg.FileNames)
                    UploadLocalItemAsync(file, _currentPath);
            }
        }

        private void CtxUploadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要上传的文件夹" };
            if (dlg.ShowDialog() == true)
            {
                 UploadLocalItemAsync(dlg.FolderName, _currentPath);
            }
        }

        private void BtnDownload_Click(object sender, RoutedEventArgs e) => TriggerDownloadSelected();

        private void BtnSshTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainForm mainForm)
            {
                mainForm.OpenSshTunnelManager();
            }
        }

        private void CtxGridDownload_Click(object sender, RoutedEventArgs e) => TriggerDownloadSelected();

        private void TriggerDownloadSelected()
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList();
            if (items.Count == 0) return;
            
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var saveDir = Path.Combine(desktop, "FreeWPFShell");
            Directory.CreateDirectory(saveDir);
            
            foreach(var item in items)
                DownloadItemAsync(item, saveDir);
        }

        private async void CtxGridDelete_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList();
            if (items.Count == 0) return;

            var res = ModernMessageBox.Show($"确认删除选中的 {items.Count} 个项吗？\n文件夹将会被强行递归删除！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                _activeTransfers++;
                await Task.Run(() => {
                    try {
                        foreach(var item in items) {
                            if (item.IsDirectory)
                                RecursiveDelete(item.FullName);
                            else
                                _sftpClient.DeleteFile(item.FullName);
                        }
                    } catch (Exception ex) {
                         Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"删除失败: " + ex.Message));
                    } finally {
                        _activeTransfers--;
                        Dispatcher.InvokeAsync(() => LoadPath(_currentPath, true));
                    }
                });
            }
        }

        private void CtxGridRename_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile selectedFile)
            {
                var dlg = new RenameDialog(selectedFile.Name);
                dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        string newPath = $"{_currentPath.TrimEnd('/')}/{dlg.NewName}";
                        _sftpClient.RenameFile(selectedFile.FullName, newPath);
                        LoadPath(_currentPath, true);
                    }
                    catch (Exception ex)
                    {
                        ModernMessageBox.Show("重命名失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }                 
        private void RecursiveDelete(string remoteDir)
        {
            var files = _sftpClient.ListDirectory(remoteDir);
            foreach (var f in files)
            {
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory) RecursiveDelete(f.FullName);
                else _sftpClient.DeleteFile(f.FullName);
            }
            _sftpClient.DeleteDirectory(remoteDir);
        }

        private void CtxGridCopy_Click(object sender, RoutedEventArgs e)
        {
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList();
            if (items.Count == 0) return;
            string payload = $"FreeWPFRemoteCopy|{HostInfo.Id}|" + string.Join("|", items.Select(x => x.FullName));
            Clipboard.SetText(payload);
        }

        private async void CtxGridPaste_Click(object sender, RoutedEventArgs e)
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                foreach (string localItem in files)
                    UploadLocalItemAsync(localItem, _currentPath);
            }
            else if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (text.StartsWith($"FreeWPFRemoteCopy|{HostInfo.Id}|"))
                {
                    string[] parts = text.Split('|');
                    var srcFiles = parts.Skip(2); // Skip prefix and hostId
                    
                    _activeTransfers++;
                    TxtStatusIcon.Text = "⏳";
                    TxtStatusIcon.ToolTip = "正在服务器端复制...";
                    
                    await Task.Run(() => {
                        try {
                            foreach(var src in srcFiles)
                            {
                                // Remote to remote copy over SSH
                                var cmd = _sshCmdClient.CreateCommand($"cp -a \"{src}\" \"{_currentPath}/\"");
                                cmd.Execute();
                            }
                        } catch (Exception ex) {
                             Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"粘贴失败: " + ex.Message));
                        } finally {
                            _activeTransfers--;
                            UpdateStatus();
                            Dispatcher.InvokeAsync(() => LoadPath(_currentPath, true));
                        }
                    });
                }
                else if (text.StartsWith("FreeWPFRemoteCopy|"))
                {
                     ModernMessageBox.Show("抱歉，目前不允许跨服务器进行一键复制粘贴，您可以先下载再上传。", "提示");
                }
            }
        }

        public void Disconnect()
        {
            _connectCts.Cancel();
        }
    }
}
