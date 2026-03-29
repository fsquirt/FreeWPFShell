using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.UserForm;

namespace FreeWPFShell.Views
{
    public partial class ProcessPage : UserControl, IDisposable
    {
        private readonly SshSessionService _session;
        private readonly ObservableCollection<ProcessItem> _processes = new();
        private readonly System.Timers.Timer _refreshTimer;
        private List<ProcessItem> _allProcessesRaw = new();

        public ProcessPage(SshSessionService session)
        {
            InitializeComponent();
            _session = session;
            ProcessGrid.ItemsSource = _processes;

            _refreshTimer = new System.Timers.Timer(2000);
            _refreshTimer.Elapsed += async (s, e) => await RefreshData();
            _refreshTimer.AutoReset = true;
            _refreshTimer.Enabled = true;

            _ = RefreshData();
        }

        private async Task RefreshData()
        {
            _allProcessesRaw = await _session.GetAllProcessesAsync();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            Dispatcher.Invoke(() =>
            {
                string search = TxtSearch.Text.Trim().ToLower();
                bool filterEmpty = TogFilterEmpty.IsChecked == true;
                uint? selectedPid = (ProcessGrid.SelectedItem as ProcessItem)?.Pid;

                var filtered = _allProcessesRaw.Where(p =>
                {
                    if (filterEmpty && string.IsNullOrEmpty(p.File)) return false;
                    if (string.IsNullOrEmpty(search)) return true;
                    return p.Pid.ToString().Contains(search) ||
                           p.File.ToLower().Contains(search) ||
                           p.Cmd.ToLower().Contains(search);
                }).ToList();

                _processes.Clear();
                foreach (var p in filtered) _processes.Add(p);

                if (selectedPid.HasValue)
                {
                    var newSelected = _processes.FirstOrDefault(x => x.Pid == selectedPid.Value);
                    if (newSelected != null) ProcessGrid.SelectedItem = newSelected;
                }
            });
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
        private void TogFilterEmpty_Click(object sender, RoutedEventArgs e) => ApplyFilter();
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshData();

        private async void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p)
            {
                TxtDetail.Text = $"正在获取进程 {p.Pid} 的详细信息...";
                var detail = await _session.GetProcessDetailAsync(p.Pid);
                if (detail != null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"PID (Process ID)      : {detail.pid}");
                    sb.AppendLine($"PPID (Parent PID)     : {detail.ppid}");
                    sb.AppendLine($"UID/GID (用户/组)     : {detail.uid_gid}");
                    sb.AppendLine($"进程状态              : {detail.status}");
                    sb.AppendLine($"优先级与 Nice 值      : {detail.priority_nice}");
                    sb.AppendLine($"CPU 占用时间          : {detail.cpu_time}");
                    sb.AppendLine($"文件描述符 (FD) 数量   : {detail.fd_count}");
                    sb.AppendLine($"内存信息 (statm)       : {detail.mem_info}");
                    sb.AppendLine($"资源限制 (ulimit)      : \n{detail.ulimit}");
                    sb.AppendLine($"CWD (当前工作目录)      : {detail.cwd}");
                    sb.AppendLine($"命令行参数 (argv)      : \n{detail.argv}");
                    sb.AppendLine($"TTY (终端关联)         : {detail.tty}");
                    sb.AppendLine($"\n--- 信号处理 (Status/Sig) ---");
                    sb.AppendLine(detail.signals);
                    sb.AppendLine($"\n--- 寄存器上下文 (Kernel Stack) ---");
                    sb.AppendLine(detail.context);
                    TxtDetail.Text = sb.ToString();
                }
                else TxtDetail.Text = "无法获取详细信息，该进程可能已经结束。";
            }
        }

        private async void CtxKill_Click(object sender, RoutedEventArgs e) => await DoKill(15);
        private async void CtxKillForce_Click(object sender, RoutedEventArgs e) => await DoKill(9);
        private async void CtxKillAll_Click(object sender, RoutedEventArgs e) => await DoKillAll(15);
        private async void CtxKillAllForce_Click(object sender, RoutedEventArgs e) => await DoKillAll(9);

        private async Task DoKill(int sig)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p)
            {
                if (ModernMessageBox.Show($"确定要对进程 {p.Pid} ({p.Cmd}) 发送信号 {sig} 吗？", "结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool success = await _session.KillProcessAsync(p.Pid, sig);
                    if (success) ModernMessageBox.Show("信号已发送。");
                    else ModernMessageBox.Show("操作失败，可能权限不足。");
                }
            }
        }

        private async Task DoKillAll(int sig)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p)
            {
                string procName = !string.IsNullOrEmpty(p.File) ? System.IO.Path.GetFileName(p.File) : p.Cmd.Split(' ')[0];
                if (string.IsNullOrEmpty(procName)) return;

                if (ModernMessageBox.Show($"确定要结束所有名为 \"{procName}\" 的进程吗？\n信号: {sig}", "全部结束", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    bool success = await _session.KillAllProcessesAsync(p.File, sig);
                    if (success) ModernMessageBox.Show("批量结束信号已发送。");
                    else ModernMessageBox.Show("操作可能部分失败或权限不足。");
                }
            }
        }

        public void Dispose()
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
        }

        private void CtxCopyPid_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessGrid.SelectedItem is ProcessItem p) Clipboard.SetText(p.Pid.ToString());
        }
    }
}
