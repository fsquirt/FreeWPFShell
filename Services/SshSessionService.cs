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
using System.Windows;
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
            set
            {
                if (_connectionStatus == value) return;
                _connectionStatus = value;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(ConnectionStatus)));
                else
                    OnPropertyChanged();
            }
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

        public Action? OnConnected { get; set; }
        public Action<Exception>? OnConnectFailed { get; set; }

        public void ConnectAsync()
        {
            PrivateKeyFile? preloadedKey = null;
            if (HostInfo.AuthMethod == SshAuthMethod.PrivateKey)
            {
                if (string.IsNullOrEmpty(HostInfo.SshKeyId))
                {
                    OnConnectFailed?.Invoke(new Exception("未配置 SSH 密钥，请在连接设置中选择一个已导入的密钥。"));
                    return;
                }
                var keyRepo = new KeyRepository();
                preloadedKey = keyRepo.LoadPrivateKeyFileAsync(HostInfo.SshKeyId).GetAwaiter().GetResult();
            }

            ConnectionStatus = "SSH.NET 建立连接...";

            new Thread(() =>
            {
                try
                {
                    var settings = _settingsRepo.Load();

                    MasterClient = BuildSshClient(preloadedKey);
                    MasterClient.Connect();

                    TerminalConnection = new SshTerminalConnection(MasterClient!, 120, 30);
                    TerminalConnection.InjectChineseLocale = settings.InjectChineseLocale;
                    TerminalConnection.AppCursorModeChanged += (isApp) =>
                    {
                        IsAppCursorMode = isApp;
                    };

                    // 先在后台线程建好 ShellStream，UI 线程设 Connection 时不会卡
                    TerminalConnection.Start();

                    IsConnected = true;
                    Application.Current?.Dispatcher.BeginInvoke(() => OnConnected?.Invoke());

                    // SFTP
                    try
                    {
                        ConnectionStatus = "SFTP 建立连接...";
                        var sftp = BuildSftpClient(preloadedKey);
                        sftp.Connect();
                        SftpClient = sftp;
                        IsSftpConnected = true;
                        _fileService = new RemoteFileService(SftpClient, _sftpLock, SessionId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("SFTP Connection Failed: " + ex.Message);
                    }

                    // Monitor
                    try
                    {
                        if (MasterClient != null && SftpClient != null)
                        {
                            _monitorService = new SshMonitorService(MasterClient, SftpClient, HostInfo, SessionId, _settingsRepo, _sftpLock, Monitor);
                            _monitorService.MonitorUpdated += (s, e) => MonitorUpdated?.Invoke(this, e);
                            _monitorService.ConnectionStatusCallback = (status) => ConnectionStatus = status;
                            _monitorService.RegisterTunnelCallback = RegisterTunnel;
                            _monitorService.StartAsync().GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Monitor Init Failed: " + ex.Message);
                    }

                    ConnectionStatus = "已连接";
                }
                catch (Exception ex)
                {
                    ConnectionStatus = "连接失败: " + ex.Message;
                    Application.Current?.Dispatcher.BeginInvoke(() => OnConnectFailed?.Invoke(ex));
                }
            })
            { IsBackground = true }.Start();
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
            IsConnected = false;
            IsSftpConnected = false;

            // 清掉回调，断开 session → terminalPage 的引用链
            OnConnected = null;
            OnConnectFailed = null;

            new Thread(() =>
            {
                try { TerminalConnection?.Close(); } catch { }
                TerminalConnection = null;

                try { _monitorService?.Stop(); } catch { }
                _monitorService = null;

                try { _fileService?.Dispose(); } catch { }
                _fileService = null;

                try
                {
                    string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", SessionId);
                    if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
                }
                catch { }

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
                }
                catch { }

                try
                {
                    lock (_sftpLock)
                    {
                        SftpClient?.Disconnect();
                        SftpClient?.Dispose();
                        SftpClient = null;
                    }
                }
                catch { }

                try
                {
                    MasterClient?.Disconnect();
                    MasterClient?.Dispose();
                }
                catch { }
            })
            { IsBackground = true }.Start();
        }

        public void Dispose() => Disconnect();
    }
}
