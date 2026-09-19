using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FreeWPFShell.Services;

namespace FreeWPFShell.Views
{
    public partial class DebugConsolePage : UserControl, IDisposable
    {
        private const int MaxLines = 5000;
        private const int KeepLines = 2500;

        private readonly object _lock = new();
        private readonly StringBuilder _pending = new();
        private int _flushPending;
        private int _lineCount;
        private bool _disposed;

        public DebugConsolePage()
        {
            InitializeComponent();

            OutputBox.Text = DebugConsoleService.Instance.Snapshot();
            _lineCount = CountLines(OutputBox.Text);
            OutputBox.ScrollToEnd();

            DebugConsoleService.Instance.Output += OnOutput;
        }

        private void BtnGc_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(() =>
            {
                long before = GC.GetTotalMemory(false);
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                var sw = Stopwatch.StartNew();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                sw.Stop();
                long after = GC.GetTotalMemory(false);

                DebugConsoleService.Log($"[GC] 托管堆 {FormatBytes(before)} → {FormatBytes(after)}，释放 {FormatBytes(Math.Max(0, before - after))}，耗时 {sw.ElapsedMilliseconds}ms，各代回收 {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2}");
            });
        }

        private void BtnTerminalForm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new FreeWPFShell.View.TerminalForm();
                window.Show();
                DebugConsoleService.Log("[Debug] 已打开 TerminalForm 窗口");
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Debug] 打开 TerminalForm 失败: " + ex.Message);
            }
        }

        private void BtnTerminalWithOther_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new FreeWPFShell.DebugForm.TerminalWithOther();
                window.Show();
                DebugConsoleService.Log("[Debug] 已打开 TerminalWithOther 窗口");
            }
            catch (Exception ex)
            {
                DebugConsoleService.Log("[Debug] 打开 TerminalWithOther 失败: " + ex.Message);
            }
        }

        private static string FormatBytes(long bytes)
            => bytes >= 1073741824L
                ? (bytes / 1073741824.0).ToString("F2") + " GB"
                : (bytes / 1048576.0).ToString("F2") + " MB";

        private void OnOutput(string text)
        {
            lock (_lock) _pending.Append(text);

            if (Interlocked.CompareExchange(ref _flushPending, 1, 0) != 0 || _disposed) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
        }

        private void Flush()
        {
            Interlocked.Exchange(ref _flushPending, 0);
            if (_disposed) return;

            string text;
            lock (_lock)
            {
                if (_pending.Length == 0) return;
                text = _pending.ToString();
                _pending.Clear();
            }

            OutputBox.AppendText(text);
            _lineCount += CountLines(text);

            if (_lineCount > MaxLines * 2) Trim();

            OutputBox.ScrollToEnd();
        }

        private void Trim()
        {
            string text = OutputBox.Text;
            int cut = LineStartIndex(text, _lineCount - KeepLines);
            if (cut <= 0) return;

            OutputBox.Text = text.Substring(cut);
            _lineCount = KeepLines;
        }

        private static int CountLines(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            return count;
        }

        private static int LineStartIndex(string text, int skipLines)
        {
            int index = 0;
            for (int i = 0; i < skipLines; i++)
            {
                int next = text.IndexOf('\n', index);
                if (next < 0) return -1;
                index = next + 1;
            }
            return index;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DebugConsoleService.Instance.Output -= OnOutput;
        }
    }
}
