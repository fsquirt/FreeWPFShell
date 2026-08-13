using System;

namespace YouShell.Terminal
{
    /// <summary>
    /// 终端后端连接契约（SSH ShellStream、本地 ConPTY 等均实现此接口）。
    /// 与 Microsoft.Terminal.Wpf.ITerminalConnection 保持语义一致，但脱离 WPF 依赖。
    /// </summary>
    public interface ITerminalConnection
    {
        /// <summary>终端后端有新的输出数据时触发。</summary>
        event EventHandler<TerminalOutputEventArgs> TerminalOutput;

        /// <summary>通知后端终端已就绪，可开始收发数据。</summary>
        void Start();

        /// <summary>将用户输入写入后端。</summary>
        void WriteInput(string data);

        /// <summary>调整终端后端尺寸。</summary>
        void Resize(uint rows, uint columns);

        /// <summary>关闭终端后端。</summary>
        void Close();
    }
}
