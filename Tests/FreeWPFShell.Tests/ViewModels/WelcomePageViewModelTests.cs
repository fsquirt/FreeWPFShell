using System.IO;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Tests.ViewModels
{
    /// <summary>
    /// WelcomePageViewModel 测试。使用临时主机库文件，验证主机加载/删除命令。
    /// </summary>
    [TestClass]
    public class WelcomePageViewModelTests : IDisposable
    {
        private readonly string _tempFile;

        public WelcomePageViewModelTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), "fwpt_hosts_test_" + Guid.NewGuid().ToString("N") + ".json");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        private WelcomePageViewModel CreateViewModel()
        {
            var settingsRepo = new SettingsRepository();
            var hostRepo = new HostRepository(settingsRepo, _tempFile);
            return new WelcomePageViewModel(hostRepo);
        }

        private void WriteHosts(params SshConnectionInfo[] hosts)
        {
            // 通过 AddAsync 写入（会保存到临时文件）
            var settingsRepo = new SettingsRepository();
            var hostRepo = new HostRepository(settingsRepo, _tempFile);
            foreach (var h in hosts)
                hostRepo.AddAsync(h, "secret").GetAwaiter().GetResult();
        }

        [TestMethod]
        public void LoadHosts_EmptyRepo_NoHosts()
        {
            var vm = CreateViewModel();
            Assert.IsNotNull(vm.Hosts);
            Assert.AreEqual(0, vm.Hosts.Count);
        }

        [TestMethod]
        public void LoadHosts_WithSavedHosts_PopulatesList()
        {
            WriteHosts(
                new SshConnectionInfo { HostName = "srv1", IpAddress = "10.0.0.1", SshUser = "root", SshPort = 22 },
                new SshConnectionInfo { HostName = "srv2", IpAddress = "10.0.0.2", SshUser = "admin", SshPort = 2222 });

            var vm = CreateViewModel();
            vm.LoadHosts();

            Assert.AreEqual(2, vm.Hosts.Count);
            Assert.IsTrue(vm.Hosts.Any(h => h.HostName == "srv1"));
            Assert.IsTrue(vm.Hosts.Any(h => h.HostName == "srv2"));
        }

        [TestMethod]
        public void Delete_RemovesHostFromRepo()
        {
            WriteHosts(new SshConnectionInfo { HostName = "to-delete", IpAddress = "10.0.0.9", SshUser = "root" });

            var vm = CreateViewModel();
            vm.LoadHosts();
            vm.DeleteConfirm = h => true; // 确认删除
            var host = vm.Hosts.First();
            vm.DeleteCommand.Execute(host);

            Assert.AreEqual(0, vm.Hosts.Count, "删除后列表应为空");
        }

        [TestMethod]
        public void ConnectCommand_InvokesConnectRequested()
        {
            var vm = CreateViewModel();
            WriteHosts(new SshConnectionInfo { HostName = "srv", IpAddress = "10.0.0.1", SshUser = "root" });
            vm.LoadHosts();

            SshConnectionInfo? requested = null;
            vm.ConnectRequested = h => requested = h;

            var host = vm.Hosts.First();
            vm.ConnectCommand.Execute(host);

            Assert.AreSame(host, requested, "ConnectRequested 应收到被点击的主机");
        }

        [TestMethod]
        public void AddConnectionCommand_InvokesCallback()
        {
            var vm = CreateViewModel();
            bool called = false;
            vm.AddConnectionRequested = () => called = true;

            vm.AddConnectionCommand.Execute(null);

            Assert.IsTrue(called, "AddConnectionRequested 应被调用");
        }
    }
}
