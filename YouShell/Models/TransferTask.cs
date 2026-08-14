using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YouShell.Models
{
    public enum TransferDirection { Upload, Download }

    public enum TransferStatus { Running, Paused, Completed, Canceled, Failed }

    /// <summary>
    /// 单个传输任务（上传/下载）。展示在 SFTP 页「传输任务」列表，
    /// 支持暂停/继续/取消；进度与状态属性统一在 UI 线程通知。
    /// </summary>
    public partial class TransferTask : ObservableObject
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public TransferDirection Direction { get; set; }
        public string FileName { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public long TotalBytes { get; set; }
        public long TransferredBytes { get; set; }

        [ObservableProperty]
        private TransferStatus _status = TransferStatus.Running;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private double _speed;

        // 实时速度采样内部状态（非 UI 绑定，仅在传输线程读写）
        internal long _lastBytes;
        internal DateTime _lastSpeedAt = DateTime.MinValue;

        public string DirectionText => Direction == TransferDirection.Upload ? "上传" : "下载";

        public string StatusText => Status switch
        {
            TransferStatus.Running => "进行中",
            TransferStatus.Paused => "已暂停",
            TransferStatus.Completed => "已完成",
            TransferStatus.Canceled => "已取消",
            TransferStatus.Failed => "失败",
            _ => ""
        };

        public string CreatedText => CreatedAt.ToString("yyyy/MM/dd HH:mm:ss");

        public string ProgressText => $"{Progress:0}%";

        public string SpeedText => $"{FormatSpeed(Speed)}/s";

        private static string FormatSpeed(double bps)
        {
            if (bps >= 1024 * 1024) return $"{bps / 1024 / 1024:0.0} MB";
            if (bps >= 1024) return $"{bps / 1024:0.0} KB";
            return $"{bps:0} B";
        }

        public bool CanPause => Status == TransferStatus.Running;
        public bool CanResume => Status == TransferStatus.Paused;
        public bool CanCancel => Status is TransferStatus.Running or TransferStatus.Paused;

        // 内部传输控制
        internal CancellationTokenSource? Cts;

        partial void OnStatusChanged(TransferStatus value)
        {
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(StatusText));
        }

        partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));

        partial void OnSpeedChanged(double value) => OnPropertyChanged(nameof(SpeedText));
    }
}
