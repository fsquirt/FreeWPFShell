using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Tests.ViewModels
{
    /// <summary>
    /// ViewModel 纯逻辑单元测试（不依赖真实 SSH 连接）。
    /// </summary>
    [TestClass]
    public class SystemManagementViewModelTests
    {
        private static SshSessionService CreateSession()
        {
            return new SshSessionService(new SshConnectionInfo { Id = "test-host" });
        }

        [TestMethod]
        [DataRow("plain", "plain")]
        [DataRow("has,comma", "\"has,comma\"")]
        [DataRow("has\"quote\"", "\"has\"\"quote\"\"\"")]
        [DataRow("has\nnewline", "\"has\nnewline\"")]
        [DataRow("", "")]
        [DataRow(null, "")]
        public void EscapeCsv_HandlesSpecialCharacters(string? input, string expected)
        {
            Assert.AreEqual(expected, SystemManagementViewModel.EscapeCsv(input));
        }

        [TestMethod]
        public void ApplyCronPreset_EveryMinute_SetsFields()
        {
            using var session = new SshSessionService(new SshConnectionInfo());
            var vm = new SystemManagementViewModel(session);
            try
            {
                vm.ApplyCronPreset("* * * * *");
                Assert.AreEqual("*", vm.CronMin);
                Assert.AreEqual("*", vm.CronHour);
                Assert.AreEqual("*", vm.CronDom);
                Assert.AreEqual("*", vm.CronMonth);
                Assert.AreEqual("*", vm.CronDow);
            }
            finally { vm.Stop(); }
        }

        [TestMethod]
        public void ApplyCronPreset_Daily_SetsFields()
        {
            using var session = new SshSessionService(new SshConnectionInfo());
            var vm = new SystemManagementViewModel(session);
            try
            {
                vm.ApplyCronPreset("0 0 * * *");
                Assert.AreEqual("0", vm.CronMin);
                Assert.AreEqual("0", vm.CronHour);
                Assert.AreEqual("*", vm.CronDom);
            }
            finally { vm.Stop(); }
        }

        [TestMethod]
        public void ApplyCronPreset_Custom_DoesNotChange()
        {
            using var session = new SshSessionService(new SshConnectionInfo());
            var vm = new SystemManagementViewModel(session);
            try
            {
                vm.CronMin = "1";
                vm.ApplyCronPreset("custom");
                Assert.AreEqual("1", vm.CronMin, "自定义不应覆盖字段");
            }
            finally { vm.Stop(); }
        }

        [TestMethod]
        public void ApplyCronPreset_InvalidTag_DoesNotThrow()
        {
            using var session = new SshSessionService(new SshConnectionInfo());
            var vm = new SystemManagementViewModel(session);
            try
            {
                vm.ApplyCronPreset("not-a-cron"); // 字段数不对，应安全返回
            }
            finally { vm.Stop(); }
        }
    }

    [TestClass]
    public class TracerouteViewModelTests
    {
        [TestMethod]
        public void InitialState_IsCorrect()
        {
            var vm = new TracerouteViewModel();
            Assert.IsFalse(vm.IsTracing);
            Assert.IsFalse(vm.IsCancelVisible);
            Assert.AreEqual(string.Empty, vm.Target);
            Assert.IsTrue(vm.DetailText.Length > 0, "初始应有提示文本");
        }

        [TestMethod]
        public void IsTracingChanged_UpdatesIsCancelVisible()
        {
            var vm = new TracerouteViewModel();
            bool raised = false;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(vm.IsCancelVisible)) raised = true;
            };

            vm.IsTracing = true;

            Assert.IsTrue(vm.IsCancelVisible);
            Assert.IsTrue(raised, "IsCancelVisible 应触发变更通知");
        }

        [TestMethod]
        public void IsTracing_False_IsCancelVisibleFalse()
        {
            var vm = new TracerouteViewModel();
            vm.IsTracing = true;
            vm.IsTracing = false;
            Assert.IsFalse(vm.IsCancelVisible);
        }
    }

    [TestClass]
    public class SshTunnelViewModelTests
    {
        [TestMethod]
        public void TunnelTypeIndex_Default_IsLocal()
        {
            var vm = new SshTunnelViewModel();
            Assert.AreEqual(0, vm.TunnelTypeIndex);
            Assert.IsTrue(vm.IsLocal, "默认应为本地转发");
        }

        [TestMethod]
        public void TunnelTypeIndex_Remote_IsNotLocal()
        {
            var vm = new SshTunnelViewModel();
            bool raised = false;
            vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(vm.IsLocal)) raised = true; };

            vm.TunnelTypeIndex = 1;

            Assert.IsFalse(vm.IsLocal);
            Assert.IsTrue(raised, "切换隧道类型应触发 IsLocal 通知");
        }

        [TestMethod]
        public void ActiveTunnels_FromGlobalManager()
        {
            var vm = new SshTunnelViewModel();
            Assert.IsNotNull(vm.ActiveTunnels);
        }
    }
}
