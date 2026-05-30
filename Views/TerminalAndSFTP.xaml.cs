using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
using SshNetException = Renci.SshNet.Common.SshException;

namespace FreeWPFShell.Views
{
    public partial class TerminalAndSFTP : UserControl
    {
        private readonly ObservableCollection<RemoteFile> _remoteFiles = new();
        private string _currentPath = "/";
        private readonly Stack<string> _backHistory = new(), _forwardHistory = new();

        private int _upActive, _upTotal, _upDone;
        private string _upName = "";
        private double _upProgress;

        private int _downActive, _downTotal, _downDone;
        private string _downName = "";
        private double _downProgress;

        private CancellationTokenSource? _transferCts;

        // UID/GID → 用户名/组名 缓存
        private Dictionary<int, string> _userMap = new();
        private Dictionary<int, string> _groupMap = new();

        // 复用的 StringBuilder（减少 UpdateStatus 分配）
        private readonly StringBuilder _statusBuilder = new(256);

        public SshSessionService? Session { get; private set; }
        private SftpClient? Sftp => Session?.SftpClient;
        private SshClient? Ssh => Session?.MasterClient;

        private PropertyChangedEventHandler? _sessionPropertyChangedHandler;

        public TerminalAndSFTP(SshSessionService session)
        {
            InitializeComponent();
            Session = session;

            _sessionPropertyChangedHandler = (s, e) =>
            {
                var session = Session;
                if (session == null) return;
                Dispatcher.Invoke(() =>
                {
                    if (e.PropertyName == nameof(SshSessionService.ConnectionStatus))
                    {
                        TxtConnStatus.Text = session.ConnectionStatus;
                    }
                    else if (e.PropertyName == nameof(SshSessionService.IsAppCursorMode))
                    {
                        TxtCursorMode.Text = session.IsAppCursorMode ? "APP MODE" : "NORMAL MODE";
                    }
                    else if (e.PropertyName == nameof(SshSessionService.IsSftpConnected))
                    {
                        if (session.IsSftpConnected)
                        {
                            BindSftp();
                        }
                    }
                });
            };
            Session.PropertyChanged += _sessionPropertyChangedHandler;
        }

        /// <summary>关Tab时必须调用，断开所有引用链，释放 Terminal 原生资源</summary>
        public void Cleanup()
        {
            // 1. 退订 Session 事件，断开 Session ↔ this 的循环引用
            if (Session != null && _sessionPropertyChangedHandler != null)
            {
                Session.PropertyChanged -= _sessionPropertyChangedHandler;
                _sessionPropertyChangedHandler = null;
            }

            // 2. 退订 ConnectionLost
            if (Session?.TerminalConnection != null)
            {
                Session.TerminalConnection.ConnectionLost -= TerminalConnection_ConnectionLost;
            }

            // 3. 清空 Terminal.Connection → 触发 Terminal 控件释放原生渲染资源（GPU纹理等）
            Terminal.Connection = null;

            // 4. 取消进行中的传输
            _transferCts?.Cancel();

            // 5. 清空集合，断开 WPF ItemsSource 绑定持有的引用
            _remoteFiles.Clear();
            _userMap.Clear();
            _groupMap.Clear();
            _backHistory.Clear();
            _forwardHistory.Clear();
            FileGrid.ItemsSource = null;

            // 6. 断开 Session 引用，让 GC 能回收整条引用链
            Session = null;
        }

        private void TxtStatusIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && (_upActive > 0 || _downActive > 0))
            {
                if (ModernMessageBox.Show("确定要中断当前所有的传输任务吗？", "中断传输", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _transferCts?.Cancel();
                    Dispatcher.InvokeAsync(() => {
                        StatusIconContainer.ToolTip = "正在取消任务...";
                    });
                }
            }
        }

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
                Session.TerminalConnection?.Resize(
                    (uint)Terminal.Rows, (uint)Terminal.Columns);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            Terminal.Focus();

            if (Session.IsSftpConnected)
            {
                BindSftp();
            }
            else
            {
                TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line;
                TxtStatusIcon.Spin = true;
                TxtStatusIcon.Foreground = Brushes.Orange;
                StatusIconContainer.ToolTip = "SFTP 连接中...";
            }
        }

        private void BindSftp()
        {
            if (Session?.IsSftpConnected != true || Session.SftpClient == null || !Session.SftpClient.IsConnected)
                return;

            FileGrid.ItemsSource = _remoteFiles;

            var sftp = Sftp;
            var ssh = Ssh;
            if (sftp == null || ssh == null) return;

            new Thread(() =>
            {
                try
                {
                    FetchUserGroupMaps(ssh);

                    var workingDir = sftp.WorkingDirectory ?? "/";
                    var files = sftp.ListDirectory(workingDir);
                    var items = BuildFileList(files);

                    Dispatcher.BeginInvoke(() =>
                    {
                        _currentPath = workingDir;
                        TxtCurrentPath.Text = workingDir;
                        _remoteFiles.Clear();
                        foreach (var item in items) _remoteFiles.Add(item);

                        TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CheckboxCircleLine;
                        TxtStatusIcon.Spin = false;
                        TxtStatusIcon.Foreground = Brushes.LimeGreen;
                        StatusIconContainer.ToolTip = "当前没有传输任务";
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CloseCircleLine;
                        TxtStatusIcon.Spin = false;
                        TxtStatusIcon.Foreground = Brushes.Red;
                        StatusIconContainer.ToolTip = $"SFTP 初始化失败: {ex.Message}";
                    });
                }
            })
            { IsBackground = true }.Start();
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

        private void LoadPath(string path, bool isHistory = false)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if (!isHistory && _currentPath != path) { _backHistory.Push(_currentPath); _forwardHistory.Clear(); }
            _currentPath = path;
            TxtCurrentPath.Text = path;

            new Thread(() =>
            {
                try
                {
                    var files = sftp.ListDirectory(path);
                    var items = BuildFileList(files);

                    Dispatcher.BeginInvoke(() =>
                    {
                        _remoteFiles.Clear();
                        foreach (var item in items) _remoteFiles.Add(item);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => ModernMessageBox.Show("访问失败: " + ex.Message));
                }
            })
            { IsBackground = true }.Start();
        }

        private List<RemoteFile> BuildFileList(IEnumerable<Renci.SshNet.Sftp.ISftpFile> files)
        {
            var dirs = new List<RemoteFile>();
            var fileItems = new List<RemoteFile>();
            foreach (var f in files)
            {
                if (f.Name == "." || f.Name == "..") continue;
                string owner = "";
                if (!f.IsDirectory)
                {
                    string user = _userMap.TryGetValue((int)f.UserId, out var u) ? u : f.UserId.ToString();
                    string group = _groupMap.TryGetValue((int)f.GroupId, out var g) ? g : f.GroupId.ToString();
                    owner = $"{user}:{group}";
                }
                var rf = new RemoteFile
                {
                    Name = f.Name,
                    Size = f.IsDirectory ? "" : FormatSize(f.Length),
                    Type = f.IsDirectory ? "文件夹" : "文件",
                    Date = f.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                    Perms = GetPermsFast(f),
                    Owner = owner,
                    IsDirectory = f.IsDirectory,
                    Length = f.Length,
                    FullName = f.FullName,
                    Icon = f.IsDirectory ? "FolderFill" : "FileTextLine"
                };
                if (f.IsDirectory) dirs.Add(rf);
                else fileItems.Add(rf);
            }
            dirs.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            fileItems.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var result = new List<RemoteFile>(dirs.Count + fileItems.Count);
            result.AddRange(dirs);
            result.AddRange(fileItems);
            return result;
        }

        private void FetchUserGroupMaps(SshClient ssh)
        {
            try
            {
                var userMap = new Dictionary<int, string>();
                var groupMap = new Dictionary<int, string>();

                var passwdResult = ssh.CreateCommand("cat /etc/passwd").Execute();
                foreach (var line in passwdResult.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(':');
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int uid))
                        userMap[uid] = parts[0];
                }

                var groupResult = ssh.CreateCommand("cat /etc/group").Execute();
                foreach (var line in groupResult.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(':');
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int gid))
                        groupMap[gid] = parts[0];
                }

                _userMap = userMap;
                _groupMap = groupMap;
            }
            catch { }
        }

        private static string GetPermsFast(ISftpFile f)
        {
            // 栈分配 char 数组避免 String.Concat 多次分配
            Span<char> perms = stackalloc char[10];
            perms[0] = f.IsDirectory ? 'd' : '-';
            perms[1] = f.OwnerCanRead ? 'r' : '-';
            perms[2] = f.OwnerCanWrite ? 'w' : '-';
            perms[3] = f.OwnerCanExecute ? 'x' : '-';
            perms[4] = f.GroupCanRead ? 'r' : '-';
            perms[5] = f.GroupCanWrite ? 'w' : '-';
            perms[6] = f.GroupCanExecute ? 'x' : '-';
            perms[7] = f.OthersCanRead ? 'r' : '-';
            perms[8] = f.OthersCanWrite ? 'w' : '-';
            perms[9] = f.OthersCanExecute ? 'x' : '-';
            return new string(perms);
        }

        private static readonly string[] s_sizeUnits = { "B", "KB", "MB", "GB", "TB" };

        private static string FormatSize(long b)
        {
            int i = 0; double d = b;
            while (d >= 1024 && i < s_sizeUnits.Length - 1) { d /= 1024; i++; }
            return $"{d:0.##} {s_sizeUnits[i]}";
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) { if (_backHistory.Count > 0) { _forwardHistory.Push(_currentPath); LoadPath(_backHistory.Pop(), true); } }
        private void BtnForward_Click(object sender, RoutedEventArgs e) { if (_forwardHistory.Count > 0) { _backHistory.Push(_currentPath); LoadPath(_forwardHistory.Pop(), true); } }
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadPath(_currentPath, true);
        private void BtnUp_Click(object sender, RoutedEventArgs e) { if (_currentPath != "/") { int i = _currentPath.TrimEnd('/').LastIndexOf('/'); LoadPath(i > 0 ? _currentPath.Substring(0, i) : "/"); } }
        private void BtnNewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            try { Sftp.CreateDirectory(_currentPath == "/" ? "/NewFolder" : _currentPath.TrimEnd('/') + "/NewFolder"); LoadPath(_currentPath, true); } catch (Exception ex) { ModernMessageBox.Show("新建文件夹失败: " + ex.Message); }
        }
        private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                if (f.IsDirectory) LoadPath(f.FullName);
                else { _ = Session?.EditRemoteFileAsync(f.FullName, "code"); }
            }
        }

        private void FileGrid_DragOver(object sender, DragEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void FileGrid_Drop(object sender, DragEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files == null || files.Length == 0) return;

            foreach (var file in files)
                UploadLocalItemAsync(file, _currentPath);
        }

        private void CtxGridEditVSCode_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f && !f.IsDirectory)
                _ = Session?.EditRemoteFileAsync(f.FullName, "code");
        }

        private void CtxGridEditNotepad_Click(object sender, RoutedEventArgs e)
        {
            if (FileGrid.SelectedItem is RemoteFile f && !f.IsDirectory)
                _ = Session?.EditRemoteFileAsync(f.FullName, "notepad");
        }

        private void UpdateStatus()
        {
            Dispatcher.InvokeAsync(() =>
            {
                bool anyUp = _upActive > 0 || (_upTotal > 0 && _upDone == _upTotal && _upActive == 0 && _downActive > 0);
                bool anyDown = _downActive > 0 || (_downTotal > 0 && _downDone == _downTotal && _downActive == 0 && _upActive > 0);

                if (_upActive > 0 || _downActive > 0)
                {
                    TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line;
                    TxtStatusIcon.Spin = true;
                    TxtStatusIcon.Foreground = Brushes.Gold;

                    _statusBuilder.Clear();
                    if (_upActive > 0 || (_upTotal > 0 && _upDone == _upTotal))
                        _statusBuilder.Append("上传: (").Append(_upDone).Append('/').Append(_upTotal).Append(") [")
                                      .Append((_upActive > 0 ? _upProgress : 100).ToString("F1")).Append("%] - ")
                                      .AppendLine(_upActive > 0 ? _upName : "已完成");
                    if (_downActive > 0 || (_downTotal > 0 && _downDone == _downTotal))
                        _statusBuilder.Append("下载: (").Append(_downDone).Append('/').Append(_downTotal).Append(") [")
                                      .Append((_downActive > 0 ? _downProgress : 100).ToString("F1")).Append("%] - ")
                                      .AppendLine(_downActive > 0 ? _downName : "已完成");

                    _statusBuilder.Append("\n双击可取消所有任务");
                    StatusIconContainer.ToolTip = _statusBuilder.ToString();
                }
                else
                {
                    TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.CheckboxCircleLine;
                    TxtStatusIcon.Spin = false;
                    TxtStatusIcon.Foreground = Brushes.LimeGreen;
                    StatusIconContainer.ToolTip = "当前没有传输任务";
                    _upTotal = _upDone = 0;
                    _downTotal = _downDone = 0;
                }
            });
        }

        private static int CountLocalFiles(string path)
        {
            try
            {
                if (File.Exists(path)) return 1;
                if (Directory.Exists(path))
                    return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
            }
            catch { }
            return 0;
        }

        private async Task<int> CountRemoteFilesAsync(string path)
        {
            var sftp = Sftp;
            if (sftp == null) return 0;
            int count = 0;
            try
            {
                var files = await Task.Run(() => sftp.ListDirectory(path));
                foreach (var f in files)
                {
                    if (f.Name == "." || f.Name == "..") continue;
                    if (f.IsDirectory) count += await CountRemoteFilesAsync(f.FullName);
                    else count++;
                }
            }
            catch { }
            return count;
        }

        private async void DownloadItemAsync(RemoteFile item, string localDir)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if (_upActive == 0 && _downActive == 0)
            {
                _transferCts = new CancellationTokenSource();
                _downTotal = 0; _downDone = 0;
                _upTotal = 0; _upDone = 0;
            }

            int count = item.IsDirectory ? await CountRemoteFilesAsync(item.FullName) : 1;
            Interlocked.Add(ref _downTotal, count);
            Interlocked.Increment(ref _downActive);
            UpdateStatus();

            Task.Run(() =>
            {
                try
                {
                    string localPath = GetUniqueLocalPath(Path.Combine(localDir, item.Name));
                    _downName = item.Name;
                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(localPath);
                        foreach (var c in sftp.ListDirectory(item.FullName))
                        {
                            if (c.Name == "." || c.Name == "..") continue;
                            if (_transferCts?.IsCancellationRequested == true) break;
                            DownloadItemSync(c, localPath, sftp);
                        }
                    }
                    else
                    {
                        using var s = File.Create(localPath);
                        using (var reg = _transferCts?.Token.Register(() => { try { s.Close(); } catch { } }))
                        {
                            sftp.DownloadFile(item.FullName, s, uploaded => {
                                _downProgress = item.Length > 0 ? (double)uploaded / item.Length * 100 : 0;
                                Dispatcher.InvokeAsync(UpdateStatus);
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException || ex is SshNetException)
                { }
                catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"下载失败 {item.Name}: " + ex.Message)); }
                finally { _downDone++; Interlocked.Decrement(ref _downActive); UpdateStatus(); }
            });
        }

        private void DownloadItemSync(ISftpFile item, string localDir, SftpClient sftp)
        {
            if (_transferCts?.IsCancellationRequested == true) return;
            string lp = Path.Combine(localDir, item.Name);
            _downName = item.Name;
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(lp);
                foreach (var c in sftp.ListDirectory(item.FullName))
                {
                    if (c.Name == "." || c.Name == "..") continue;
                    if (_transferCts?.IsCancellationRequested == true) break;
                    DownloadItemSync(c, lp, sftp);
                }
            }
            else
            {
                try {
                    using var s = File.Create(lp);
                    using (var reg = _transferCts?.Token.Register(() => { try { s.Close(); } catch { } }))
                    {
                        sftp.DownloadFile(item.FullName, s, uploaded => {
                            _downProgress = item.Length > 0 ? (double)uploaded / item.Length * 100 : 0;
                            Dispatcher.InvokeAsync(UpdateStatus);
                        });
                    }
                    _downDone++;
                } catch { }
            }
        }

        private async void UploadLocalItemAsync(string localPath, string remoteDir)
        {
            var session = Session;
            var sftp = Sftp;
            if (session == null || sftp == null || !sftp.IsConnected) return;

            if (_upActive == 0 && _downActive == 0)
            {
                _transferCts = new CancellationTokenSource();
                _upTotal = 0; _upDone = 0;
                _downTotal = 0; _downDone = 0;
            }

            int count = await Task.Run(() => CountLocalFiles(localPath));
            Interlocked.Add(ref _upTotal, count);
            Interlocked.Increment(ref _upActive);
            UpdateStatus();

            try
            {
                await Task.Run(() =>
                {
                    bool isDir = (File.GetAttributes(localPath) & FileAttributes.Directory) == FileAttributes.Directory;
                    string name = Path.GetFileName(localPath.TrimEnd('\\', '/')), rp = remoteDir.TrimEnd('/') + "/" + name;
                    _upName = name;
                    if (isDir)
                    {
                        lock (session.SftpLock) { if (!sftp.Exists(rp)) sftp.CreateDirectory(rp); }
                        UploadDirSync(localPath, rp, session, sftp);
                    }
                    else
                    {
                        long fileSize = new FileInfo(localPath).Length;
                        using var s = File.OpenRead(localPath);
                        using (var reg = _transferCts?.Token.Register(() => { try { s.Close(); } catch { } }))
                        {
                            lock (session.SftpLock)
                            {
                                sftp.UploadFile(s, rp, uploaded => {
                                    _upProgress = fileSize > 0 ? (double)uploaded / fileSize * 100 : 0;
                                    Dispatcher.InvokeAsync(UpdateStatus);
                                });
                            }
                        }
                    }
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException || ex is SshNetException)
            { }
            catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"上传失败: " + ex.Message)); }
            finally
            {
                _upDone++; Interlocked.Decrement(ref _upActive);
                UpdateStatus();
                Dispatcher.InvokeAsync(() => { if (_upActive == 0 && _downActive == 0) LoadPath(_currentPath, true); });
            }
        }

        private void UploadDirSync(string localDir, string remoteDir, SshSessionService session, SftpClient sftp)
        {
            if (_transferCts?.IsCancellationRequested == true) return;
            var files = Directory.GetFiles(localDir);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _transferCts?.Token ?? default }, f =>
            {
                try
                {
                    string fileName = Path.GetFileName(f);
                    long fileSize = new FileInfo(f).Length;

                    _upName = fileName;
                    Dispatcher.InvokeAsync(UpdateStatus);

                    using var s = File.OpenRead(f);
                    using (var reg = _transferCts?.Token.Register(() => { try { s.Close(); } catch { } }))
                    {
                        lock (session.SftpLock)
                        {
                            sftp.UploadFile(s, remoteDir.TrimEnd('/') + "/" + fileName,
                                uploaded => {
                                    _upProgress = fileSize > 0 ? (double)uploaded / fileSize * 100 : 0;
                                    Dispatcher.InvokeAsync(UpdateStatus);
                                });
                        }
                    }
                    Interlocked.Increment(ref _upDone);
                }
                catch { }
            });

            if (_transferCts?.IsCancellationRequested == true) return;
            foreach (var d in Directory.GetDirectories(localDir))
            {
                if (_transferCts?.IsCancellationRequested == true) break;
                string rp = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(d);
                lock (session.SftpLock) { if (!sftp.Exists(rp)) sftp.CreateDirectory(rp); }
                UploadDirSync(d, rp, session, sftp);
            }
        }

        private static string GetUniqueLocalPath(string p) { if (!File.Exists(p) && !Directory.Exists(p)) return p; string dir = Path.GetDirectoryName(p) ?? "", name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p); int c = 1; while (File.Exists(p) || Directory.Exists(p)) { p = Path.Combine(dir, $"{name} ({c}){ext}"); c++; } return p; }

        private void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            BtnUpload.ContextMenu.IsOpen = true;
        }
        private void CtxUploadFile_Click(object sender, RoutedEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
            if (dlg.ShowDialog() == true) foreach (string f in dlg.FileNames) UploadLocalItemAsync(f, _currentPath);
        }
        private void CtxUploadFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要上传的文件夹" };
            if (dlg.ShowDialog() == true) UploadLocalItemAsync(dlg.FolderName, _currentPath);
        }
        private void BtnDownload_Click(object sender, RoutedEventArgs e) => TriggerDownloadSelected();
        private void BtnSshTunnel_Click(object sender, RoutedEventArgs e) { (Application.Current.MainWindow as Views.MainForm)?.OpenSshTunnelManager(); }
        private void BtnTraceroute_Click(object sender, RoutedEventArgs e) { (Application.Current.MainWindow as Views.MainForm)?.OpenTraceroutePage(Session?.HostInfo?.IpAddress); }
        private void BtnSysManagement_Click(object sender, RoutedEventArgs e) { if (Session != null) (Application.Current.MainWindow as Views.MainForm)?.OpenSystemManagementPage(Session); }
        private void CtxGridDownload_Click(object sender, RoutedEventArgs e) => TriggerDownloadSelected();

        private void TriggerDownloadSelected()
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList(); if (items.Count == 0) return;
            string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FreeWPFShell"); Directory.CreateDirectory(saveDir);
            foreach (var item in items) DownloadItemAsync(item, saveDir);
        }

        private async void CtxGridDelete_Click(object sender, RoutedEventArgs e)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList(); if (items.Count == 0) return;
            if (ModernMessageBox.Show($"确认删除选中的 {items.Count} 个项吗？\n文件夹将会被强行递归删除！", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Interlocked.Increment(ref _upActive);
                await Task.Run(() =>
                {
                    try { foreach (var item in items) { if (item.IsDirectory) RecursiveDelete(item.FullName, sftp); else sftp.DeleteFile(item.FullName); } }
                    catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"删除失败: " + ex.Message)); }
                    finally { Interlocked.Decrement(ref _upActive); Dispatcher.InvokeAsync(() => LoadPath(_currentPath, true)); }
                });
            }
        }

        private void RecursiveDelete(string dir, SftpClient sftp) { foreach (var f in sftp.ListDirectory(dir)) { if (f.Name != "." && f.Name != "..") { if (f.IsDirectory) RecursiveDelete(f.FullName, sftp); else sftp.DeleteFile(f.FullName); } } sftp.DeleteDirectory(dir); }

        private void CtxGridRename_Click(object sender, RoutedEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            if (FileGrid.SelectedItem is RemoteFile f)
            {
                var dlg = new UserForm.RenameDialog(f.Name); dlg.Owner = Window.GetWindow(this);
                if (dlg.ShowDialog() == true) { try { Sftp.RenameFile(f.FullName, $"{_currentPath.TrimEnd('/')}/{dlg.NewName}"); LoadPath(_currentPath, true); } catch (Exception ex) { ModernMessageBox.Show("重命名失败: " + ex.Message); } }
            }
        }

        private void CtxGridCopy_Click(object sender, RoutedEventArgs e)
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            var items = FileGrid.SelectedItems.Cast<RemoteFile>().ToList(); if (items.Count == 0) return;
            try
            {
                Clipboard.SetText($"FreeWPFRemoteCopy|{Session?.HostInfo?.Id}|" + string.Join("|", items.Select(x => x.FullName)));
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("复制到剪切板失败: " + ex.Message);
            }
        }

        private async void CtxGridPaste_Click(object sender, RoutedEventArgs e)
        {
            var sftp = Sftp;
            var ssh = Ssh;
            if (sftp == null || ssh == null || !sftp.IsConnected) return;
            try
            {
                if (Clipboard.ContainsFileDropList()) { foreach (string? f in Clipboard.GetFileDropList()) { if (f != null) UploadLocalItemAsync(f, _currentPath); } }
                else if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText() ?? "";
                    if (text.StartsWith($"FreeWPFRemoteCopy|{Session?.HostInfo?.Id}|"))
                    {
                        Interlocked.Increment(ref _upActive); TxtStatusIcon.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line; TxtStatusIcon.ToolTip = "正在服务器端复制...";
                        await Task.Run(() =>
                        {
                            try { foreach (var src in text.Split('|').Skip(2)) ssh.CreateCommand($"cp -a \"{src}\" \"{_currentPath}/\"").Execute(); }
                            catch (Exception ex) { Dispatcher.InvokeAsync(() => ModernMessageBox.Show($"粘贴失败: " + ex.Message)); }
                            finally { Interlocked.Decrement(ref _upActive); UpdateStatus(); Dispatcher.InvokeAsync(() => LoadPath(_currentPath, true)); }
                        });
                    }
                    else if (text.StartsWith("FreeWPFRemoteCopy|")) ModernMessageBox.Show("抱歉，目前不允许跨服务器进行一键复制粘贴。", "提示");
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("访问剪切板失败: " + ex.Message);
            }
        }

        private void TerminalConnection_ConnectionLost(object? sender, EventArgs e)
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
                if (mainWindow != null)
                {
                    mainWindow.CloseTab(this);
                }
            });
        }
    }
}
