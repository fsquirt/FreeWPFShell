using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using FreeWPFShell.Models;

namespace FreeWPFShell.Services
{
    public sealed class SftpTransferManager
    {
        private readonly object _batchLock = new();
        private readonly object _allLock = new();
        private readonly List<SftpTransferTask> _all = new();

        private CancellationTokenSource _batchCts = new();

        public ObservableCollection<SftpTransferTask> Tasks { get; } = new();

        public CancellationToken BatchToken
        {
            get { lock (_batchLock) return _batchCts.Token; }
        }

        public bool IsBatchCancelled
        {
            get { lock (_batchLock) return _batchCts.IsCancellationRequested; }
        }

        public void BeginBatch()
        {
            lock (_batchLock) { _batchCts = new CancellationTokenSource(); }
        }

        public SftpTransferTask Create(SftpTransferKind kind, string fileName, string localPath, string remotePath, long totalBytes)
        {
            var task = new SftpTransferTask(kind, fileName, localPath, remotePath, totalBytes, BatchToken);
            lock (_allLock) _all.Add(task);
            OnUi(() => Tasks.Add(task));
            return task;
        }

        public void PauseAll()
        {
            foreach (var t in Snapshot()) t.Pause();
        }

        public void ResumeAll()
        {
            foreach (var t in Snapshot()) t.Resume();
        }

        public void CancelAll()
        {
            lock (_batchLock) { try { _batchCts.Cancel(); } catch { } }
            foreach (var t in Snapshot()) t.Cancel();
        }

        public void Clear()
        {
            lock (_allLock) _all.Clear();
            OnUi(() => Tasks.Clear());
        }

        public void CancelAndClear()
        {
            CancelAll();
            Clear();
        }

        private SftpTransferTask[] Snapshot()
        {
            lock (_allLock) return _all.ToArray();
        }

        private static void OnUi(Action action)
        {
            var app = Application.Current;
            if (app == null) { action(); return; }
            try
            {
                if (app.Dispatcher.CheckAccess()) action();
                else app.Dispatcher.BeginInvoke(action);
            }
            catch { }
        }
    }
}
