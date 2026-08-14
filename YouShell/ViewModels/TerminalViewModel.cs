using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouShell.Models;
using YouShell.Repositories;
using YouShell.Services;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace YouShell.ViewModels
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

        // 传输任务列表（每个上传/下载一个任务，支持暂停/继续/取消）
        public ObservableCollection<TransferTask> TransferTasks { get; } = new();

        // 并行传输上限（并发 SFTP 最大传输个数，可在设置中配置，默认 1）
        private readonly SemaphoreSlim _transferSlots;
        private readonly int _maxConcurrentTransfers;
        private readonly SettingsRepository _settingsRepo = new();

        // ── 诊断日志（定位「进度/速度长时间为 0」用；输出到控制台，调试时用 Console 查看） ──
        private static int s_diagProgressCount;
        private static void Diag(string msg)
        {
            try
            {
                Console.WriteLine($"[transfer-diag] {DateTime.Now:HH:mm:ss.fff} {msg}");
            }
            catch { }
        }

        // 状态图标/提示（由 Code-behind 或 UI 读取）
        [ObservableProperty]
        private string _statusText = "当前没有传输任务";
        [ObservableProperty]
        private bool _isTransferring;

        // UID/GID → 用户名/组名 缓存
        private Dictionary<int, string> _userMap = new();
        private Dictionary<int, string> _groupMap = new();

        private SftpClient? Sftp => _session.SftpClient;
        private Renci.SshNet.SshClient? Ssh => _session.MasterClient;

        // UI 交互回调
        public Action<string, string>? ShowMessage { get; set; }
        public Func<string, string, Task<bool>>? Confirm { get; set; }
        public event Action? TransferStateChanged;

        public TerminalViewModel(SshSessionService session)
        {
            _session = session;
            _maxConcurrentTransfers = Math.Clamp(_settingsRepo.Load().MaxConcurrentTransfers, 1, 16);
            _transferSlots = new SemaphoreSlim(_maxConcurrentTransfers, _maxConcurrentTransfers);
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
                    // 并发=1 时传输会长时间占用主连接（持 _sftpLock），这里同样加锁避免与传输并发操作同一 SftpClient
                    var files = new List<ISftpFile>();
                    lock (_session.SftpLock) files = sftp.ListDirectory(path).ToList();
                    var items = BuildFileList(files);
                    YouShell.Core.UiDispatcher.Enqueue(() =>
                    {
                        Files.Clear();
                        foreach (var item in items) Files.Add(item);
                    });
                }
                catch (Exception ex)
                {
                    YouShell.Core.UiDispatcher.Enqueue(() =>
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
                    var files = new List<ISftpFile>();
                    lock (_session.SftpLock) files = sftp.ListDirectory(workingDir).ToList();
                    var items = BuildFileList(files);
                    YouShell.Core.UiDispatcher.Enqueue(() =>
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

            var dirTasks = new List<Task>();
            foreach (var item in items)
            {
                if (item.IsDirectory)
                    dirTasks.Add(DownloadDirectoryAsync(item, localDir));
                else
                    StartDownloadFile(item.Name, item.FullName, item.Length,
                        GetUniqueLocalPath(Path.Combine(localDir, item.Name)));
            }
            await Task.WhenAll(dirTasks);
        }

        private async Task DownloadDirectoryAsync(RemoteFile dir, string localDir)
        {
            var entries = await Task.Run(() => EnumerateRemoteFiles(dir.FullName));
            string baseDir = Path.Combine(localDir, dir.Name);
            foreach (var (remotePath, length) in entries)
            {
                if (string.IsNullOrEmpty(remotePath)) continue;
                string rel = remotePath.Length > dir.FullName.Length
                    ? remotePath[(dir.FullName.Length + 1)..]
                    : Path.GetFileName(remotePath);
                string local = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
                StartDownloadFile(Path.GetFileName(remotePath), remotePath, length, local);
            }
        }

        private List<(string Path, long Length)> EnumerateRemoteFiles(string dirPath)
        {
            var result = new List<(string, long)>();
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return result;
            try
            {
                lock (_session.SftpLock) CollectRemoteFiles(sftp, dirPath, result);
            }
            catch { }
            return result;
        }

        private static void CollectRemoteFiles(SftpClient sftp, string dirPath, List<(string, long)> result)
        {
            foreach (var f in sftp.ListDirectory(dirPath))
            {
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory) CollectRemoteFiles(sftp, f.FullName, result);
                else result.Add((f.FullName, f.Length));
            }
        }

        private void StartDownloadFile(string name, string remotePath, long length, string localPath)
        {
            var task = new TransferTask
            {
                Direction = TransferDirection.Download,
                FileName = name,
                RemotePath = remotePath,
                LocalPath = localPath,
                TotalBytes = length,
                Cts = new CancellationTokenSource(),
            };
            YouShell.Core.UiDispatcher.Run(() => TransferTasks.Add(task));
            _ = TransferFileAsync(task);
        }

        // ── 上传 ─────────────────────────────────────────────────

        public void UploadLocalItem(string localPath, string remoteDir)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;

            bool isDir = (File.GetAttributes(localPath) & FileAttributes.Directory) == FileAttributes.Directory;
            string name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
            string remoteBase = remoteDir.TrimEnd('/');

            if (!isDir)
            {
                StartUploadFile(name, localPath, remoteBase + "/" + name, new FileInfo(localPath).Length);
                return;
            }

            // 目录：先在主连接上建好远程目录结构，再并行上传文件
            Task.Run(() =>
            {
                try
                {
                    string baseDir = localPath.TrimEnd('\\', '/');
                    var dirs = Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories);
                    lock (_session.SftpLock)
                    {
                        EnsureRemoteDir(sftp, remoteBase + "/" + name);
                        foreach (var d in dirs.OrderBy(x => x.Count(c => c == Path.DirectorySeparatorChar)))
                        {
                            string rel = Path.GetRelativePath(baseDir, d).Replace('\\', '/');
                            EnsureRemoteDir(sftp, remoteBase + "/" + name + "/" + rel);
                        }
                    }

                    foreach (var f in Directory.GetFiles(localPath, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(baseDir, f).Replace('\\', '/');
                        StartUploadFile(Path.GetFileName(f), f, remoteBase + "/" + name + "/" + rel, new FileInfo(f).Length);
                    }
                }
                catch (Exception ex)
                {
                    YouShell.Core.UiDispatcher.Enqueue(() => ShowMessage?.Invoke("上传失败", ex.Message));
                }
            });
        }

        private void StartUploadFile(string name, string localPath, string remotePath, long length)
        {
            var task = new TransferTask
            {
                Direction = TransferDirection.Upload,
                FileName = name,
                RemotePath = remotePath,
                LocalPath = localPath,
                TotalBytes = length,
                Cts = new CancellationTokenSource(),
            };
            YouShell.Core.UiDispatcher.Run(() => TransferTasks.Add(task));
            _ = TransferFileAsync(task);
        }

        private static void EnsureRemoteDir(SftpClient sftp, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return;
            bool exists;
            try { exists = sftp.Exists(path); } catch { exists = true; }
            if (exists) return;
            int idx = path.LastIndexOf('/');
            if (idx > 0) EnsureRemoteDir(sftp, path[..idx]);
            try { sftp.CreateDirectory(path); } catch { }
        }

        private static string GetUniqueLocalPath(string p)
        {
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
            string dir = Path.GetDirectoryName(p) ?? "", name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
            int c = 1;
            while (File.Exists(p) || Directory.Exists(p)) { p = Path.Combine(dir, $"{name} ({c}){ext}"); c++; }
            return p;
        }

        // ── 传输引擎 ─────────────────────────────────────────────

        private async Task TransferFileAsync(TransferTask task)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                Diag($"[transfer] 开始 {task.Direction} '{task.FileName}' total={task.TotalBytes} remote='{task.RemotePath}' local='{task.LocalPath}'");
                var tWait = DateTime.UtcNow;
                await _transferSlots.WaitAsync(task.Cts!.Token);
                Diag($"[transfer] 已获槽 '{task.FileName}' 等槽耗时={(DateTime.UtcNow - tWait).TotalMilliseconds:F0}ms");

                try
                {
                    var tCheck = DateTime.UtcNow;
                    bool useTunnel = ShouldUseTunnelTransfer();
                    Diag($"[transfer] '{task.FileName}' ShouldUseTunnelTransfer={useTunnel} (判定耗时={(DateTime.UtcNow - tCheck).TotalMilliseconds:F1}ms)");
                    if (useTunnel)
                    {
                        Diag($"[transfer] 走隧道多线程路径 '{task.FileName}'");
                        // 隧道多线程：通过 Linux Monitor 的 HTTP 分段接口并行传输单个文件
                        await DoTunnelTransferAsync(task);
                    }
                    else
                    {
                        Diag($"[transfer] 走普通 SFTP 路径 '{task.FileName}'");
                        // 网络连接握手 + 整个文件读写都是阻塞操作，必须放到后台线程，
                        // 否则会跑在 UI 线程上把界面卡死。
                        await Task.Run(() =>
                        {
                            // 并发>1 时 SftpClient 非线程安全，必须各自新建独立连接（SSH 握手开销是并发的必要代价）；
                            // 并发=1（默认）直接复用已连接的主连接，避免每次传输都做一次完整 SSH 握手——
                            // 否则高延迟服务器上「进度/速度长时间为 0」其实是在等重连。
                            if (_maxConcurrentTransfers > 1)
                            {
                                using var parallel = _session.OpenParallelSftpClient();
                                var client = parallel ?? _session.SftpClient;
                                if (client == null || !client.IsConnected) throw new InvalidOperationException("SFTP 未连接");
                                if (parallel != null) DoTransfer(client, task);
                                else lock (_session.SftpLock) DoTransfer(client, task);
                            }
                            else
                            {
                                var client = _session.SftpClient;
                                if (client == null || !client.IsConnected) throw new InvalidOperationException("SFTP 未连接");
                                lock (_session.SftpLock) DoTransfer(client, task);
                            }
                        });
                    }

                    YouShell.Core.UiDispatcher.Run(() => task.Status = TransferStatus.Completed);
                }
                finally
                {
                    _transferSlots.Release();
                }
            }
            catch (OperationCanceledException)
            {
                bool paused = task.Status == TransferStatus.Paused;
                YouShell.Core.UiDispatcher.Run(() =>
                {
                    if (task.Status != TransferStatus.Paused)
                        task.Status = TransferStatus.Canceled;
                });
                if (!paused) TryDeletePartialFile(task);
            }
            catch (Exception)
            {
                bool paused = task.Status == TransferStatus.Paused;
                YouShell.Core.UiDispatcher.Run(() =>
                {
                    if (task.Status != TransferStatus.Paused)
                        task.Status = TransferStatus.Failed;
                });
                if (!paused) TryDeletePartialFile(task);
            }
            finally
            {
                RefreshOverallStatus();
            }
        }

        private static void DoTransfer(SftpClient client, TransferTask task)
        {
            if (task.Direction == TransferDirection.Download) DownloadFile(client, task);
            else UploadFile(client, task);
        }

        private static void DownloadFile(SftpClient client, TransferTask task)
        {
            var t0 = DateTime.UtcNow;
            Diag($"[sftp-download] 开始 '{task.FileName}' remote='{task.RemotePath}' local='{task.LocalPath}' total={task.TotalBytes}");
            string? dir = Path.GetDirectoryName(task.LocalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(task.LocalPath);
            using var reg = task.Cts!.Token.Register(() => { try { fs.Close(); } catch { } });
            try
            {
                client.DownloadFile(task.RemotePath, fs, uploaded => UpdateTaskProgress(task, (long)uploaded));
                Diag($"[sftp-download] 完成 '{task.FileName}' 耗时={(DateTime.UtcNow - t0).TotalSeconds:F1}s");
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // 暂停/取消通过关闭流来中断传输，把随之抛出的 ObjectDisposedException/IOException
                // 规范化为 OperationCanceledException，走正常的取消分支，避免被当成错误上报。
                task.Cts!.Token.ThrowIfCancellationRequested();
                throw;
            }
        }

        private static void UploadFile(SftpClient client, TransferTask task)
        {
            var t0 = DateTime.UtcNow;
            Diag($"[sftp-upload] 开始 '{task.FileName}' local='{task.LocalPath}' remote='{task.RemotePath}' total={task.TotalBytes}");
            using var fs = File.OpenRead(task.LocalPath);
            using var reg = task.Cts!.Token.Register(() => { try { fs.Close(); } catch { } });
            try
            {
                client.UploadFile(fs, task.RemotePath, true, uploaded => UpdateTaskProgress(task, (long)uploaded));
                Diag($"[sftp-upload] 完成 '{task.FileName}' 耗时={(DateTime.UtcNow - t0).TotalSeconds:F1}s");
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                task.Cts!.Token.ThrowIfCancellationRequested();
                throw;
            }
        }

        private static void UpdateTaskProgress(TransferTask task, long uploaded)
        {
            if (Interlocked.Increment(ref s_diagProgressCount) <= 5)
                Diag($"[progress] uploaded={uploaded} total={task.TotalBytes}");
            task.TransferredBytes = uploaded;
            int pct = task.TotalBytes > 0 ? (int)(uploaded * 100 / task.TotalBytes) : 0;
            // 仅整百分比变化时才通知，避免高频率回调 flooding UI 线程；通知须在 UI 线程（WinRT PropertyChangedEventArgs）
            if ((int)task.Progress != pct)
                YouShell.Core.UiDispatcher.Run(() => task.Progress = pct);

            // 实时速度：约每 0.5s 采样一次（uploaded 为累计字节数，求增量/时间）
            var now = DateTime.UtcNow;
            double elapsed = (now - task._lastSpeedAt).TotalSeconds;
            if (elapsed >= 0.5)
            {
                double speed = elapsed > 0 ? (uploaded - task._lastBytes) / elapsed : 0;
                task._lastBytes = uploaded;
                task._lastSpeedAt = now;
                YouShell.Core.UiDispatcher.Run(() => task.Speed = Math.Max(0, speed));
            }
        }

        // ── 隧道多线程传输（通过 Linux Monitor 的 HTTP 分段接口并行读写单个文件） ──

        private bool ShouldUseTunnelTransfer()
        {
            var settings = _settingsRepo.Load();
            return settings.UseTunnelMultithreadTransfer
                && _session.MonitorService != null
                && _session.MonitorService.LinuxMonitorLocalPort > 0;
        }

        private async Task DoTunnelTransferAsync(TransferTask task)
        {
            var monitor = _session.MonitorService;
            if (monitor == null || monitor.LinuxMonitorLocalPort == 0)
                throw new InvalidOperationException("SSH 隧道监控未就绪");

            int threads = Math.Clamp(_settingsRepo.Load().TransferThreadsPerTask, 1, 64);
            long total = Math.Max(0, task.TotalBytes);
            Diag($"[tunnel] DoTunnelTransferAsync 入口 '{task.FileName}' dir={task.Direction} total={total} threads={threads} port={monitor.LinuxMonitorLocalPort}");

            // 切成固定 4MB 小块，用 threads 个并发 worker 流式传输：
            // 内存有界（约 threads × 4MB），且每块完成即更新一次进度/速度。
            const int BlockSize = 4 * 1024 * 1024;
            var blocks = new List<(long Offset, int Length)>();
            for (long off = 0; off < total; off += BlockSize)
            {
                int len = (int)Math.Min(BlockSize, total - off);
                blocks.Add((off, len));
            }
            Diag($"[tunnel] 分块完成 块数={blocks.Count}");

            if (task.Direction == TransferDirection.Download)
                await TunnelDownloadAsync(monitor, task, blocks, threads);
            else
                await TunnelUploadAsync(monitor, task, blocks, threads);
        }

        private static async Task TunnelDownloadAsync(SshMonitorService monitor, TransferTask task, List<(long Offset, int Length)> blocks, int threads)
        {
            string? dir = Path.GetDirectoryName(task.LocalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Diag($"[download] 打开本地文件 '{task.LocalPath}' 块数={blocks.Count} 线程={threads} 开始");
            var tStart = DateTime.UtcNow;
            using var fs = new FileStream(task.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            Diag($"[download] 本地文件已打开 耗时={(DateTime.UtcNow - tStart).TotalMilliseconds:F0}ms");
            // 不预先 SetLength：RandomAccess.WriteAsync 在偏移处写入会自动扩展文件（稀疏洞），
            // 避免超大文件 SetLength 的潜在耗时；所有块最终都会覆盖 [0, TotalBytes) 每个字节。

            long done = 0;
            var ct = task.Cts!.Token;
            using var gate = new SemaphoreSlim(threads, threads);
            var firstBlockStart = DateTime.MinValue;
            await Task.WhenAll(blocks.Select(async block =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var tb = DateTime.UtcNow;
                    if (block.Offset == 0) firstBlockStart = tb;
                    Diag($"[download] 请求块 offset={block.Offset} len={block.Length} 并发中");
                    var data = await monitor.ReadFileRangeAsync(task.RemotePath, block.Offset, block.Length, ct).ConfigureAwait(false);
                    var rb = DateTime.UtcNow;
                    Diag($"[download] 收到块 offset={block.Offset} len={(data?.Length ?? 0)} 请求耗时={(rb - tb).TotalMilliseconds:F0}ms");
                    if (data == null || data.Length != block.Length)
                        throw new IOException($"读取远程片段失败: offset={block.Offset} 期望={block.Length} 实得={(data?.Length ?? 0)}");
                    await RandomAccess.WriteAsync(fs.SafeFileHandle, data, block.Offset, ct).ConfigureAwait(false);
                    Diag($"[download] 写入本地 offset={block.Offset} len={block.Length} 完成");
                    UpdateTaskProgress(task, Interlocked.Add(ref done, block.Length));
                }
                finally { gate.Release(); }
            }));
            Diag($"[download] 全部块完成 '{task.FileName}' 总耗时={(DateTime.UtcNow - tStart).TotalSeconds:F1}s");
        }

        private static async Task TunnelUploadAsync(SshMonitorService monitor, TransferTask task, List<(long Offset, int Length)> blocks, int threads)
        {
            Diag($"[upload] 开始远程截断 '{task.FileName}' 块数={blocks.Count} 线程={threads}");
            var tTrunc = DateTime.UtcNow;
            if (!await monitor.TruncateRemoteFileAsync(task.RemotePath, task.Cts!.Token).ConfigureAwait(false))
                throw new IOException("无法在远程创建目标文件");
            Diag($"[upload] 截断完成 '{task.FileName}' 耗时={(DateTime.UtcNow - tTrunc).TotalMilliseconds:F0}ms");

            var tStart = DateTime.UtcNow;
            using var fs = new FileStream(task.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);

            long done = 0;
            var ct = task.Cts!.Token;
            using var gate = new SemaphoreSlim(threads, threads);
            await Task.WhenAll(blocks.Select(async block =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var buf = new byte[block.Length];
                    int read = await RandomAccess.ReadAsync(fs.SafeFileHandle, buf, block.Offset, ct).ConfigureAwait(false);
                    if (read != block.Length) throw new IOException($"读取本地文件片段失败 offset={block.Offset} 期望={block.Length} 实得={read}");
                    Diag($"[upload] 读取本地 offset={block.Offset} len={block.Length} 完成，发请求");
                    var tw = DateTime.UtcNow;
                    bool ok = await monitor.WriteFileRangeAsync(task.RemotePath, block.Offset, buf, ct).ConfigureAwait(false);
                    Diag($"[upload] 写入远程 offset={block.Offset} len={block.Length} ok={ok} 请求耗时={(DateTime.UtcNow - tw).TotalMilliseconds:F0}ms");
                    if (!ok)
                        throw new IOException($"写入远程片段失败: offset={block.Offset}");
                    UpdateTaskProgress(task, Interlocked.Add(ref done, block.Length));
                }
                finally { gate.Release(); }
            }));
            Diag($"[upload] 全部块完成 '{task.FileName}' 总耗时={(DateTime.UtcNow - tStart).TotalSeconds:F1}s");
        }

        private static void TryDeletePartialFile(TransferTask task)
        {
            if (task.Direction != TransferDirection.Download) return;
            try { if (File.Exists(task.LocalPath)) File.Delete(task.LocalPath); } catch { }
        }

        private void RefreshOverallStatus()
        {
            YouShell.Core.UiDispatcher.Run(() =>
            {
                int active = TransferTasks.Count(t => t.Status is TransferStatus.Running or TransferStatus.Paused);
                int done = TransferTasks.Count(t => t.Status == TransferStatus.Completed);
                IsTransferring = active > 0;
                StatusText = active > 0
                    ? $"传输任务: {active} 个进行中，{done} 个已完成"
                    : "当前没有传输任务";
            });
        }

        public void PauseTask(TransferTask task)
        {
            task.Status = TransferStatus.Paused;
            task.Cts?.Cancel();
            RefreshOverallStatus();
        }

        public void ResumeTask(TransferTask task)
        {
            task.Cts = new CancellationTokenSource();
            task.TransferredBytes = 0;
            task.Progress = 0;
            task._lastBytes = 0;
            task._lastSpeedAt = DateTime.UtcNow;
            task.Status = TransferStatus.Running;
            _ = TransferFileAsync(task);
            RefreshOverallStatus();
        }

        public void CancelTask(TransferTask task)
        {
            task.Status = TransferStatus.Canceled;
            task.Cts?.Cancel();
            TryDeletePartialFile(task);
            RefreshOverallStatus();
        }

        // ── 删除/重命名 ──────────────────────────────────────────

        public void Delete(IEnumerable<RemoteFile> items)
        {
            var sftp = Sftp;
            if (sftp == null || !sftp.IsConnected) return;
            Task.Run(() =>
            {
                try
                {
                    lock (_session.SftpLock)
                    {
                        foreach (var item in items)
                        {
                            if (item.IsDirectory) RecursiveDelete(item.FullName, sftp);
                            else sftp.DeleteFile(item.FullName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    YouShell.Core.UiDispatcher.Enqueue(() => ShowMessage?.Invoke("删除失败", ex.Message));
                }
                finally
                {
                    YouShell.Core.UiDispatcher.Enqueue(() => LoadPath(CurrentPath, true));
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
                Task.Run(() =>
                {
                    try
                    {
                        foreach (var src in clipboardText.Split('|').Skip(2))
                            ssh.CreateCommand($"cp -a \"{src}\" \"{CurrentPath}/\"").Execute();
                    }
                    catch (Exception ex)
                    {
                        YouShell.Core.UiDispatcher.Enqueue(() => ShowMessage?.Invoke("粘贴失败", ex.Message));
                    }
                    finally
                    {
                        YouShell.Core.UiDispatcher.Enqueue(() => LoadPath(CurrentPath, true));
                    }
                });
            }
        }

        public void Edit(RemoteFile file, string editor)
        {
            if (file != null && !file.IsDirectory)
                _ = _session.EditRemoteFileAsync(file.FullName, editor);
        }

        [RelayCommand]
        private void CancelAllTransfers()
        {
            foreach (var t in TransferTasks.ToList())
                if (t.CanCancel) CancelTask(t);
        }
    }
}
