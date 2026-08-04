using FreeWPFShell.Models;

namespace FreeWPFShell.Tests.Models
{
    /// <summary>
    /// TracerouteHop 路由跳节点模型测试：属性变更通知。
    /// </summary>
    [TestClass]
    public class TracerouteHopTests
    {
        [TestMethod]
        public void SetIp_RaisesPropertyChanged()
        {
            var hop = new TracerouteHop { Hop = 1 };
            bool raised = false;
            hop.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(TracerouteHop.Ip)) raised = true; };

            hop.Ip = "1.2.3.4";

            Assert.IsTrue(raised);
            Assert.AreEqual("1.2.3.4", hop.Ip);
        }

        [TestMethod]
        public void SetSameValue_DoesNotRaise()
        {
            var hop = new TracerouteHop { Latency = "5ms" };
            int count = 0;
            hop.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(TracerouteHop.Latency)) count++; };

            hop.Latency = "5ms"; // 相同值，不应触发

            Assert.AreEqual(0, count);
        }

        [TestMethod]
        public void SetGeoDetail_Notifies()
        {
            var hop = new TracerouteHop();
            bool raised = false;
            hop.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(TracerouteHop.GeoDetail)) raised = true; };

            hop.GeoDetail = new IpGeoResult { SimpleGeo = "中国" };

            Assert.IsTrue(raised);
            Assert.AreEqual("中国", ((IpGeoResult)hop.GeoDetail!).SimpleGeo);
        }

        [TestMethod]
        public void DefaultValues_AreCorrect()
        {
            var hop = new TracerouteHop();
            Assert.AreEqual(string.Empty, hop.Ip);
            Assert.AreEqual(string.Empty, hop.Status);
            Assert.AreEqual(string.Empty, hop.Latency);
            Assert.AreEqual(0, hop.Hop);
        }
    }
}
