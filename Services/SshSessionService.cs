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
using FreeWPFShell.Models.Dto;
using FreeWPFShell.Repositories;
using FreeWPFShell.Services.Abstractions;
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

        private readonly SettingsRepository _settingsRepo;
        private readonly object _sftpLock = new();

        // SSH 隧道代理（跳板机）
        private SshClient? _jumpClient;
        private ForwardedPortLocal? _jumpPort;
        public object SftpLock => _sftpLock;

        // 会话级隧道管理（单一职责）
        private ITunnelService? _tunnelService;

        // SSH/SFTP 客户端工厂（连接参数构建）
        private readonly IConnectionFactory _connectionFactory;

        private RemoteFileService? _fileService;
        private SshMonitorService? _monitorService;

        // SFTP 看门狗：SFTP 连接独立于主 SSH 且可能被服务器/NAT 掐断，断开后自动重连
        private System.Timers.Timer? _sftpWatchdog;
        // 0=空闲 1=重连中（Interlocked 防止并发重连循环）
        private int _sftpReconnectState = 0;
        private const int SftpReconnectMaxAttempts = 5;
        private const int SftpReconnectIntervalMs = 3000;
        private const int SftpReconnectCooldownMs = 30_000;

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
            _tunnelService = new TunnelService(hostInfo.Id, hostInfo.HostName ?? hostInfo.IpAddress);
            _connectionFactory = new ConnectionFactory();
        }

        public async Task EditRemoteFileAsync(string remotePath, string editorCommand)
        {
            if (_fileService != null)
                await _fileService.EditRemoteFileAsync(remotePath, editorCommand);
        }

        public Action? OnConnected { get; set; }
        public Action<Exception>? OnConnectFailed { get; set; }

        /// <summary>加载预载私钥（密钥认证时）。连接与 SFTP 重连共用。</summary>
        private PrivateKeyFile? LoadPreloadedKey()
        {
            if (HostInfo.AuthMethod != SshAuthMethod.PrivateKey) return null;
            var keyRepo = new KeyRepository();
            return keyRepo.LoadPrivateKeyFileAsync(HostInfo.SshKeyId).GetAwaiter().GetResult();
        }

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
                preloadedKey = LoadPreloadedKey();
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
                        StartSftpWatchdog();
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

        private SshClient BuildSshClient(PrivateKeyFile? preloadedKey = null)
            => _connectionFactory.BuildSshClient(HostInfo, preloadedKey, _jumpPort);

        private SftpClient BuildSftpClient(PrivateKeyFile? preloadedKey = null)
            => _connectionFactory.BuildSftpClient(HostInfo, preloadedKey, _jumpPort);

        #region SFTP 自动重连看门狗

        private void StartSftpWatchdog()
        {
            if (_sftpWatchdog != null) return;
            _sftpWatchdog = new System.Timers.Timer(2000) { AutoReset = true, Enabled = true };
            _sftpWatchdog.Elapsed += OnSftpWatchdogTick;
        }

        /// <summary>
        /// 每 2 秒检测 SFTP 连接健康状态。检测到断开且主 SSH 仍存活时，
        /// 启动一次后台重连循环（最多 5 次、间隔 3s；全部失败冷却 30s 后由看门狗再次触发）。
        /// 主 SSH 已断时重连无意义（隧道也断了），交给上层会话清理与探针自退兜底。
        /// </summary>
        private void OnSftpWatchdogTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!IsConnected || SftpClient == null || SftpClient.IsConnected) return;
            if (MasterClient == null || !MasterClient.IsConnected) return;
            if (Interlocked.CompareExchange(ref _sftpReconnectState, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try
                {
                    for (int attempt = 1; attempt <= SftpReconnectMaxAttempts; attempt++)
                    {
                        if (!IsConnected || MasterClient == null || !MasterClient.IsConnected) return;

                        ConnectionStatus = $"SFTP 重连中(第{attempt}次)...";
                        try
                        {
                            var fresh = BuildSftpClient(LoadPreloadedKey());
                            fresh.Connect();
                            SwapSftpClient(fresh);
                            IsSftpConnected = true;
                            ConnectionStatus = "已连接";
                            return;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SFTP Reconnect] 第{attempt}次失败: {ex.Message}");
                        }

                        Thread.Sleep(SftpReconnectIntervalMs);
                    }

                    // 全部失败：标记断开并冷却，冷却结束后看门狗会再次触发重连循环
                    IsSftpConnected = false;
                    ConnectionStatus = "SFTP 重连失败，等待自动重试...";
                    Thread.Sleep(SftpReconnectCooldownMs);
                }
                finally
                {
                    Interlocked.Exchange(ref _sftpReconnectState, 0);
                }
            });
        }

        /// <summary>在 _sftpLock 内原子替换 SFTP 客户端（新 client 已连接），旧 client 在锁外释放。</summary>
        private void SwapSftpClient(SftpClient fresh)
        {
            SftpClient old;
            lock (_sftpLock)
            {
                old = SftpClient;
                SftpClient = fresh;
            }
            // 文件服务换绑新 client（保留已打开的编辑器 watcher）
            _fileService?.UpdateClient(fresh);
            if (old != null)
            {
                try { old.Disconnect(); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        #endregion

        private SshClient BuildJumpClient(PrivateKeyFile? jumpKey = null)
            => _connectionFactory.BuildJumpClient(HostInfo, jumpKey);

        public void RegisterTunnel(SshTunnelInfo tunnel)
            => _tunnelService?.RegisterTunnel(tunnel);

        /// <summary>
        /// 幂等清理本会话创建的所有 SSH 隧道（Stop 端口并从全局隧道表移除）。
        /// 在连接意外断开、主动断开或会话结束时都会调用，确保不会残留隧道。
        /// 委托给独立的 TunnelService 处理，避免上帝对象内维护大量隧道状态。
        /// </summary>
        public void CleanupTunnels()
            => _tunnelService?.CleanupTunnels();

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

                // 停止 SFTP 看门狗（会话已主动关闭，不再重连）
                try { _sftpWatchdog?.Stop(); _sftpWatchdog?.Dispose(); } catch { }
                _sftpWatchdog = null;

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
                try { _tunnelService?.Dispose(); } catch { }

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
