using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.Share;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FreeWPFShell.ViewModels
{

    public partial class TerminalViewModel : ObservableObject
    {
        private readonly SshSessionService _session;

        public ObservableCollection<RemoteFile> Files { get; } = new();
        public ObservableCollection<RemoteFile> SelectedFiles { get; } = new();

        private readonly Stack<string> _backHistory = new();
        private readonly Stack<string> _forwardHistory = new();

        [ObservableProperty]
        private string _currentPath = "/";


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


        [ObservableProperty]
        private string _statusText = "当前没有传输任务";
        [ObservableProperty]
        private bool _isTransferring;

        private readonly SftpTransferManager _transfers;

        private readonly object _counterLock = new();

        private void AddUpActive(int delta) { lock (_counterLock) { UpActive = Math.Max(0, UpActive + delta); } }
        private void AddUpTotal(int delta) { lock (_counterLock) { UpTotal = Math.Max(0, UpTotal + delta); } }
        private void AddUpDone(int delta) { lock (_counterLock) { UpDone = Math.Max(0, UpDone + delta); } }
        private void AddDownActive(int delta) { lock (_counterLock) { DownActive = Math.Max(0, DownActive + delta); } }
        private void AddDownTotal(int delta) { lock (_counterLock) { DownTotal = Math.Max(0, DownTotal + delta); } }
        private void AddDownDone(int delta) { lock (_counterLock) { DownDone = Math.Max(0, DownDone + delta); } }

        private void ResetTransferCounters()
        {
            lock (_counterLock)
            {
                UpActive = 0; UpTotal = 0; UpDone = 0;
                DownActive = 0; DownTotal = 0; DownDone = 0;
            }
        }


        private Dictionary<int, string> _userMap = new();
        private Dictionary<int, string> _groupMap = new();

        private SftpClient? Sftp => _session.SftpClient;
        private Renci.SshNet.SshClient? Ssh => _session.MasterClient;


        public Action<string, string>? ShowMessage { get; set; }
        public Func<string, string, bool>? Confirm { get; set; }
        public Action? TransferStateChanged { get; set; }

        public TerminalViewModel(SshSessionService session)
        {
            _session = session;
            _transfers = session.Transfers;
        }

        public SshSessionService Session => _session;

        public SftpTransferManager Transfers => _transfers;

        partial void OnIsTransferringChanged(bool value)
        {
            TransferStateChanged?.Invoke();
        }



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



        public async Task DownloadAsync(IEnumerable<RemoteFile> items, string localDir)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if ((UpActive == 0 && DownActive == 0) || _transfers.IsBatchCancelled)
            {
                _transfers.BeginBatch();
                ResetTransferCounters();
            }

            foreach (var item in items)
            {
                int count = item.IsDirectory ? await CountRemoteFilesAsync(item.FullName) : 1;
                AddDownTotal(count);
                AddDownActive(1);
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
            await Task.Run(() =>
            {
                bool failed = false;
                SftpTransferTask? task = null;
                string? localPath = null;
                try
                {
                    localPath = GetUniqueLocalPath(Path.Combine(localDir, item.Name));
                    DownName = item.Name;
                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(localPath);
                        foreach (var c in sftp.ListDirectory(item.FullName))
                        {
                            if (c.Name == "." || c.Name == "..") continue;
                            if (_transfers.IsBatchCancelled) break;
                            DownloadItemSync(c, localPath, sftp);
                        }
                    }
                    else
                    {
                        var t = _transfers.Create(SftpTransferKind.Download, item.Name, localPath, item.FullName, item.Length);
                        task = t;
                        t.Start();
                        RunDownload(sftp, t, item.FullName, localPath, item.Length, uploaded =>
                        {
                            DownProgress = item.Length > 0 ? (double)uploaded / item.Length * 100 : 0;
                            t.ReportProgress((long)uploaded);
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                        });
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException || ex is Renci.SshNet.Common.SshException)
                { }
                catch (Exception ex)
                {
                    failed = true;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowMessage?.Invoke($"下载失败 {item.Name}", ex.Message));
                }
                finally
                {
                    if (item.IsDirectory && localPath != null && _transfers.IsBatchCancelled)
                    {
                        try { Directory.Delete(localPath, false); } catch { }
                    }
                    FinishTask(task, failed);
                    AddDownDone(1);
                    AddDownActive(-1);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                }
            });
        }

        private static void FinishTask(SftpTransferTask? task, bool failed)
        {
            if (task == null) return;
            if (task.Cts.IsCancellationRequested) task.FinishCancelled();
            else if (failed) task.FinishFailed();
            else task.FinishCompleted();
        }

        private static void RunDownload(SftpClient sftp, SftpTransferTask task, string remotePath, string localPath, long expectedBytes, Action<ulong> onProgress)
        {
            try
            {
                using (var raw = File.Create(localPath))
                using (var s = new PausableStream(raw, task.Gate, task.Cts.Token))
                using (var reg = task.Cts.Token.Register(() => { try { s.Close(); } catch { } }))
                {
                    sftp.DownloadFile(remotePath, s, onProgress);
                }
                task.MarkArtifactComplete();
            }
            catch
            {
                try
                {
                    var fi = new FileInfo(localPath);
                    if (fi.Exists && (expectedBytes <= 0 || fi.Length < expectedBytes)) fi.Delete();
                }
                catch { }
                throw;
            }
        }

        private static void RunUpload(SftpClient sftp, SshSessionService session, SftpTransferTask task, string localPath, string remotePath, Action<ulong> onProgress)
        {
            long remoteBefore = -1;
            bool touched = false;
            try
            {
                using (var raw = File.OpenRead(localPath))
                using (var s = new PausableStream(raw, task.Gate, task.Cts.Token))
                using (var reg = task.Cts.Token.Register(() => { try { s.Close(); } catch { } }))
                {
                    task.WaitIfPaused();
                    lock (session.SftpLock)
                    {
                        try { remoteBefore = sftp.GetAttributes(remotePath).Size; } catch { remoteBefore = -1; }
                        touched = true;
                        sftp.UploadFile(s, remotePath, onProgress);
                    }
                }
                task.MarkArtifactComplete();
            }
            catch
            {
                if (touched) TryDeletePartialUpload(sftp, session, localPath, remotePath, remoteBefore);
                throw;
            }
        }

        private static void TryDeletePartialUpload(SftpClient sftp, SshSessionService session, string localPath, string remotePath, long remoteBefore)
        {
            try
            {
                if (!sftp.IsConnected) return;
                long localLength;
                try { localLength = new FileInfo(localPath).Length; } catch { return; }
                lock (session.SftpLock)
                {
                    if (!sftp.Exists(remotePath)) return;
                    long now = sftp.GetAttributes(remotePath).Size;
                    if (now >= localLength) return;
                    if (remoteBefore >= 0 && now == remoteBefore) return;
                    sftp.DeleteFile(remotePath);
                }
            }
            catch { }
        }

        private void DownloadItemSync(ISftpFile item, string localDir, SftpClient sftp)
        {
            if (_transfers.IsBatchCancelled) return;
            string lp = Path.Combine(localDir, item.Name);
            DownName = item.Name;
            if (item.IsDirectory)
            {
                bool created = !Directory.Exists(lp);
                Directory.CreateDirectory(lp);
                foreach (var c in sftp.ListDirectory(item.FullName))
                {
                    if (c.Name == "." || c.Name == "..") continue;
                    if (_transfers.IsBatchCancelled) break;
                    DownloadItemSync(c, lp, sftp);
                }
                if (created && _transfers.IsBatchCancelled) { try { Directory.Delete(lp, false); } catch { } }
            }
            else
            {
                lp = GetUniqueLocalPath(lp);
                var task = _transfers.Create(SftpTransferKind.Download, item.Name, lp, item.FullName, item.Length);
                task.Start();
                bool ok = false;
                try
                {
                    RunDownload(sftp, task, item.FullName, lp, item.Length, uploaded =>
                    {
                        DownProgress = item.Length > 0 ? (double)uploaded / item.Length * 100 : 0;
                        task.ReportProgress((long)uploaded);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                    });
                    ok = true;
                    AddDownDone(1);
                }
                catch { }
                finally
                {
                    FinishTask(task, !ok);
                }
            }
        }



        public void UploadLocalItem(string localPath, string remoteDir)
        {
            var session = _session;
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            if ((UpActive == 0 && DownActive == 0) || _transfers.IsBatchCancelled)
            {
                _transfers.BeginBatch();
                ResetTransferCounters();
            }

            int count = CountLocalFiles(localPath);
            AddUpTotal(count);
            AddUpActive(1);

            Task.Run(() =>
            {
                bool failed = false;
                SftpTransferTask? task = null;
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
                        var t = _transfers.Create(SftpTransferKind.Upload, name, localPath, rp, fileSize);
                        task = t;
                        t.Start();
                        RunUpload(sftp, session, t, localPath, rp, uploaded =>
                        {
                            UpProgress = fileSize > 0 ? (double)uploaded / fileSize * 100 : 0;
                            t.ReportProgress((long)uploaded);
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                        });
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException || ex is Renci.SshNet.Common.SshException)
                { }
                catch (Exception ex)
                {
                    failed = true;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowMessage?.Invoke("上传失败", ex.Message));
                }
                finally
                {
                    FinishTask(task, failed);
                    AddUpDone(1);
                    AddUpActive(-1);
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
            if (_transfers.IsBatchCancelled) return;
            var files = Directory.GetFiles(localDir);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _transfers.BatchToken }, f =>
            {
                SftpTransferTask? task = null;
                bool ok = false;
                try
                {
                    string fileName = Path.GetFileName(f);
                    long fileSize = new FileInfo(f).Length;
                    string rp = remoteDir.TrimEnd('/') + "/" + fileName;
                    var t = _transfers.Create(SftpTransferKind.Upload, fileName, f, rp, fileSize);
                    task = t;
                    t.Start();
                    UpName = fileName;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateTransferStatus);
                    RunUpload(sftp, session, t, f, rp, uploaded =>
                    {
                        UpProgress = fileSize > 0 ? (double)uploaded / fileSize * 100 : 0;
                        t.ReportProgress((long)uploaded);
                    });
                    ok = true;
                    AddUpDone(1);
                }
                catch { }
                finally
                {
                    FinishTask(task, !ok);
                }
            });

            if (_transfers.IsBatchCancelled) return;
            foreach (var d in Directory.GetDirectories(localDir))
            {
                if (_transfers.IsBatchCancelled) break;
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



        public void Delete(IEnumerable<RemoteFile> items)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;
            AddUpActive(1);
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
                    AddUpActive(-1);
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
                AddUpActive(1);
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
                        AddUpActive(-1);
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
                sb.Append("\n点击状态图标查看传输任务");
                StatusText = sb.ToString();
            }
            else
            {
                StatusText = "当前没有传输任务";
                ResetTransferCounters();
            }
        }

        [RelayCommand]
        private void CancelAllTransfers()
        {
            _transfers.CancelAll();
        }
    }
}
