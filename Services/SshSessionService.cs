using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Share;
using Renci.SshNet;

namespace FreeWPFShell.Services
{
    public class SshSessionService : IDisposable, INotifyPropertyChanged
    {
        private static int _sessionCounter = 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string SessionId { get; }
        public int SessionIndex { get; }
        public SshConnectionInfo HostInfo { get; }
        public string DisplayName { get; }

        public SshClient? MasterClient { get; private set; }
        public SftpClient? SftpClient { get; private set; }
        public SshTerminalConnection? TerminalConnection { get; private set; }

        public bool IsConnected { get; private set; }

        private bool _isSftpConnected;
        public bool IsSftpConnected
        {
            get => _isSftpConnected;
            private set { if (_isSftpConnected != value) { _isSftpConnected = value; OnPropertyChanged(); } }
        }

        private string _connectionStatus = "准备连接...";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { if (_connectionStatus != value) { _connectionStatus = value; OnPropertyChanged(); } }
        }

        private bool _isAppCursorMode;
        public bool IsAppCursorMode
        {
            get => _isAppCursorMode;
            set { if (_isAppCursorMode != value) { _isAppCursorMode = value; OnPropertyChanged(); } }
        }

        private readonly List<SshTunnelInfo> _associatedTunnels = new();
        private readonly SettingsRepository _settingsRepo;
        private readonly object _sftpLock = new();
        public object SftpLock => _sftpLock;

        private RemoteFileService? _fileService;
        private SshMonitorService? _monitorService;

        public MonitorData Monitor { get; } = new();
        public event EventHandler<MonitorData>? MonitorUpdated;

        public SshSessionService(SshConnectionInfo hostInfo, SettingsRepository? settingsRepo = null)
        {
            HostInfo = hostInfo;
            _settingsRepo = settingsRepo ?? new SettingsRepository();
            SessionId = Guid.NewGuid().ToString("N");
            SessionIndex = Interlocked.Increment(ref _sessionCounter) - 1;
            string baseName = string.IsNullOrEmpty(hostInfo.HostName) ? hostInfo.IpAddress : hostInfo.HostName;
            DisplayName = $"{baseName} #{SessionIndex}";
        }

        public async Task EditRemoteFileAsync(string remotePath, string editorCommand)
        {
            if (_fileService != null)
                await _fileService.EditRemoteFileAsync(remotePath, editorCommand);
        }

        public async Task ConnectAsync()
        {
            PrivateKeyFile? preloadedKey = null;
            if (HostInfo.AuthMethod == SshAuthMethod.PrivateKey)
            {
                if (string.IsNullOrEmpty(HostInfo.SshKeyId))
                    throw new Exception("未配置 SSH 密钥，请在连接设置中选择一个已导入的密钥。");

                var keyRepo = new Repositories.KeyRepository();
                preloadedKey = await keyRepo.LoadPrivateKeyFileAsync(HostInfo.SshKeyId);
            }

            try
            {
                ConnectionStatus = "SSH.NET 建立连接...";
                await Task.Run(() =>
                {
                    MasterClient = BuildSshClient(preloadedKey);
                    MasterClient.Connect();
                });

                IsConnected = true;
                TerminalConnection = new SshTerminalConnection(MasterClient!, 120, 30);
                TerminalConnection.InjectChineseLocale = _settingsRepo.Load().InjectChineseLocale;
                TerminalConnection.AppCursorModeChanged += (isApp) =>
                {
                    IsAppCursorMode = isApp;
                };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        ConnectionStatus = "SFTP 建立连接...";
                        var sftp = BuildSftpClient(preloadedKey);
                        await Task.Run(() => sftp.Connect());
                        SftpClient = sftp;
                        IsSftpConnected = true;

                        _fileService = new RemoteFileService(SftpClient, _sftpLock, SessionId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("SFTP Connection Failed: " + ex.Message);
                    }

                    try
                    {
                        if (MasterClient != null && SftpClient != null)
                        {
                            _monitorService = new SshMonitorService(MasterClient, SftpClient, HostInfo, SessionId, _settingsRepo, _sftpLock, Monitor);
                            _monitorService.MonitorUpdated += (s, e) => MonitorUpdated?.Invoke(this, e);
                            _monitorService.ConnectionStatusCallback = (status) => ConnectionStatus = status;
                            _monitorService.RegisterTunnelCallback = RegisterTunnel;
                            _monitorService.Start();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Monitor Init Failed: " + ex.Message);
                    }

                    ConnectionStatus = "已连接";
                });
            }
            catch (Exception ex)
            {
                ConnectionStatus = "连接失败: " + ex.Message;
                throw;
            }
        }

        private ConnectionInfo BuildConnectionInfo(PrivateKeyFile? preloadedKey = null)
        {
            var authMethods = new List<AuthenticationMethod>(2);

            if (HostInfo.AuthMethod == SshAuthMethod.Password)
            {
                authMethods.Add(new PasswordAuthenticationMethod(
                    HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? ""));
            }
            else
            {
                if (preloadedKey == null)
                    throw new Exception("密钥未预加载，请确保在连接前已加载密钥。");
                authMethods.Add(new PrivateKeyAuthenticationMethod(HostInfo.SshUser, preloadedKey));
            }

            if (HostInfo.UseProxy && HostInfo.Proxy != null)
            {
                ProxyTypes proxyType = HostInfo.Proxy.Type switch
                {
                    ProxyType.Http => ProxyTypes.Http,
                    ProxyType.Socks4 => ProxyTypes.Socks4,
                    ProxyType.Socks5 => ProxyTypes.Socks5,
                    _ => ProxyTypes.None
                };
                return new ConnectionInfo(
                    HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser,
                    proxyType, HostInfo.Proxy.ServerAddress, HostInfo.Proxy.Port,
                    HostInfo.Proxy.Username, HostInfo.Proxy.Password,
                    authMethods.ToArray());
            }

            var conn = new ConnectionInfo(
                HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser,
                authMethods.ToArray());
            conn.Encoding = Encoding.UTF8;
            return conn;
        }

        private SshClient BuildSshClient(PrivateKeyFile? preloadedKey = null)
        {
            var client = new SshClient(BuildConnectionInfo(preloadedKey));
            client.ErrorOccurred += (sender, e) =>
            {
                try { Debug.WriteLine($"[SshClient Error] {e.Exception?.Message}"); } catch { }
            };
            return client;
        }

        private SftpClient BuildSftpClient(PrivateKeyFile? preloadedKey = null)
        {
            var client = new SftpClient(BuildConnectionInfo(preloadedKey));
            client.ErrorOccurred += (sender, e) =>
            {
                try { Debug.WriteLine($"[SftpClient Error] {e.Exception?.Message}"); } catch { }
            };
            return client;
        }

        public void RegisterTunnel(SshTunnelInfo tunnel)
        {
            lock (_associatedTunnels) { _associatedTunnels.Add(tunnel); }
            SshTunnelManager.Instance.RegisterTunnel(tunnel);
        }

        #region Linux Monitor API Delegation
        public uint LinuxMonitorLocalPort => _monitorService?.LinuxMonitorLocalPort ?? 0;
        public Task<ProcessDetail?> GetProcessDetailAsync(uint pid) => _monitorService?.GetProcessDetailAsync(pid) ?? Task.FromResult<ProcessDetail?>(null);
        public Task<bool> KillProcessAsync(uint pid, int signal) => _monitorService?.KillProcessAsync(pid, signal) ?? Task.FromResult(false);
        public Task<List<ProcessItem>> GetAllProcessesAsync() => _monitorService?.GetAllProcessesAsync() ?? Task.FromResult(new List<ProcessItem>());
        public Task<List<LoginRecord>> GetLoginRecordsAsync(string endpoint) => _monitorService?.GetLoginRecordsAsync(endpoint) ?? Task.FromResult(new List<LoginRecord>());
        public Task<List<ServiceItem>> GetServicesAsync() => _monitorService?.GetServicesAsync() ?? Task.FromResult(new List<ServiceItem>());
        public Task<bool> ServiceActionAsync(string serviceName, string action) => _monitorService?.ServiceActionAsync(serviceName, action) ?? Task.FromResult(false);
        public Task<string> GetServiceLogAsync(string serviceName) => _monitorService?.GetServiceLogAsync(serviceName) ?? Task.FromResult("");
        public Task<bool> KillAllProcessesAsync(string fullPath, int signal) => _monitorService?.KillAllProcessesAsync(fullPath, signal) ?? Task.FromResult(false);
        public Task<List<NetConnItem>> GetNetConnsAsync() => _monitorService?.GetNetConnsAsync() ?? Task.FromResult(new List<NetConnItem>());
        public Task<List<CronJobItem>> GetCronJobsAsync() => _monitorService?.GetCronJobsAsync() ?? Task.FromResult(new List<CronJobItem>());
        public Task<bool> AddCronJobAsync(string rawLine) => _monitorService?.AddCronJobAsync(rawLine) ?? Task.FromResult(false);
        public Task<bool> RemoveCronJobAsync(int lineIndex) => _monitorService?.RemoveCronJobAsync(lineIndex) ?? Task.FromResult(false);
        public Task<bool> ToggleCronJobAsync(int lineIndex, bool enabled) => _monitorService?.ToggleCronJobAsync(lineIndex, enabled) ?? Task.FromResult(false);
        public Task<string> GetCronStatusAsync() => _monitorService?.GetCronStatusAsync() ?? Task.FromResult("未连接");
        #endregion

        public void Disconnect()
        {
            _fileService?.Dispose();
            _fileService = null;

            _monitorService?.Stop();
            _monitorService = null;

            try
            {
                string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", SessionId);
                if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
            }
            catch { }

            IsConnected = false;
            IsSftpConnected = false;

            Task.Run(() =>
            {
                try
                {
                    lock (_associatedTunnels)
                    {
                        foreach (var tunnel in _associatedTunnels)
                        {
                            try
                            {
                                if (tunnel.PortConfig != null && tunnel.PortConfig.IsStarted) tunnel.PortConfig.Stop();
                                SshTunnelManager.Instance.UnregisterTunnel(tunnel.Id);
                            }
                            catch { }
                        }
                        _associatedTunnels.Clear();
                    }

                    lock (_sftpLock)
                    {
                        SftpClient?.Disconnect();
                        SftpClient?.Dispose();
                        SftpClient = null;
                    }
                    MasterClient?.Disconnect(); MasterClient?.Dispose();
                }
                catch { }
            });

            TerminalConnection?.Close();
            TerminalConnection = null;
        }

        public void Dispose() => Disconnect();
    }
}
