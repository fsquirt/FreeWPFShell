using FreeWPFShell.Models;

namespace FreeWPFShell.Tests.Models
{
    /// <summary>
    /// MonitorData 监控数据模型测试：环形网络历史缓冲区、最大值计算、
    /// 进程/磁盘列表更新等纯逻辑。
    /// </summary>
    [TestClass]
    public class MonitorDataTests
    {
        [TestMethod]
        public void AddNetHistoryEntry_UnderCapacity_Appends()
        {
            var md = new MonitorData();
            md.AddNetHistoryEntry(100, 200);
            md.AddNetHistoryEntry(300, 400);

            Assert.AreEqual(2, md.NetHistory.Count);
            Assert.AreEqual(100, md.NetHistory[0].rx);
            Assert.AreEqual(400, md.NetHistory[1].tx);
        }

        [TestMethod]
        public void AddNetHistoryEntry_OverCapacity_KeepsLast50()
        {
            var md = new MonitorData();
            for (int i = 1; i <= 55; i++)
                md.AddNetHistoryEntry(i, i * 2);

            Assert.AreEqual(50, md.NetHistory.Count);
            // 最旧的 5 条(1..5)被挤出，第一条变为 6
            Assert.AreEqual(6, md.NetHistory[0].rx);
            Assert.AreEqual(55, md.NetHistory[49].rx);
            Assert.AreEqual(110, md.NetHistory[49].tx);
        }

        [TestMethod]
        public void GetNetHistoryMax_ReturnsMaxAcrossRxTx()
        {
            var md = new MonitorData();
            md.AddNetHistoryEntry(100, 9999);
            md.AddNetHistoryEntry(5000, 3);
            Assert.AreEqual(9999, md.GetNetHistoryMax());
        }

        [TestMethod]
        public void GetNetHistoryMax_WhenEmpty_ReturnsMin1024()
        {
            var md = new MonitorData();
            Assert.AreEqual(1024, md.GetNetHistoryMax());
        }

        [TestMethod]
        public void GetNetHistoryMax_WithSmallValues_ReturnsMin1024()
        {
            var md = new MonitorData();
            md.AddNetHistoryEntry(10, 20);
            // 都小于 1024 时下限为 1024（保证图表刻度合理）
            Assert.AreEqual(1024, md.GetNetHistoryMax());
        }

        [TestMethod]
        public void UpdateProcesses_ReplacesListAndNotifies()
        {
            var md = new MonitorData();
            var raised = false;
            md.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MonitorData.Processes)) raised = true;
            };

            md.UpdateProcesses(new[] { new ProcessItem { Pid = 1, Cmd = "init" } });

            Assert.IsTrue(raised);
            Assert.AreEqual(1, md.Processes.Count);
            Assert.AreEqual("init", md.Processes[0].Cmd);
        }

        [TestMethod]
        public void UpdateDisks_ReplacesList()
        {
            var md = new MonitorData();
            md.UpdateDisks(new[] { new DiskItem { Path = "/", Avail = "1G", Size = "10G" } });

            Assert.AreEqual(1, md.Disks.Count);
            Assert.AreEqual("/", md.Disks[0].Path);
        }

        [TestMethod]
        public void CpuPct_SetValue_AlsoRaisesCpuText()
        {
            var md = new MonitorData();
            var cpuTextRaised = false;
            md.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MonitorData.CpuText)) cpuTextRaised = true;
            };

            md.CpuPct = 45.5;

            Assert.IsTrue(cpuTextRaised);
            Assert.AreEqual("45.5%", md.CpuText);
        }
    }
}
