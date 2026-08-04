using FreeWPFShell.Models;
using FreeWPFShell.Share;

namespace FreeWPFShell.Tests.Services
{
    /// <summary>
    /// IpGeoService IP 归属地查询测试。
    /// 内网 IP 判断不依赖 mmdb；公网 IP 查询依赖 IPDataBase/mmdb 已复制到输出目录。
    /// </summary>
    [TestClass]
    public class IpGeoServiceTests
    {
        [TestMethod]
        public void Query_PrivateIp_ReturnsInternalMarker()
        {
            var result = IpGeoService.Instance.Query("10.0.0.1");
            Assert.AreEqual("内网IP", result.SimpleGeo);
        }

        [TestMethod]
        [DataRow("127.0.0.1")]
        [DataRow("192.168.1.100")]
        [DataRow("172.16.0.5")]
        [DataRow("169.254.1.1")]
        public void Query_PrivateIpRange_IsInternal(string ip)
        {
            var result = IpGeoService.Instance.Query(ip);
            Assert.AreEqual("内网IP", result.SimpleGeo, $"{ip} 应判定为内网");
        }

        [TestMethod]
        [DataRow("172.32.0.1")] // 172.31 之外不是内网
        [DataRow("8.8.8.8")]
        public void Query_PublicIp_NotInternal(string ip)
        {
            var result = IpGeoService.Instance.Query(ip);
            Assert.AreNotEqual("内网IP", result.SimpleGeo, $"{ip} 不应判定为内网");
        }

        [TestMethod]
        public void Query_InvalidIp_DoesNotThrow()
        {
            // 非法 IP 不应抛异常，返回空结果
            var result = IpGeoService.Instance.Query("not-an-ip");
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Query_SameIp_ReturnsCachedResult()
        {
            var first = IpGeoService.Instance.Query("114.114.114.114");
            var second = IpGeoService.Instance.Query("114.114.114.114");
            Assert.AreSame(first, second, "相同 IP 应命中缓存");
        }

        [TestMethod]
        public void Query_PublicIp_ProducesSimpleGeo()
        {
            // 公网 IP 应产生一个非空的归属地字符串（依赖 mmdb）
            var result = IpGeoService.Instance.Query("114.114.114.114");
            Assert.IsNotNull(result.SimpleGeo);
        }
    }
}
