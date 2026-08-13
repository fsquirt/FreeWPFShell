using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Renci.SshNet;

namespace YouShell.Services
{
    public class RemoteFileService : IDisposable
    {
        private readonly SftpClient _sftpClient;
        private readonly object _sftpLock;
        private readonly string _sessionId;
        private readonly Dictionary<string, FileSystemWatcher> _activeWatchers = new();

        public RemoteFileService(SftpClient sftpClient, object sftpLock, string sessionId)
        {
            _sftpClient = sftpClient;
            _sftpLock = sftpLock;
            _sessionId = sessionId;
        }

        /// <summary>UI 提示回调（message, title），由上层注入；null 时静默忽略。</summary>
        public Action<string, string>? ShowMessage { get; set; }

        public async Task EditRemoteFileAsync(string remotePath, string editorCommand)
        {
            if (_sftpClient == null || !_sftpClient.IsConnected)
            {
                ShowMessage?.Invoke("SFTP 未连接，无法编辑文件", "错误");
                return;
            }

            try
            {
                string fileName = Path.GetFileName(remotePath);
                string pathHash = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(remotePath))).Replace("-", "").Substring(0, 8);
                string localDir = Path.Combine(Path.GetTempPath(), "YouShell", _sessionId, pathHash);
                if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                string localPath = Path.Combine(localDir, fileName);

                StopFileWatcher(localPath);

                using (var fs = File.Create(localPath))
                {
                    await Task.Run(() => {
                        lock (_sftpLock)
                        {
                            _sftpClient.DownloadFile(remotePath, fs);
                        }
                    });
                }

                StartFileWatcher(localPath, remotePath);

                try
                {
                    var psi = new ProcessStartInfo(editorCommand, $"\"{localPath}\"")
                    {
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    ShowMessage?.Invoke($"无法启动编辑器 '{editorCommand}':\n{ex.Message}\n\n请检查该程序是否已安装并已添加到系统环境变量 PATH 中。", "启动失败");
                }
            }
            catch (Exception ex)
            {
                ShowMessage?.Invoke($"下载文件失败: {ex.Message}", "错误");
            }
        }

        private void StopFileWatcher(string localPath)
        {
            lock (_activeWatchers)
            {
                if (_activeWatchers.TryGetValue(localPath, out var watcher))
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    _activeWatchers.Remove(localPath);
                }
            }
        }

        private void StartFileWatcher(string localPath, string remotePath)
        {
            string localDir = Path.GetDirectoryName(localPath)!;
            string fileName = Path.GetFileName(localPath);

            StopFileWatcher(localPath);

            var watcher = new FileSystemWatcher(localDir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            DateTime lastUploadTime = DateTime.MinValue;

            FileSystemEventHandler handler = (s, e) =>
            {
                if ((DateTime.Now - lastUploadTime).TotalMilliseconds < 500) return;
                lastUploadTime = DateTime.Now;

                Task.Run(async () =>
                {
                    try
                    {
                        int retry = 10;
                        while (retry-- > 0)
                        {
                            try
                            {
                                using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    lock (_sftpLock)
                                    {
                                        if (_sftpClient != null && _sftpClient.IsConnected)
                                        {
                                            _sftpClient.UploadFile(fs, remotePath, true);
                                        }
                                    }
                                }
                                break;
                            }
                            catch { await Task.Delay(100); }
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"[Editor] Upload Error: {ex.Message}"); }
                });
            };

            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += (s, e) => handler(s, e);

            lock (_activeWatchers)
            {
                _activeWatchers[localPath] = watcher;
            }
        }

        public void Dispose()
        {
            lock (_activeWatchers)
            {
                foreach (var w in _activeWatchers.Values) w.Dispose();
                _activeWatchers.Clear();
            }
        }
    }
}
