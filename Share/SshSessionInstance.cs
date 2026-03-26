using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace FreeWPFShell.Share
{
    public class SshSessionInstance : IDisposable
    {
        private static int _sessionCounter = 0;
        
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public int SessionIndex { get; }
        public SshManager.SshConnectionInfo HostInfo { get; }
        public string DisplayName { get; }
        
        // Network Clients
        public SshClient MasterClient { get; private set; }
        public SftpClient SftpClient { get; private set; }
        public ConPtyConnection TerminalConnection { get; private set; }
        
        // State
        public bool IsConnected { get; private set; }
        public uint LinuxMonitorLocalPort { get; private set; } = 0;
        
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public SshSessionInstance(SshManager.SshConnectionInfo hostInfo)
        {
            HostInfo = hostInfo;
            SessionIndex = Interlocked.Increment(ref _sessionCounter) - 1;
            string baseName = string.IsNullOrEmpty(hostInfo.HostName) ? hostInfo.IpAddress : hostInfo.HostName;
            DisplayName = $"{baseName} #{SessionIndex}";
        }

        public async Task ConnectAsync()
        {
            await Task.Run(() => 
            {
                // 1. Establish Master Client
                MasterClient = new SshClient(HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? "");
                MasterClient.Connect();

                if (_cts.IsCancellationRequested) return;

                // 2. Establish SFTP
                SftpClient = new SftpClient(HostInfo.IpAddress, HostInfo.SshPort, HostInfo.SshUser, HostInfo.DecryptedSshSecret ?? "");
                SftpClient.Connect();

                // 3. Deploy Linux Monitor
                DeployLinuxMonitor();
                
                IsConnected = true;
            }, _cts.Token);

            if (_cts.IsCancellationRequested) return;

            // 4. Mount Terminal Control PTY
            TerminalConnection = new ConPtyConnection($"ssh {HostInfo.SshUser}@{HostInfo.IpAddress} -p {HostInfo.SshPort}", 120, 40);
            
            // Auto login hook
            string outputBuffer = "";
            EventHandler<Microsoft.Terminal.Wpf.TerminalOutputEventArgs> onOutput = null;
            onOutput = (s, args) =>
            {
                if (args.Data != null)
                {
                    outputBuffer += args.Data;
                    if (outputBuffer.ToLower().Contains("password:"))
                    {
                        TerminalConnection.TerminalOutput -= onOutput;
                        Task.Delay(50).ContinueWith(_ => TerminalConnection.WriteInput($"{HostInfo.DecryptedSshSecret}\n"));
                    }
                    if (outputBuffer.Length > 2048) outputBuffer = ""; 
                }
            };
            TerminalConnection.TerminalOutput += onOutput;
        }

        private void DeployLinuxMonitor()
        {
            var sm = new SshManager.SshConnectionManager();
            if (!sm.Settings.UseLinuxMonitor) return;
            
            try
            {
                LinuxMonitorLocalPort = (uint)(new Random().Next(40000, 60000));
                
                var tunnelInfo = new SshTunnelInfo
                {
                    Id = $"Mon_{SessionId}",
                    HostId = HostInfo.Id,
                    HostName = HostInfo.HostName ?? HostInfo.IpAddress,
                    Type = "Local",
                    BindAddress = "127.0.0.1",
                    BindPort = LinuxMonitorLocalPort,
                    DestAddress = "127.0.0.1",
                    DestPort = LinuxMonitorLocalPort,
                    Remark = "自动创建 - Linux Monitor"
                };
                
                var port = new Renci.SshNet.ForwardedPortLocal("127.0.0.1", LinuxMonitorLocalPort, "127.0.0.1", LinuxMonitorLocalPort);
                MasterClient.AddForwardedPort(port);
                port.Start();
                tunnelInfo.PortConfig = port;
                
                SshTunnelManager.Instance.RegisterTunnel(tunnelInfo);

                string binPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "linux-monitor");
                
                if (System.IO.File.Exists(binPath))
                {
                    string uniqueRemotePath = $"/tmp/linux-monitor_{LinuxMonitorLocalPort}";
                    
                    var cmdKill = MasterClient.CreateCommand($"pkill -9 -f {uniqueRemotePath}");
                    cmdKill.Execute();

                    using (var fs = System.IO.File.OpenRead(binPath))
                    {
                        SftpClient.UploadFile(fs, uniqueRemotePath, true);
                    }

                    var cmd1 = MasterClient.CreateCommand($"chmod +x {uniqueRemotePath}");
                    cmd1.Execute();
                    
                    var cmd3 = MasterClient.CreateCommand($"nohup {uniqueRemotePath} {LinuxMonitorLocalPort} >/dev/null 2>&1 &");
                    cmd3.Execute();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Monitor Deploy Failed: " + ex.Message);
            }
        }

        public void Disconnect()
        {
            _cts.Cancel();
            
            if (LinuxMonitorLocalPort > 0)
            {
                SshTunnelManager.Instance.UnregisterTunnel($"Mon_{SessionId}");
                try {
                    var cmdKill = MasterClient.CreateCommand($"pkill -9 -f linux-monitor_{LinuxMonitorLocalPort}");
                    cmdKill.Execute(); 
                } catch {}
            }

            SftpClient?.Disconnect();
            SftpClient?.Dispose();
            MasterClient?.Disconnect();
            MasterClient?.Dispose();
            TerminalConnection?.Close();
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
