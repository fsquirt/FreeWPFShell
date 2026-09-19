using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeWPFShell.Models
{
    public partial class MonitorData : ObservableObject
    {
        private double _cpuPct;
        private double _memPct;
        private string _memText = "0M/0M";
        private double _swapPct;
        private string _swapText = "0M/0M";
        private string _uptime = "运行 -- 天...";
        private string _load = "负载 --, --, --";
        private string _netUp = "0K/s";
        private string _netDown = "0K/s";
        private string _netIface = "--";
        private string _netMax = "100K";
        private string _netMid = "50K";
        private double _netRxSpeed;
        private double _netTxSpeed;
        private string _ping = "--ms";


        private readonly (double rx, double tx)[] _netHistory = new (double, double)[50];
        private int _netHistoryCount;

        private readonly List<ProcessItem> _processes = new();
        private readonly List<DiskItem> _disks = new();

        public double CpuPct { get => _cpuPct; set { _cpuPct = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuText)); } }
        public string CpuText => $"{_cpuPct:F1}%";
        public double MemPct { get => _memPct; set { _memPct = value; OnPropertyChanged(); } }
        public string MemText { get => _memText; set { _memText = value; OnPropertyChanged(); } }
        public double SwapPct { get => _swapPct; set { _swapPct = value; OnPropertyChanged(); } }
        public string SwapText { get => _swapText; set { _swapText = value; OnPropertyChanged(); } }
        public string Uptime { get => _uptime; set { _uptime = value; OnPropertyChanged(); } }
        public string Load { get => _load; set { _load = value; OnPropertyChanged(); } }
        public string NetUp { get => _netUp; set { _netUp = value; OnPropertyChanged(); } }
        public string NetDown { get => _netDown; set { _netDown = value; OnPropertyChanged(); } }
        public string NetIface { get => _netIface; set { _netIface = value; OnPropertyChanged(); } }
        public string NetMax { get => _netMax; set { _netMax = value; OnPropertyChanged(); } }
        public string NetMid { get => _netMid; set { _netMid = value; OnPropertyChanged(); } }
        public double NetRxSpeed { get => _netRxSpeed; set { _netRxSpeed = value; OnPropertyChanged(); } }
        public double NetTxSpeed { get => _netTxSpeed; set { _netTxSpeed = value; OnPropertyChanged(); } }
        public string Ping { get => _ping; set { _ping = value; OnPropertyChanged(); } }


        public IReadOnlyList<(double rx, double tx)> NetHistory => new ArraySegment<(double, double)>(_netHistory, 0, _netHistoryCount);
        public IReadOnlyList<ProcessItem> Processes => _processes;
        public IReadOnlyList<DiskItem> Disks => _disks;


        public void AddNetHistoryEntry(double rx, double tx)
        {
            if (_netHistoryCount < _netHistory.Length)
            {
                _netHistory[_netHistoryCount++] = (rx, tx);
            }
            else
            {

                Array.Copy(_netHistory, 1, _netHistory, 0, _netHistory.Length - 1);
                _netHistory[_netHistory.Length - 1] = (rx, tx);
            }
            OnPropertyChanged(nameof(NetHistory));
        }


        public double GetNetHistoryMax()
        {
            double max = 1024;
            for (int i = 0; i < _netHistoryCount; i++)
            {
                double v = Math.Max(_netHistory[i].rx, _netHistory[i].tx);
                if (v > max) max = v;
            }
            return max;
        }


        public void UpdateProcesses(System.Collections.Generic.IEnumerable<ProcessItem> items)
        {
            _processes.Clear();
            _processes.AddRange(items);
            OnPropertyChanged(nameof(Processes));
        }


        public void UpdateDisks(System.Collections.Generic.IEnumerable<DiskItem> items)
        {
            _disks.Clear();
            _disks.AddRange(items);
            OnPropertyChanged(nameof(Disks));
        }


        public void NotifyBulkRefresh()
        {
            OnPropertyChanged(nameof(CpuPct));
            OnPropertyChanged(nameof(CpuText));
            OnPropertyChanged(nameof(MemPct));
            OnPropertyChanged(nameof(MemText));
            OnPropertyChanged(nameof(SwapPct));
            OnPropertyChanged(nameof(SwapText));
            OnPropertyChanged(nameof(Uptime));
            OnPropertyChanged(nameof(Load));
            OnPropertyChanged(nameof(NetUp));
            OnPropertyChanged(nameof(NetDown));
            OnPropertyChanged(nameof(NetIface));
            OnPropertyChanged(nameof(NetMax));
            OnPropertyChanged(nameof(NetMid));
            OnPropertyChanged(nameof(NetRxSpeed));
            OnPropertyChanged(nameof(NetTxSpeed));
            OnPropertyChanged(nameof(Ping));
            OnPropertyChanged(nameof(Processes));
            OnPropertyChanged(nameof(Disks));
            OnPropertyChanged(nameof(NetHistory));
        }
    }
}
