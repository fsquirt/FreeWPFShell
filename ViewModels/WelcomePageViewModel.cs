using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Services;
using FreeWPFShell.Share;

namespace FreeWPFShell.ViewModels
{

    public partial class WelcomePageViewModel : ObservableObject
    {
        private readonly HostRepository _hostRepo;

        public ObservableCollection<SshConnectionInfo> Hosts { get; } = new();

        [ObservableProperty]
        private SshConnectionInfo? _selectedHost;


        public Action<SshConnectionInfo>? ConnectRequested { get; set; }
        public Action<SshConnectionInfo>? EditRequested { get; set; }
        public Func<SshConnectionInfo, bool>? DeleteConfirm { get; set; }
        public Action? AddConnectionRequested { get; set; }
        public Action? OpenSettingsRequested { get; set; }
        public Action? OpenKeyManagerRequested { get; set; }

        public WelcomePageViewModel(HostRepository? hostRepo = null)
        {
            _hostRepo = hostRepo ?? new HostRepository(new SettingsRepository());
            LoadHosts();
        }


        public async void LoadHosts()
        {
            try
            {
                _hostRepo.Reload();
                var hosts = _hostRepo.GetAll();

                Hosts.Clear();
                foreach (var h in hosts) Hosts.Add(h);


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
                DebugConsoleService.Log("LoadHosts Error: " + ex.Message);
            }
        }


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
        private void Delete(SshConnectionInfo? host)
        {
            if (host == null) return;
            if (DeleteConfirm?.Invoke(host) != true) return;
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
