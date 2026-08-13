using System;

namespace YouShell.Terminal
{
    /// <summary>终端后端输出事件参数。</summary>
    public class TerminalOutputEventArgs : EventArgs
    {
        public TerminalOutputEventArgs(string data)
        {
            Data = data;
        }

        /// <summary>后端发送到终端的数据。</summary>
        public string Data { get; }
    }
}
