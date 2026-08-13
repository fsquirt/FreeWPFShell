using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YouShell.Models;
using YouShell.Repositories;
using YouShell.Share;

namespace YouShell.ViewModels
{
    /// <summary>
    /// 首页（主机列表）ViewModel。管理已保存的主机列表、加载刷新与 IP 地理编码，
    /// 以及"新建/编辑/删除/连接/设置/密钥管理"等操作命令。
    /// 窗口交互（对话框、连接会话）通过注入的回调委托完成，保持 VM 可测试。
    /// </summary>
    public partial class WelcomePageViewModel : ObservableObject
    {
        private readonly HostRepository _hostRepo;

        public ObservableCollection<SshConnectionInfo> Hosts { get; } = new();

        [ObservableProperty]
        private SshConnectionInfo? _selectedHost;

        // 由 View 注入的 UI 交互回调
        public Action<SshConnectionInfo>? ConnectRequested { get; set; }
        public Action<SshConnectionInfo>? EditRequested { get; set; }
        public Func<SshConnectionInfo, Task<bool>>? DeleteConfirm { get; set; }
        public Action? AddConnectionRequested { get; set; }
        public Action? OpenSettingsRequested { get; set; }
        public Action? OpenKeyManagerRequested { get; set; }

        public WelcomePageViewModel(HostRepository? hostRepo = null)
        {
            _hostRepo = hostRepo ?? new HostRepository(new SettingsRepository());
            LoadHosts();
        }

        /// <summary>加载主机列表，并异步填充 IP 地理编码。</summary>
        public async void LoadHosts()
        {
            try
            {
                _hostRepo.Reload();
                var hosts = _hostRepo.GetAll();

                Hosts.Clear();
                foreach (var h in hosts) Hosts.Add(h);

                // 异步获取地理位置，不阻塞 UI 渲染
                await Task.Run(() =>
                {
                    foreach (var host in hosts)
                    {
                        try
                        {
                            var geo = IpGeoService.Instance.Query(host.IpAddress);
                            host.SimpleIpGEO = geo.SimpleGeo;
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadHosts Error: " + ex.Message);
            }
        }

        /// <summary>解密并返回指定主机的连接信息（含 SSH 密码/密钥）。</summary>
        public Task<SshConnectionInfo> GetAndDecryptAsync(string id)
            => _hostRepo.GetAndDecryptAsync(id);

        [RelayCommand]
        private void Connect(SshConnectionInfo? host)
        {
            if (host != null) ConnectRequested?.Invoke(host);
        }

        [RelayCommand]
        private void AddConnection() => AddConnectionRequested?.Invoke();

        [RelayCommand]
        private void Edit(SshConnectionInfo? host)
        {
            if (host != null) EditRequested?.Invoke(host);
        }

        [RelayCommand]
        private async Task Delete(SshConnectionInfo? host)
        {
            if (host == null) return;
            if (DeleteConfirm == null || !await DeleteConfirm(host)) return;
            _hostRepo.Delete(host.Id);
            Hosts.Remove(host);
            if (ReferenceEquals(SelectedHost, host)) SelectedHost = null;
        }

        [RelayCommand]
        private void OpenSettings() => OpenSettingsRequested?.Invoke();

        [RelayCommand]
        private void OpenKeyManager() => OpenKeyManagerRequested?.Invoke();
    }
}
