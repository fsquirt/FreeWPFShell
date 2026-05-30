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

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        /// <summary>
        /// 当检测到 Application Cursor Mode 切换时触发。
        /// true = Application Mode, false = Normal Mode.
        /// </summary>
        public event Action<bool>? AppCursorModeChanged;

        /// <summary>
        /// 当连接中途断开时触发
        /// </summary>
        public event EventHandler? ConnectionLost;

        public bool IsConnected => _client?.IsConnected ?? false;

        public bool InjectChineseLocale { get; set; } = true;

        /// <summary>
        /// 使用已连接 of SshClient 创建终端连接（不拥有 client 生命周期）。
        /// </summary>
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

            // xterm-256color 是支持真彩色终端的标准 TERM 值
            // 真彩色由应用通过 COLORTERM=truecolor 协商，TERM 不需要改
            var termModes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>();
            _shellStream = _client.CreateShellStream(
                "xterm-256color",
                _columns, _rows,
                _columns * 8, _rows * 16,  // 像素尺寸估算
                65536,                       // 增大缓冲区
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
            await Task.Delay(100); // 等待 shell 准备就绪
            // 自动检测服务器上实际存在的 UTF-8 locale，优先中文 → en_US → 任意 UTF-8
            // 避免硬编码 zh_CN.UTF-8（服务器可能没装，导致 setlocale 失败 → ls 中文文件名显示为转义序列）
            WriteInput("_l=$(locale -a 2>/dev/null | grep -im1 'zh_cn\\.utf-\\?8'); [ -z \"$_l\" ] && _l=$(locale -a 2>/dev/null | grep -im1 'en_us\\.utf-\\?8'); [ -z \"$_l\" ] && _l=$(locale -a 2>/dev/null | grep -im1 '\\.utf-\\?8'); [ -n \"$_l\" ] && { export LANG=$_l; export LC_ALL=$_l; }; unset _l\n");
        }

        private async Task ReadOutputAsync(CancellationToken token)
        {
            var buffer = new byte[8192];
            // 用于检测 AppCursorMode 切换的 VT 序列
            // \x1b[?1h = 启用 Application Cursor Keys (DECCKM)
            // \x1b[?1l = 禁用 Application Cursor Keys
            var scanBuffer = new StringBuilder(256);

            try
            {
                while (!token.IsCancellationRequested && _shellStream != null && _shellStream.CanRead)
                {
                    int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead > 0)
                    {
                        string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        // 检测 Application Cursor Mode 切换
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
            // 快速路径：大部分数据不包含 ESC 序列
            if (!data.Contains('\x1b')) return;

            if (data.Contains("\x1b[?1h"))
                AppCursorModeChanged?.Invoke(true);
            if (data.Contains("\x1b[?1l"))
                AppCursorModeChanged?.Invoke(false);
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
            // SSH.NET 2025.1.0 支持 ChangeWindowSize
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
