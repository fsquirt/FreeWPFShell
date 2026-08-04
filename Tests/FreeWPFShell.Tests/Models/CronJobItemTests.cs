using FreeWPFShell.Models;

namespace FreeWPFShell.Tests.Models
{
    /// <summary>
    /// CronJobItem.ScheduleDescription：cron 表达式 → 中文描述的纯逻辑测试。
    /// </summary>
    [TestClass]
    public class CronJobItemTests
    {
        [TestMethod]
        [DataRow("* * * * *", "每分钟")]
        [DataRow("*/5 * * * *", "每5分钟")]
        [DataRow("0 */2 * * *", "每2小时")]
        [DataRow("0 0 */3 * *", "每3天")]
        [DataRow("0 0 * * 0", "每周日 00:00")]
        [DataRow("0 0 1 * *", "每月1日 00:00")]
        [DataRow("0 0 1 1 *", "每年1月1日 00:00")]
        public void ScheduleDescription_ExactMatchValues(string cron, string expected)
        {
            Assert.AreEqual(expected, new CronJobItem { Schedule = cron }.ScheduleDescription);
        }

        [TestMethod]
        public void ScheduleDescription_MinuteEveryN()
        {
            // "*/15" + 全通配 → "每15分钟"
            Assert.AreEqual("每15分钟", new CronJobItem { Schedule = "*/15 * * * *" }.ScheduleDescription);
        }

        [TestMethod]
        public void ScheduleDescription_DailyAtTime()
        {
            // 每天 03:30：min=30, hour=3, dom/month/dow=*
            Assert.AreEqual("每天 03:30", new CronJobItem { Schedule = "30 3 * * *" }.ScheduleDescription);
        }

        [TestMethod]
        public void ScheduleDescription_InvalidFormat_ReturnsRaw()
        {
            // 字段数不对时原样返回
            string raw = "0 0 * *";
            Assert.AreEqual(raw, new CronJobItem { Schedule = raw }.ScheduleDescription);
        }

        [TestMethod]
        public void StatusText_Enabled_IsChineseEnable()
        {
            Assert.AreEqual("启用", new CronJobItem { Enabled = true }.StatusText);
            Assert.AreEqual("禁用", new CronJobItem { Enabled = false }.StatusText);
        }
    }
}
