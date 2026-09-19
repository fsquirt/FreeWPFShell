using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FreeWPFShell.Services
{
    public sealed class DebugConsoleService
    {
        public static DebugConsoleService Instance { get; } = new();

        private const int MaxLines = 5000;

        [ThreadStatic]
        private static bool _reentrant;

        private readonly object _lock = new();
        private readonly Queue<string> _lines = new();
        private readonly StringBuilder _lineBuffer = new();
        private bool _installed;

        public event Action<string>? Output;

        private DebugConsoleService() { }

        public void Install()
        {
            lock (_lock)
            {
                if (_installed) return;
                _installed = true;
            }

            try { Console.SetOut(new TeeWriter(this, Console.Out)); } catch { }
            try { Console.SetError(new TeeWriter(this, Console.Error)); } catch { }
            try { Trace.Listeners.Add(new ForwardListener(this)); } catch { }
        }

        public static void Log(string message)
            => Instance.Write((message ?? string.Empty) + Environment.NewLine);

        public string Snapshot()
        {
            lock (_lock)
            {
                if (_lines.Count == 0) return string.Empty;

                var sb = new StringBuilder();
                foreach (string line in _lines) sb.Append(line).Append('\n');
                return sb.ToString();
            }
        }

        public void Write(string? text)
        {
            if (string.IsNullOrEmpty(text) || _reentrant) return;

            _reentrant = true;
            try
            {
                StringBuilder? chunk = null;

                lock (_lock)
                {
                    int start = 0;
                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (c != '\n' && c != '\r') continue;

                        if (i > start) _lineBuffer.Append(text, start, i - start);
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                        start = i + 1;

                        string line = _lineBuffer.ToString();
                        _lineBuffer.Clear();

                        string stamped = line.Length == 0 ? string.Empty : Stamp(line);
                        _lines.Enqueue(stamped);
                        while (_lines.Count > MaxLines) _lines.Dequeue();

                        chunk ??= new StringBuilder();
                        chunk.Append(stamped).Append('\n');
                    }

                    if (start < text.Length) _lineBuffer.Append(text, start, text.Length - start);
                }

                if (chunk != null) Output?.Invoke(chunk.ToString());
            }
            finally
            {
                _reentrant = false;
            }
        }

        private static string Stamp(string line) => DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;

        private sealed class TeeWriter : TextWriter
        {
            private readonly DebugConsoleService _owner;
            private readonly TextWriter _inner;

            public TeeWriter(DebugConsoleService owner, TextWriter inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public override Encoding Encoding => _inner.Encoding;

            public override void Write(char value)
            {
                _owner.Write(value.ToString());
                _inner.Write(value);
            }

            public override void Write(string? value)
            {
                _owner.Write(value);
                _inner.Write(value);
            }

            public override void WriteLine(string? value)
            {
                _owner.Write((value ?? string.Empty) + Environment.NewLine);
                _inner.WriteLine(value);
            }

            public override void WriteLine()
            {
                _owner.Write(Environment.NewLine);
                _inner.WriteLine();
            }

            public override void Flush() => _inner.Flush();
        }

        private sealed class ForwardListener : TraceListener
        {
            private readonly DebugConsoleService _owner;

            public ForwardListener(DebugConsoleService owner) => _owner = owner;

            public override void Write(string? message) => _owner.Write(message);

            public override void WriteLine(string? message) => _owner.Write((message ?? string.Empty) + Environment.NewLine);
        }
    }
}
