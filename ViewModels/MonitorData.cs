using System.Collections.Generic;
using FreeWPFShell.Models;
using FreeWPFShell.Share;

namespace FreeWPFShell.ViewModels
{
    public class MonitorData : ObservableObject
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
        private List<(double rx, double tx)> _netHistory = new();
        private List<ProcessItem> _processes = new();
        private List<DiskItem> _disks = new();

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
        public List<(double rx, double tx)> NetHistory { get => _netHistory; set { _netHistory = value; OnPropertyChanged(); } }
        public List<ProcessItem> Processes { get => _processes; set { _processes = value; OnPropertyChanged(); } }
        public List<DiskItem> Disks { get => _disks; set { _disks = value; OnPropertyChanged(); } }
    }
}
