using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeWPFShell.Models
{
    public enum SftpTransferKind { Upload, Download }

    public enum SftpTransferState { Waiting, Running, Paused, Completed, Cancelled, Failed }

    public sealed class SftpTransferTask : ObservableObject
    {
        private const long PushIntervalMs = 300;
        private const long SpeedSampleMs = 400;

        private readonly object _sync = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _transferred;
        private long _speedSampleBytes;
        private long _speedSampleMs;
        private long _lastPushMs;
        private double _speedBps;
        private int _state = (int)SftpTransferState.Waiting;

        private int _artifactComplete;

        private double _progressPercent;
        private string _progressText = "0.0%";
        private string _speedText = "--";
        private bool _canPause;
        private bool _canResume;
        private bool _canCancel = true;

        public SftpTransferTask(SftpTransferKind kind, string fileName, string localPath, string remotePath,
            long totalBytes, CancellationToken batchToken)
        {
            Kind = kind;
            FileName = fileName;
            LocalPath = localPath;
            RemotePath = remotePath;
            TotalBytes = totalBytes;
            Gate = new ManualResetEventSlim(true);
            Cts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
        }

        public SftpTransferKind Kind { get; }
        public string FileName { get; }
        public string LocalPath { get; }
        public string RemotePath { get; }
        public long TotalBytes { get; }

        public ManualResetEventSlim Gate { get; }
        public CancellationTokenSource Cts { get; }

        public SftpTransferState State => (SftpTransferState)Volatile.Read(ref _state);

        public double ProgressPercent
        {
            get => _progressPercent;
            private set { if (_progressPercent != value) { _progressPercent = value; OnPropertyChanged(); } }
        }

        public string ProgressText
        {
            get => _progressText;
            private set { if (_progressText != value) { _progressText = value; OnPropertyChanged(); } }
        }

        public string SpeedText
        {
            get => _speedText;
            private set { if (_speedText != value) { _speedText = value; OnPropertyChanged(); } }
        }

        public bool CanPause
        {
            get => _canPause;
            private set { if (_canPause != value) { _canPause = value; OnPropertyChanged(); } }
        }

        public bool CanResume
        {
            get => _canResume;
            private set { if (_canResume != value) { _canResume = value; OnPropertyChanged(); } }
        }

        public bool CanCancel
        {
            get => _canCancel;
            private set { if (_canCancel != value) { _canCancel = value; OnPropertyChanged(); } }
        }

        public void Start()
        {
            if (State != SftpTransferState.Waiting) return;
            long ms = _clock.ElapsedMilliseconds;
            lock (_sync)
            {
                _speedSampleBytes = Interlocked.Read(ref _transferred);
                _speedSampleMs = ms;
                _lastPushMs = 0;
                _speedBps = 0;
            }
            Volatile.Write(ref _state, (int)SftpTransferState.Running);
            Push();
        }

        public void ReportProgress(long transferred)
        {
            Interlocked.Exchange(ref _transferred, transferred);

            long ms = _clock.ElapsedMilliseconds;
            bool push;
            lock (_sync)
            {
                push = ms - _lastPushMs >= PushIntervalMs;
                if (push)
                {
                    long dt = ms - _speedSampleMs;
                    if (dt >= SpeedSampleMs)
                    {
                        _speedBps = (transferred - _speedSampleBytes) * 1000.0 / dt;
                        _speedSampleBytes = transferred;
                        _speedSampleMs = ms;
                    }
                    _lastPushMs = ms;
                }
            }
            if (push) Push();
        }

        public void Pause()
        {
            if (State != SftpTransferState.Running) return;
            Gate.Reset();
            lock (_sync) { _speedBps = 0; }
            Volatile.Write(ref _state, (int)SftpTransferState.Paused);
            Push();
        }

        public void Resume()
        {
            if (State != SftpTransferState.Paused) return;
            long ms = _clock.ElapsedMilliseconds;
            lock (_sync)
            {
                _speedSampleBytes = Interlocked.Read(ref _transferred);
                _speedSampleMs = ms;
                _lastPushMs = 0;
                _speedBps = 0;
            }
            Volatile.Write(ref _state, (int)SftpTransferState.Running);
            Gate.Set();
            Push();
        }

        public void Cancel()
        {
            var st = State;
            if (st != SftpTransferState.Waiting && st != SftpTransferState.Running && st != SftpTransferState.Paused) return;

            Volatile.Write(ref _state, (int)SftpTransferState.Cancelled);
            lock (_sync) { _speedBps = 0; }
            try { Cts.Cancel(); } catch { }
            Gate.Set();
            Push();
        }

        public bool ArtifactComplete => Volatile.Read(ref _artifactComplete) != 0;

        public void MarkArtifactComplete() => Volatile.Write(ref _artifactComplete, 1);

        public void WaitIfPaused()
        {
            Cts.Token.ThrowIfCancellationRequested();
            if (Gate.IsSet) return;
            Gate.Wait(Cts.Token);
            Cts.Token.ThrowIfCancellationRequested();
        }

        public void FinishCompleted() => Finish(SftpTransferState.Completed);
        public void FinishFailed() => Finish(SftpTransferState.Failed);
        public void FinishCancelled() => Finish(SftpTransferState.Cancelled);

        private void Finish(SftpTransferState state)
        {
            var cur = State;
            if (cur == SftpTransferState.Completed || cur == SftpTransferState.Cancelled || cur == SftpTransferState.Failed) return;
            if (state == SftpTransferState.Completed) Interlocked.Exchange(ref _transferred, TotalBytes);
            lock (_sync) { _speedBps = 0; }
            Volatile.Write(ref _state, (int)state);
            Gate.Set();
            Push();
        }

        private void Push()
        {
            var app = Application.Current;
            if (app == null) return;
            try
            {
                if (app.Dispatcher.CheckAccess()) ApplyUi();
                else app.Dispatcher.BeginInvoke((Action)ApplyUi);
            }
            catch { }
        }

        private void ApplyUi()
        {
            var st = State;
            long transferred = Interlocked.Read(ref _transferred);

            double pct = TotalBytes > 0
                ? transferred * 100.0 / TotalBytes
                : (st == SftpTransferState.Completed ? 100.0 : 0.0);
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;

            double bps;
            lock (_sync) { bps = _speedBps; }

            ProgressPercent = pct;
            ProgressText = pct.ToString("F1") + "%";
            SpeedText = st switch
            {
                SftpTransferState.Running => FormatSpeed(bps),
                SftpTransferState.Paused => "已暂停",
                SftpTransferState.Completed => "已完成",
                SftpTransferState.Cancelled => "已取消",
                SftpTransferState.Failed => "失败",
                _ => "--"
            };
            CanPause = st == SftpTransferState.Running;
            CanResume = st == SftpTransferState.Paused;
            CanCancel = st == SftpTransferState.Waiting || st == SftpTransferState.Running || st == SftpTransferState.Paused;
        }

        private static readonly string[] s_units = { "B", "KB", "MB", "GB", "TB" };

        private static string FormatSpeed(double bps)
        {
            if (double.IsNaN(bps) || bps < 1) return "0 B/s";
            int i = 0;
            double d = bps;
            while (d >= 1024 && i < s_units.Length - 1) { d /= 1024; i++; }
            return string.Format("{0:0.##} {1}/s", d, s_units[i]);
        }
    }
}
