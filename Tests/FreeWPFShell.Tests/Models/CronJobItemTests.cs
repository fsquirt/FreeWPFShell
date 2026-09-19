using FreeWPFShell.Models;

namespace FreeWPFShell.Tests.Models
{

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

            Assert.AreEqual("每15分钟", new CronJobItem { Schedule = "*/15 * * * *" }.ScheduleDescription);
        }

        [TestMethod]
        public void ScheduleDescription_DailyAtTime()
        {

            Assert.AreEqual("每天 03:30", new CronJobItem { Schedule = "30 3 * * *" }.ScheduleDescription);
        }

        [TestMethod]
        public void ScheduleDescription_InvalidFormat_ReturnsRaw()
        {

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
