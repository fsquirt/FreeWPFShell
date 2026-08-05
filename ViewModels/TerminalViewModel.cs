using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FreeWPFShell.ViewModels
{
    /// <summary>
    /// 终端 + SFTP 页 ViewModel。承载 SFTP 文件列表、导航、传输状态与操作逻辑。
    /// 终端原生控件（Microsoft.Terminal.Wpf）交互保留在 Code-behind。
    /// </summary>
    public partial class TerminalViewModel : ObservableObject
    {
        private readonly SshSessionService _session;

        public ObservableCollection<RemoteFile> Files { get; } = new();
        public ObservableCollection<RemoteFile> SelectedFiles { get; } = new();

        private readonly Stack<string> _backHistory = new();
        private readonly Stack<string> _forwardHistory = new();

        [ObservableProperty]
        private string _currentPath = "/";

        // 传输状态
        [ObservableProperty]
        private int _upActive;
        [ObservableProperty]
        private int _upTotal;
        [ObservableProperty]
        private int _upDone;
        [ObservableProperty]
        private string _upName = "";
        [ObservableProperty]
        private double _upProgress;

        [ObservableProperty]
        private int _downActive;
        [ObservableProperty]
        private int _downTotal;
        [ObservableProperty]
        private int _downDone;
        [ObservableProperty]
        private string _downName = "";
        [ObservableProperty]
        private double _downProgress;

        // 状态图标/提示（由 Code-behind 或 UI 读取）
        [ObservableProperty]
        private string _statusText = "当前没有传输任务";
        [ObservableProperty]
        private bool _isTransferring;

        private CancellationTokenSource? _transferCts;

        // UID/GID → 用户名/组名 缓存
        private Dictionary<int, string> _userMap = new();
        private Dictionary<int, string> _groupMap = new();

        private SftpClient? Sftp => _session.SftpClient;
        private Renci.SshNet.SshClient? Ssh => _session.MasterClient;

        // UI 交互回调
        public Action<string, string>? ShowMessage { get; set; }
        public Func<string, string, bool>? Confirm { get; set; }
        public Action? TransferStateChanged { get; set; }

        public TerminalViewModel(SshSessionService session)
        {
            _session = session;
        }

        public SshSessionService Session => _session;

        partial void OnIsTransferringChanged(bool value)
        {
            TransferStateChanged?.Invoke();
        }

        // ── 导航 ─────────────────────────────────────────────────

        [RelayCommand]
        private void GoBack()
        {
            if (_backHistory.Count > 0)
            {
                _forwardHistory.Push(CurrentPath);
                LoadPath(_backHistory.Pop(), isHistory: true);
            }
        }

        [RelayCommand]
        private void GoForward()
        {
            if (_forwardHistory.Count > 0)
            {
                _backHistory.Push(CurrentPath);
                LoadPath(_forwardHistory.Pop(), isHistory: true);
            }
        }

        [RelayCommand]
        private void Refresh() => LoadPath(CurrentPath, isHistory: true);

        [RelayCommand]
        private void GoUp()
        {
            if (CurrentPath != "/")
            {
                int i = CurrentPath.TrimEnd('/').LastIndexOf('/');
                LoadPath(i > 0 ? CurrentPath.Substring(0, i) : "/");
            }
        }

        [RelayCommand]
        private void NewFolder()
        {
            if (Sftp == null || !Sftp.IsConnected) return;
            try
            {
                Sftp.CreateDirectory(CurrentPath == "/" ? "/NewFolder" : CurrentPath.TrimEnd('/') + "/NewFolder");
                LoadPath(CurrentPath, isHistory: true);
            }
            catch (Exception ex) { ShowMessage?.Invoke("新建文件夹失败", ex.Message); }
        }

        public void LoadPath(string path, bool isHistory = false)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if (!isHistory && CurrentPath != path) { _backHistory.Push(CurrentPath); _forwardHistory.Clear(); }
            CurrentPath = path;

            new Thread(() =>
            {
                try
                {
                    var files = sftp.ListDirectory(path);
                    var items = BuildFileList(files);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        Files.Clear();
                        foreach (var item in items) Files.Add(item);
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        ShowMessage?.Invoke("访问失败", ex.Message));
                }
            }) { IsBackground = true }.Start();
        }

        public void BindSftp()
        {
            var sftp = Sftp;
            var ssh = Ssh;
            if (sftp == null || ssh == null || !sftp.IsConnected) return;

            new Thread(() =>
            {
                try
                {
                    FetchUserGroupMaps(ssh);
                    var workingDir = sftp.WorkingDirectory ?? "/";
                    var files = sftp.ListDirectory(workingDir);
                    var items = BuildFileList(files);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        CurrentPath = workingDir;
                        Files.Clear();
                        foreach (var item in items) Files.Add(item);
                    });
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        private List<RemoteFile> BuildFileList(IEnumerable<ISftpFile> files)
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

        private void FetchUserGroupMaps(Renci.SshNet.SshClient ssh)
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

        // ── 下载 ─────────────────────────────────────────────────

        public async Task DownloadAsync(IEnumerable<RemoteFile> items, string localDir)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if (UpActive == 0 && DownActive == 0)
            {
                _transferCts = new CancellationTokenSource();
                DownTotal = 0; DownDone = 0;
                UpTotal = 0; UpDone = 0;
            }

            foreach (var item in items)
            {
                int count = item.IsDirectory ? await CountRemoteFilesAsync(item.FullName) : 1;
                DownTotal += count;
                DownActive++;
                _ = DownloadItemAsync(item, localDir, sftp);
            }
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

        private async Task DownloadItemAsync(RemoteFile item, string localDir, SftpClient sftp)
        {
            Task.Run(() =>
            {
                try
                {
                    string localPath = GetUniqueLocalPath(Path.Combine(localDir, item.Name));
                    DownName = item.Name;
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
                            sftp.DownloadFile(item.FullName, s, uploaded =>
                            {
                                DownProgress = item.Length > 0 ? (double)uploaded / item.Length * 100 : 0;
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException || ex is Renci.SshNet.Common.SshException)
                { }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowMessage?.Invoke($"下载失败 {item.Name}", ex.Message));
                }
                finally
                {
                    DownDone++;
                    DownActive--;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                }
            });
        }

        private void DownloadItemSync(ISftpFile item, string localDir, SftpClient sftp)
        {
            if (_transferCts?.IsCancellationRequested == true) return;
            string lp = Path.Combine(localDir, item.Name);
            DownName = item.Name;
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
                try
                {
                    using var s = File.Create(lp);
                    using (var reg = _transferCts?.Token.Register(() => { try { s.Close(); } catch { } }))
                    {
                        sftp.DownloadFile(item.FullName, s, uploaded =>
                        {
                            DownProgress = item.Length > 0 ? (double)uploaded / item.Length * 100 : 0;
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                        });
                    }
                    DownDone++;
                }
                catch { }
            }
        }

        // ── 上传 ─────────────────────────────────────────────────

        public void UploadLocalItem(string localPath, string remoteDir)
        {
            var session = _session;
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if (UpActive == 0 && DownActive == 0)
            {
                _transferCts = new CancellationTokenSource();
                UpTotal = 0; UpDone = 0;
                DownTotal = 0; DownDone = 0;
            }

            int count = CountLocalFiles(localPath);
            UpTotal += count;
            UpActive++;

            Task.Run(() =>
            {
                try
                {
                    bool isDir = (File.GetAttributes(localPath) & FileAttributes.Directory) == FileAttributes.Directory;
                    string name = Path.GetFileName(localPath.TrimEnd('\\', '/')), rp = remoteDir.TrimEnd('/') + "/" + name;
                    UpName = name;
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
                                sftp.UploadFile(s, rp, uploaded =>
                                {
                                    UpProgress = fileSize > 0 ? (double)uploaded / fileSize * 100 : 0;
                                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                                });
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException || ex is Renci.SshNet.Common.SshException)
                { }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowMessage?.Invoke("上传失败", ex.Message));
                }
                finally
                {
                    UpDone++;
                    UpActive--;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        UpdateTransferStatus();
                        if (UpActive == 0 && DownActive == 0) LoadPath(CurrentPath, true);
                    });
                }
            });
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
                    UpName = fileName;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                    using var s = File.OpenRead(f);
                    using (var reg = _transferCts?.Token.Register(() => { try { s.Close(); } catch { } }))
                    {
                        lock (session.SftpLock)
                        {
                            sftp.UploadFile(s, remoteDir.TrimEnd('/') + "/" + fileName,
                                uploaded => { UpProgress = fileSize > 0 ? (double)uploaded / fileSize * 100 : 0; });
                        }
                    }
                    UpDone++;
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

        private static string GetUniqueLocalPath(string p)
        {
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
            string dir = Path.GetDirectoryName(p) ?? "", name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
            int c = 1;
            while (File.Exists(p) || Directory.Exists(p)) { p = Path.Combine(dir, $"{name} ({c}){ext}"); c++; }
            return p;
        }

        // ── 删除/重命名 ──────────────────────────────────────────

        public void Delete(IEnumerable<RemoteFile> items)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;
            UpActive++;
            Task.Run(() =>
            {
                try
                {
                    foreach (var item in items)
                    {
                        if (item.IsDirectory) RecursiveDelete(item.FullName, sftp);
                        else sftp.DeleteFile(item.FullName);
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowMessage?.Invoke("删除失败", ex.Message));
                }
                finally
                {
                    UpActive--;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => LoadPath(CurrentPath, true));
                }
            });
        }

        private void RecursiveDelete(string dir, SftpClient sftp)
        {
            foreach (var f in sftp.ListDirectory(dir))
            {
                if (f.Name != "." && f.Name != "..")
                {
                    if (f.IsDirectory) RecursiveDelete(f.FullName, sftp);
                    else sftp.DeleteFile(f.FullName);
                }
            }
            sftp.DeleteDirectory(dir);
        }

        public void Rename(RemoteFile file, string newName)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;
            try
            {
                sftp.RenameFile(file.FullName, $"{CurrentPath.TrimEnd('/')}/{newName}");
                LoadPath(CurrentPath, true);
            }
            catch (Exception ex) { ShowMessage?.Invoke("重命名失败", ex.Message); }
        }

        // ── 复制 / 粘贴 ──────────────────────────────────────────

        public string BuildCopyText(IEnumerable<RemoteFile> items)
        {
            return $"FreeWPFRemoteCopy|{_session.HostInfo.Id}|" + string.Join("|", items.Select(x => x.FullName));
        }

        public void Paste(string clipboardText)
        {
            var sftp = Sftp;
            var ssh = Ssh;
            if (sftp == null || ssh == null || !sftp.IsConnected) return;

            if (string.IsNullOrEmpty(clipboardText)) return;
            if (clipboardText.StartsWith($"FreeWPFRemoteCopy|{_session.HostInfo.Id}|"))
            {
                UpActive++;
                Task.Run(() =>
                {
                    try
                    {
                        foreach (var src in clipboardText.Split('|').Skip(2))
                            ssh.CreateCommand($"cp -a \"{src}\" \"{CurrentPath}/\"").Execute();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowMessage?.Invoke("粘贴失败", ex.Message));
                    }
                    finally
                    {
                        UpActive--;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => LoadPath(CurrentPath, true));
                    }
                });
            }
        }

        public void Edit(RemoteFile file, string editor)
        {
            if (file != null && !file.IsDirectory)
                _ = _session.EditRemoteFileAsync(file.FullName, editor);
        }

        // ── 传输状态 ─────────────────────────────────────────────

        private void UpdateTransferStatus()
        {
            bool transferring = UpActive > 0 || DownActive > 0;
            IsTransferring = transferring;

            if (transferring)
            {
                var sb = new StringBuilder(256);
                if (UpActive > 0 || (UpTotal > 0 && UpDone == UpTotal))
                    sb.Append("上传: (").Append(UpDone).Append('/').Append(UpTotal).Append(") [")
                      .Append((UpActive > 0 ? UpProgress : 100).ToString("F1")).Append("%] - ")
                      .AppendLine(UpActive > 0 ? UpName : "已完成");
                if (DownActive > 0 || (DownTotal > 0 && DownDone == DownTotal))
                    sb.Append("下载: (").Append(DownDone).Append('/').Append(DownTotal).Append(") [")
                      .Append((DownActive > 0 ? DownProgress : 100).ToString("F1")).Append("%] - ")
                      .AppendLine(DownActive > 0 ? DownName : "已完成");
                sb.Append("\n双击可取消所有任务");
                StatusText = sb.ToString();
            }
            else
            {
                StatusText = "当前没有传输任务";
                UpTotal = UpDone = 0;
                DownTotal = DownDone = 0;
            }
        }

        [RelayCommand]
        private void CancelAllTransfers()
        {
            _transferCts?.Cancel();
        }
    }
}
