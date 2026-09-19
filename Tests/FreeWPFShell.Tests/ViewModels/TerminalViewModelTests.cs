using FreeWPFShell.Models;
using FreeWPFShell.Services;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Tests.ViewModels
{

    [TestClass]
    public class TerminalViewModelTests
    {
        private static SshSessionService CreateSession()
        {
            return new SshSessionService(new SshConnectionInfo { Id = "test-host" });
        }

        [TestMethod]
        public void InitialState_CurrentPathIsRoot()
        {
            using var session = CreateSession();
            var vm = new TerminalViewModel(session);
            Assert.AreEqual("/", vm.CurrentPath);
            Assert.IsNotNull(vm.Files);
            Assert.AreEqual(0, vm.Files.Count);
        }

        [TestMethod]
        public void BuildCopyText_ContainsHostIdAndPaths()
        {
            using var session = CreateSession();
            var vm = new TerminalViewModel(session);
            var items = new[]
            {
                new RemoteFile { FullName = "/etc/passwd", IsDirectory = false },
                new RemoteFile { FullName = "/var/log/syslog", IsDirectory = false },
            };

            string text = vm.BuildCopyText(items);

            Assert.IsTrue(text.StartsWith($"FreeWPFRemoteCopy|test-host|"), "应以主机 ID 前缀开头");
            Assert.IsTrue(text.Contains("/etc/passwd"), "应包含文件路径");
            Assert.IsTrue(text.Contains("/var/log/syslog"), "应包含第二个文件路径");
        }

        [TestMethod]
        public void IsTransferring_InitiallyFalse()
        {
            using var session = CreateSession();
            var vm = new TerminalViewModel(session);
            Assert.IsFalse(vm.IsTransferring);
        }

        [TestMethod]
        public void CancelAllTransfersCommand_Exists_AndDoesNotThrow()
        {
            using var session = CreateSession();
            var vm = new TerminalViewModel(session);

            vm.CancelAllTransfersCommand.Execute(null);
            Assert.IsTrue(true);
        }
    }
}
