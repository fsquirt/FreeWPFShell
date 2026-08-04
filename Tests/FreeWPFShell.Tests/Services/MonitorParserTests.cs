using FreeWPFShell.Models;
using FreeWPFShell.Services;

namespace FreeWPFShell.Tests.Services
{
    /// <summary>
    /// SshMonitorService 的 top/proc/net 文本解析逻辑测试。
    /// 通过 internal 测试构造函数注入 MonitorData，无需真实 SSH 客户端。
    /// </summary>
    [TestClass]
    public class MonitorParserTests
    {
        private const string TopSample = @"
==STAT==
cpu  1000 0 0 0 0 0 0 0 0 0
==TOP==
top - 12:00:00 up 1 day, 2:34,  1 user,  load average: 0.52, 0.40, 0.35
MiB Mem :   1000.0 total,  500.0 used,  500.0 free,   0.0 buffers
MiB Swap:    200.0 total,   50.0 used,  150.0 free
==PROC==
%MEM %CPU COMMAND
  5.0  2.0 /usr/sbin/sshd
==NET==
eth0:  1000 0 0 0 0 0 0 0  2000 0 0 0 0 0 0 0
==DISK==
Filesystem  Avail  Size
/dev/sda1    10G   20G
";

        private static SshMonitorService CreateService()
        {
            return new SshMonitorService(new MonitorData());
        }

        [TestMethod]
        public void ParseTopOutput_ExtractsUptimeAndLoad()
        {
            var svc = CreateService();
            svc.ParseTopOutput(TopSample);

            Assert.IsTrue(svc.Monitor.Uptime.StartsWith("运行"));
            Assert.IsTrue(svc.Monitor.Uptime.Contains("1 day"));
            Assert.IsTrue(svc.Monitor.Load.Contains("负载"));
        }

        [TestMethod]
        public void ParseTopOutput_ExtractsMemText()
        {
            var svc = CreateService();
            svc.ParseTopOutput(TopSample);

            // 500*1024 KB = 500M；1000*1024 KB = 1000M
            Assert.AreEqual("500M/1000M", svc.Monitor.MemText);
        }

        [TestMethod]
        public void ParseTopOutput_ExtractsSwapText()
        {
            var svc = CreateService();
            svc.ParseTopOutput(TopSample);

            // 50*1024 KB = 50M；200*1024 KB = 200M
            Assert.AreEqual("50M/200M", svc.Monitor.SwapText);
        }

        [TestMethod]
        public void ParseTopOutput_ExtractsProcesses()
        {
            var svc = CreateService();
            svc.ParseTopOutput(TopSample);

            Assert.IsTrue(svc.Monitor.Processes.Count > 0);
            Assert.IsTrue(svc.Monitor.Processes.Any(p => p.Cmd.Contains("sshd")));
        }

        [TestMethod]
        public void ParseTopOutput_ExtractsDiskItems()
        {
            var svc = CreateService();
            svc.ParseTopOutput(TopSample);

            Assert.IsTrue(svc.Monitor.Disks.Count > 0);
            Assert.IsTrue(svc.Monitor.Disks.Any(d => d.Path.Contains("/dev/sda1")));
        }

        [TestMethod]
        public void ParseTopOutput_ComputesCpuUsage()
        {
            var svc = CreateService();
            // 第一次建立基线（无输出）
            svc.ParseTopOutput(TopSample);
            // 第二次 CPU 数值增大，应计算出 usage >= 0
            string secondSample = TopSample.Replace("cpu  1000", "cpu  2000");
            svc.ParseTopOutput(secondSample);

            Assert.IsTrue(svc.Monitor.CpuPct >= 0);
            Assert.IsTrue(svc.Monitor.CpuPct <= 100);
        }

        [TestMethod]
        public void ParseLinuxMonitorJson_ExtractsStats()
        {
            var svc = CreateService();
            string json = @"{""cpu_pct"":45.5,""mem_used"":536870912,""mem_total"":1073741824,""swap_used"":0,""swap_total"":0,""uptime"":""1 day"",""load"":""0.5"",""rx_speed"":1024,""tx_speed"":2048,""iface"":""eth0"",""processes"":[{""pid"":1,""cmd"":""init""}],""disks"":[{""path"":""/"",""avail"":""10G"",""size"":""20G""}]}";
            svc.ParseLinuxMonitorJson(json);

            Assert.AreEqual(45.5, svc.Monitor.CpuPct, 0.01);
            Assert.AreEqual("0.5G / 1.0G", svc.Monitor.MemText);
            Assert.IsTrue(svc.Monitor.Processes.Count > 0);
            Assert.IsTrue(svc.Monitor.Disks.Count > 0);
        }
    }
}
