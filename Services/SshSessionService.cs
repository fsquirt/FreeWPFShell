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
        private readonly object _tunnelLock = new();
        private bool _tunnelCleanupDone;
        private readonly SettingsRepository _settingsRepo;
        private readonly object _sftpLock = new();

        // SSH 隧道代理（跳板机）
        private SshClient? _jumpClient;
        private ForwardedPortLocal? _jumpPort;
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

            // SSH 隧道代理：预加载跳板机密钥
            PrivateKeyFile? jumpKey = null;
            if (HostInfo.UseProxy && HostInfo.Proxy?.Type == ProxyType.Ssh && !string.IsNullOrEmpty(HostInfo.Proxy.SshKeyId))
            {
                var keyRepo = new KeyRepository();
                jumpKey = keyRepo.LoadPrivateKeyFileAsync(HostInfo.Proxy.SshKeyId).GetAwaiter().GetResult();
            }

            ConnectionStatus = "SSH.NET 建立连接...";

            new Thread(() =>
            {
                try
                {
                    var settings = _settingsRepo.Load();

                    // SSH 隧道代理：先连接跳板机，建立端口转发
                    if (HostInfo.UseProxy && HostInfo.Proxy?.Type == ProxyType.Ssh)
                    {
                        ConnectionStatus = "连接跳板机...";
                        _jumpClient = BuildJumpClient(jumpKey);
                        _jumpClient.Connect();

                        ConnectionStatus = "建立SSH隧道...";
                        uint localPort = (uint)Random.Shared.Next(40000, 60000);
                        _jumpPort = new ForwardedPortLocal("127.0.0.1", localPort, HostInfo.IpAddress, (uint)HostInfo.SshPort);
                        _jumpClient.AddForwardedPort(_jumpPort);
                        _jumpPort.Start();

                        // 注册隧道到管理器
                        var tunnelInfo = new SshTunnelInfo
                        {
                            Id = $"Jump_{SessionId}",
                            HostId = HostInfo.Id,
                            HostName = HostInfo.HostName ?? HostInfo.IpAddress,
                            Type = "本地(跳板机)",
                            BindAddress = "127.0.0.1",
                            BindPort = localPort,
                            DestAddress = HostInfo.IpAddress,
                            DestPort = (uint)HostInfo.SshPort,
                            Remark = $"跳板机 {HostInfo.Proxy.ServerAddress} → {HostInfo.IpAddress}:{HostInfo.SshPort}",
                            PortConfig = _jumpPort
                        };
                        RegisterTunnel(tunnelInfo);

                        // 通过转发端口连接目标主机（BuildConnectionInfo 会自动检测 _jumpPort 并使用转发端口）
                        MasterClient = BuildSshClient(preloadedKey);
                    }
                    else
                    {
                        MasterClient = BuildSshClient(preloadedKey);
                    }

                    MasterClient.Connect();

                    TerminalConnection = new SshTerminalConnection(MasterClient!, 120, 30);
                    TerminalConnection.InjectChineseLocale = settings.InjectChineseLocale;
                    TerminalConnection.AppCursorModeChanged += (isApp) =>
                    {
                        IsAppCursorMode = isApp;
                    };
                    // 订阅终端断连：连接意外断开时自动清理本会话隧道
                    TerminalConnection.ConnectionLost += OnTerminalConnectionLost;

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
                            // 监控轮询检测到连接断开时，自动清理隧道（兜底信号，覆盖终端流未及时返回 0 的场景）
                            _monitorService.ConnectionLostCallback = CleanupTunnels;
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

            // SSH 隧道代理：通过本地转发端口连接，不使用 SSH.NET 的 Proxy 机制
            if (HostInfo.UseProxy && HostInfo.Proxy?.Type == ProxyType.Ssh && _jumpPort != null)
            {
                var conn = new ConnectionInfo(
                    "127.0.0.1", (int)_jumpPort.BoundPort, HostInfo.SshUser,
                    authMethods.ToArray());
                conn.Encoding = Encoding.UTF8;
                conn.Timeout = TimeSpan.FromSeconds(30);
                return conn;
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

            var defaultConn = new ConnectionInfo(
                HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser,
                authMethods.ToArray());
            defaultConn.Encoding = Encoding.UTF8;
            return defaultConn;
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

        private SshClient BuildJumpClient(PrivateKeyFile? jumpKey = null)
        {
            if (HostInfo.Proxy == null || HostInfo.Proxy.Type != ProxyType.Ssh)
                throw new Exception("跳板机配置无效。");

            var authMethods = new List<AuthenticationMethod>(1);
            if (jumpKey != null)
                authMethods.Add(new PrivateKeyAuthenticationMethod(HostInfo.Proxy.Username, jumpKey));
            else
                authMethods.Add(new PasswordAuthenticationMethod(HostInfo.Proxy.Username, HostInfo.Proxy.Password));

            var connInfo = new ConnectionInfo(
                HostInfo.Proxy.ServerAddress, HostInfo.Proxy.Port, HostInfo.Proxy.Username,
                authMethods.ToArray());
            connInfo.Encoding = Encoding.UTF8;
            connInfo.Timeout = TimeSpan.FromSeconds(15);

            var client = new SshClient(connInfo);
            client.ErrorOccurred += (sender, e) =>
            {
                try { Debug.WriteLine($"[JumpClient Error] {e.Exception?.Message}"); } catch { }
            };
            return client;
        }

        public void RegisterTunnel(SshTunnelInfo tunnel)
        {
            lock (_tunnelLock) { _associatedTunnels.Add(tunnel); }
            SshTunnelManager.Instance.RegisterTunnel(tunnel);
        }

        /// <summary>
        /// 幂等清理本会话创建的所有 SSH 隧道（Stop 端口并从全局隧道表移除）。
        /// 在连接意外断开、主动断开或会话结束时都会调用，确保不会残留隧道。
        /// </summary>
        public void CleanupTunnels()
        {
            // 防重入：避免 Disconnected 事件、Disconnect()、CloseTab 等多条路径并发重复清理
            bool needClean;
            lock (_tunnelLock)
            {
                if (_tunnelCleanupDone) return;
                _tunnelCleanupDone = true;
                needClean = _associatedTunnels.Count > 0;
            }
            if (!needClean) return;

            try
            {
                lock (_tunnelLock)
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
        }

        /// <summary>
        /// 终端连接断连回调：当 SSH 连接意外断开（网络中断/服务器关闭）时，
        /// 由 SshTerminalConnection 触发，立即清理本会话所有隧道，
        /// 不依赖 UI 弹窗确认，避免残留隧道占用端口或导致后续连接异常。
        /// </summary>
        private void OnTerminalConnectionLost(object? sender, EventArgs e)
        {
            CleanupTunnels();
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

                // 清理跳板机资源
                try { _jumpPort?.Stop(); } catch { }
                _jumpPort = null;
                try { _jumpClient?.Disconnect(); _jumpClient?.Dispose(); } catch { }
                _jumpClient = null;

                try { _monitorService?.Stop(); } catch { }
                _monitorService = null;

                try { _fileService?.Dispose(); } catch { }
                _fileService = null;

                // 清理本会话创建的所有隧道（含跳板机、监控、手动创建的），幂等
                try
                {
                    if (TerminalConnection != null)
                    {
                        TerminalConnection.ConnectionLost -= OnTerminalConnectionLost;
                    }
                }
                catch { }
                CleanupTunnels();

                try
                {
                    string localDir = Path.Combine(Path.GetTempPath(), "FreeWPFShell", SessionId);
                    if (Directory.Exists(localDir)) Directory.Delete(localDir, true);
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
