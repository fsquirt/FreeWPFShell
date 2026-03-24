using Microsoft.Terminal.Wpf;
using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeWPFShell
{
    public class SshTerminalConnection : ITerminalConnection, IDisposable
    {
        private SshClient _client;
        private ShellStream _shellStream;
        private CancellationTokenSource _cts;

        public event EventHandler<TerminalOutputEventArgs> TerminalOutput;

        public bool IsConnected => _client?.IsConnected ?? false;
        
        // Expose the raw SshClient so you can use it for SFTP and PortForwarding outside of the terminal
        public SshClient Client => _client;

        public SshTerminalConnection(string host, int port, string username, string password)
        {
            _client = new SshClient(host, port, username, password);
        }

        public SshTerminalConnection(SshClient existingClient)
        {
            _client = existingClient;
        }

        public void Start()
        {
            if (!_client.IsConnected)
            {
                _client.Connect();
            }

            // Create the shell stream with xterm as terminal type
            _shellStream = _client.CreateShellStream("xterm", 120, 30, 800, 600, 1024);

            _cts = new CancellationTokenSource();

            // Start a background task to read output from the SSH server
            Task.Run(() => ReadOutputAsync(_cts.Token));
        }

        private async Task ReadOutputAsync(CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested && _shellStream != null && _shellStream.CanRead)
                {
                    int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead > 0)
                    {
                        string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
                    }
                    else
                    {
                        break; // Connection closed
                    }
                }
            }
            catch (Exception)
            {
                // The stream gets closed, ignore the error and cleanly exit
            }
        }

        public void WriteInput(string data)
        {
            if (_shellStream != null && _shellStream.CanWrite)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                _shellStream.Write(bytes, 0, bytes.Length);
                _shellStream.Flush();
            }
        }

        public void Resize(uint rows, uint columns)
        {
            // SSH.NET's ShellStream might not expose SendWindowChange in all versions. 
            // Often you just set the initial terminal size when calling CreateShellStream.
            // If needed, you might have to reflect or use a custom channel.
        }

        public void Close()
        {
            _cts?.Cancel();
            _shellStream?.Dispose();
            _client?.Disconnect();
            _client?.Dispose();
        }

        public void Dispose()
        {
            Close();
        }
    }
}
