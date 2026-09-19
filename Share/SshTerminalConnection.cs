using Microsoft.Terminal.Wpf;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeWPFShell
{
    public class SshTerminalConnection : ITerminalConnection, IDisposable
    {
        private readonly SshClient _client;
        private readonly bool _ownsClient;
        private ShellStream? _shellStream;
        private CancellationTokenSource? _cts;
        private uint _columns;
        private uint _rows;
        private bool _connectionLostTriggered = false;
        private bool _started;

        private const int CursorScanTailLength = 4;
        private string _cursorScanTail = "";
        private bool? _lastAppCursorMode;

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;


        public event Action<bool>? AppCursorModeChanged;


        public event EventHandler? ConnectionLost;

        public bool IsConnected => _client?.IsConnected ?? false;

        public bool InjectChineseLocale { get; set; } = true;


        public SshTerminalConnection(SshClient existingClient, uint initialColumns = 120, uint initialRows = 30)
        {
            _client = existingClient;
            _ownsClient = false;
            _columns = initialColumns;
            _rows = initialRows;
        }

        private void RaiseConnectionLost()
        {
            if (_connectionLostTriggered) return;
            _connectionLostTriggered = true;
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            if (!_client.IsConnected)
                _client.Connect();


            var termModes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>();
            _shellStream = _client.CreateShellStream(
                "xterm-256color",
                _columns, _rows,
                _columns * 8, _rows * 16,  
                65536,                       
                termModes);

            if (InjectChineseLocale)
            {
                _ = InjectLocaleAsync();
            }

            _cts = new CancellationTokenSource();
            Task.Run(() => ReadOutputAsync(_cts.Token));
        }

        public async Task InjectLocaleAsync()
        {
            if (_shellStream == null) return;
            await Task.Delay(100); 

            WriteInput("_l=$(locale -a 2>/dev/null | grep -im1 'zh_cn\\.utf-\\?8'); [ -z \"$_l\" ] && _l=$(locale -a 2>/dev/null | grep -im1 'en_us\\.utf-\\?8'); [ -z \"$_l\" ] && _l=$(locale -a 2>/dev/null | grep -im1 '\\.utf-\\?8'); [ -n \"$_l\" ] && { export LANG=$_l; export LC_ALL=$_l; }; unset _l\n");
        }

        private async Task ReadOutputAsync(CancellationToken token)
        {
            var buffer = new byte[8192];

            try
            {
                while (!token.IsCancellationRequested && _shellStream != null && _shellStream.CanRead)
                {
                    int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead > 0)
                    {
                        string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);


                        DetectCursorMode(data);

                        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
                    }
                    else
                    {
                        if (!token.IsCancellationRequested)
                        {
                            RaiseConnectionLost();
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception)
            {
                if (!token.IsCancellationRequested)
                {
                    RaiseConnectionLost();
                }
            }
        }

        private void DetectCursorMode(string data)
        {
            string scan = _cursorScanTail + data;
            _cursorScanTail = scan.Length > CursorScanTailLength
                ? scan.Substring(scan.Length - CursorScanTailLength)
                : scan;

            if (scan.IndexOf('\x1b') < 0) return;

            const string appModeOn = "\x1b[?1h";
            const string appModeOff = "\x1b[?1l";

            int lastOn = scan.LastIndexOf(appModeOn, StringComparison.Ordinal);
            int lastOff = scan.LastIndexOf(appModeOff, StringComparison.Ordinal);
            if (lastOn < 0 && lastOff < 0) return;

            bool isApp = lastOn > lastOff;

            if (_lastAppCursorMode != isApp)
            {
                _lastAppCursorMode = isApp;
                AppCursorModeChanged?.Invoke(isApp);
            }
        }

        public void WriteInput(string data)
        {
            try
            {
                if (_shellStream != null && _shellStream.CanWrite)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(data);
                    _shellStream.Write(bytes, 0, bytes.Length);
                    _shellStream.Flush();
                }
                else
                {
                    RaiseConnectionLost();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SshTerminalConnection WriteInput Error] {ex.Message}");
                RaiseConnectionLost();
            }
        }

        public void Resize(uint rows, uint columns)
        {
            _columns = columns;
            _rows = rows;

            try
            {
                _shellStream?.ChangeWindowSize(columns, rows, columns * 8, rows * 16);
            }
            catch { }
        }

        public void Close()
        {
            _cts?.Cancel();
            _started = false;
            _connectionLostTriggered = false;
            _cursorScanTail = "";
            _lastAppCursorMode = null;
            try { _shellStream?.Dispose(); } catch { }
            _shellStream = null;

            if (_ownsClient)
            {
                try { _client?.Disconnect(); } catch { }
                try { _client?.Dispose(); } catch { }
            }
        }

        public void Dispose() => Close();
    }
}
