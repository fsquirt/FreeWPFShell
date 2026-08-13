using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using MaxMind.Db;

using YouShell.Models;

namespace YouShell.Share
{
    public class IpGeoService : IDisposable
    {
        private static readonly Lazy<IpGeoService> _instance = new(() => new IpGeoService());
        public static IpGeoService Instance => _instance.Value;

        private readonly Reader? _geoCN;
        private readonly Reader? _geoLite2ASN;
        private readonly Reader? _geoLite2City;

        // IP 查询缓存，避免重复查询相同 IP 创建大量对象
        private readonly Dictionary<string, IpGeoResult> _queryCache = new(32);

        private IpGeoService()
        {
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IPDataBase");
            _geoCN = LoadDb(Path.Combine(dbPath, "GeoCN.mmdb"), "GeoCN");
            _geoLite2ASN = LoadDb(Path.Combine(dbPath, "GeoLite2-ASN.mmdb"), "ASN");
            _geoLite2City = LoadDb(Path.Combine(dbPath, "GeoLite2-City.mmdb"), "City");
        }

        private static Reader? LoadDb(string path, string name)
        {
            if (!File.Exists(path)) return null;
            try { return new Reader(path); } catch { return null; }
        }

        public IpGeoResult Query(string ip)
        {
            if (_queryCache.TryGetValue(ip, out var cached)) return cached;

            var r = new IpGeoResult { Ip = ip };
            if (!IPAddress.TryParse(ip, out var ipAddr)) return r;
            if (IsPrivate(ipAddr)) { r.SimpleGeo = "内网IP"; r.DetailText = $"IP: {ip}\n这是内网私有地址，无法查询归属地。"; _queryCache[ip] = r; return r; }

            try
            {
                bool isChinese = false;

                if (_geoCN != null)
                {
                    var raw = _geoCN.Find<Dictionary<string, object>>(ipAddr);
                    if (raw != null)
                    {
                        r.ISP = TryGet(raw, "isp") ?? "";
                        r.NetType = TryGet(raw, "net") ?? "";
                        r.Province = TryGet(raw, "province") ?? "";
                        r.ProvinceCode = TryGet(raw, "provinceCode") ?? "";
                        r.City = TryGet(raw, "city") ?? "";
                        r.CityCode = TryGet(raw, "cityCode") ?? "";
                        r.Districts = TryGet(raw, "districts") ?? "";
                        r.DistrictsCode = TryGet(raw, "districtsCode") ?? "";
                        if (!string.IsNullOrEmpty(r.Province)) isChinese = true;
                    }
                }

                if (_geoLite2ASN != null)
                {
                    var raw = _geoLite2ASN.Find<Dictionary<string, object>>(ipAddr);
                    if (raw != null)
                    {
                        r.ASN = TryGet(raw, "autonomous_system_number") ?? "";
                        r.ASOrg = TryGet(raw, "autonomous_system_organization") ?? "";
                    }
                }

                if (_geoLite2City != null)
                {
                    var raw = _geoLite2City.Find<Dictionary<string, object>>(ipAddr);
                    if (raw != null)
                    {
                        var continent = GetNested(raw, "continent");
                        if (continent != null)
                        {
                            var names = GetNested(continent, "names");
                            if (names != null) r.Continent = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? "";
                        }

                        var country = GetNested(raw, "country");
                        if (country != null)
                        {
                            r.CountryIso = TryGet(country, "iso_code") ?? "";
                            var names = GetNested(country, "names");
                            if (names != null) r.Country = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? "";
                        }

                        var rc = GetNested(raw, "registered_country");
                        if (rc != null) r.RegisteredCountry = TryGet(rc, "iso_code") ?? "";

                        var subs = GetList(raw, "subdivisions");
                        if (subs != null && subs.Count > 0 && subs[0] is Dictionary<string, object> sub0)
                        {
                            var names = GetNested(sub0, "names");
                            if (names != null && string.IsNullOrEmpty(r.Province))
                                r.Province = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? "";
                        }

                        var cityObj = GetNested(raw, "city");
                        if (cityObj != null)
                        {
                            var names = GetNested(cityObj, "names");
                            if (names != null && string.IsNullOrEmpty(r.City))
                                r.City = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? "";
                        }

                        var postal = GetNested(raw, "postal");
                        if (postal != null) r.PostalCode = TryGet(postal, "code") ?? "";

                        var loc = GetNested(raw, "location");
                        if (loc != null)
                        {
                            r.Latitude = TryGetDouble(loc, "latitude");
                            r.Longitude = TryGetDouble(loc, "longitude");
                            r.AccuracyRadius = TryGetDouble(loc, "accuracy_radius");
                            r.Timezone = TryGet(loc, "time_zone") ?? "";
                        }

                        var traits = GetNested(raw, "traits");
                        if (traits != null)
                        {
                            r.TraitsNetwork = TryGet(traits, "network") ?? "";
                            r.IsAnonymousProxy = TryGetBool(traits, "is_anonymous_proxy");
                            r.IsSatelliteProvider = TryGetBool(traits, "is_satellite_provider");
                        }
                    }
                }

                r.SimpleGeo = BuildSimpleGeo(r, isChinese);
                r.DetailText = BuildDetailText(r);
            }
            catch { }
            _queryCache[ip] = r;
            return r;
        }

        private static string? TryGet(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) ? val?.ToString() : null;

        private static double? TryGetDouble(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var val) || val == null) return null;
            if (val is double dv) return dv;
            if (val is float fv) return (double)fv;
            if (val is long lv) return lv;
            if (val is int iv) return iv;
            return null;
        }

        private static bool? TryGetBool(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var val) || val == null) return null;
            if (val is bool bv) return bv;
            return null;
        }

        private static Dictionary<string, object>? GetNested(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) && val is Dictionary<string, object> n ? n : null;

        // 不拷贝列表：直接返回 IList，调用方只用 Count 和索引访问
        private static IList? GetList(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) && val is IList l ? l : null;

        /// <summary>不调用 GetAddressBytes（分配 byte[]），直接检查 IP 字符串前两个段。</summary>
        private static bool IsPrivate(IPAddress ip)
        {
            // AddressFamily.InterNetwork (IPv4) → ToString() 返回 a.b.c.d 格式
            string s = ip.ToString();
            // 快速路径：检查第一个数字
            int firstDot = s.IndexOf('.');
            if (firstDot < 0) return false; // IPv6 不在此判断
#if NET // .NET 6+ 支持 span 解析
            if (!int.TryParse(s.AsSpan(0, firstDot), out int first)) return false;
#else
            if (!int.TryParse(s.Substring(0, firstDot), out int first)) return false;
#endif
            if (first == 10 || first == 127) return true;

            int secondDot = s.IndexOf('.', firstDot + 1);
            if (secondDot < 0) return false;
#if NET
            if (!int.TryParse(s.AsSpan(firstDot + 1, secondDot - firstDot - 1), out int second)) return false;
#else
            if (!int.TryParse(s.Substring(firstDot + 1, secondDot - firstDot - 1), out int second)) return false;
#endif
            if (first == 172 && second >= 16 && second <= 31) return true;
            if (first == 192 && second == 168) return true;
            if (first == 169 && second == 254) return true;

            return false;
        }

        private static string BuildSimpleGeo(IpGeoResult r, bool isChinese)
        {
            if (isChinese)
            {
                bool hasProvince = !string.IsNullOrEmpty(r.Province);
                bool hasCity = !string.IsNullOrEmpty(r.City) && r.City != r.Province;
                bool hasDistricts = !string.IsNullOrEmpty(r.Districts);
                bool hasISP = !string.IsNullOrEmpty(r.ISP);

                // 直接用 + 拼接，编译器/运行时会对少量字符串做优化
                string result = hasProvince ? r.Province : "";
                if (hasCity) result += r.City;
                if (hasDistricts) result += r.Districts;
                if (hasISP) result += r.ISP;
                return result;
            }
            else
            {
                bool hasProvince = !string.IsNullOrEmpty(r.Province) && r.Province != r.Country;
                if (hasProvince) return r.Country + r.Province;
                if (!string.IsNullOrEmpty(r.ProvinceCode) && r.ProvinceCode != r.CountryIso) return r.Country + r.ProvinceCode;
                return r.Country;
            }
        }

        private static string BuildDetailText(IpGeoResult r)
        {
            // 预估容量，一次性分配 StringBuilder
            int est = 256;
            var sb = new System.Text.StringBuilder(est);

            sb.Append("IP: ").AppendLine(r.Ip);
            if (!string.IsNullOrEmpty(r.Continent)) sb.Append("大洲: ").AppendLine(r.Continent);
            if (!string.IsNullOrEmpty(r.Country)) sb.Append("国家/地区: ").Append(r.Country).Append(" (").Append(r.CountryIso).AppendLine(")");
            if (!string.IsNullOrEmpty(r.Province))
            {
                sb.Append("省份/州: ").Append(r.Province);
                if (!string.IsNullOrEmpty(r.ProvinceCode)) sb.Append(" [").Append(r.ProvinceCode).Append(']');
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(r.City))
            {
                sb.Append("城市: ").Append(r.City);
                if (!string.IsNullOrEmpty(r.CityCode)) sb.Append(" [").Append(r.CityCode).Append(']');
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(r.Districts))
            {
                sb.Append("区县: ").Append(r.Districts);
                if (!string.IsNullOrEmpty(r.DistrictsCode)) sb.Append(" [").Append(r.DistrictsCode).Append(']');
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(r.ISP)) sb.Append("运营商: ").AppendLine(r.ISP);
            if (!string.IsNullOrEmpty(r.NetType)) sb.Append("网络类型: ").AppendLine(r.NetType);
            if (r.Latitude.HasValue && r.Longitude.HasValue)
                sb.Append("地理坐标: ").Append(r.Latitude.Value.ToString("F4")).Append(", ").Append(r.Longitude.Value.ToString("F4"))
                  .Append(" (精度半径: ").Append((r.AccuracyRadius ?? 0).ToString("F0")).AppendLine("km)");
            if (!string.IsNullOrEmpty(r.Timezone)) sb.Append("时区: ").AppendLine(r.Timezone);
            if (!string.IsNullOrEmpty(r.PostalCode)) sb.Append("邮政编码: ").AppendLine(r.PostalCode);
            if (!string.IsNullOrEmpty(r.RegisteredCountry)) sb.Append("注册地: ").AppendLine(r.RegisteredCountry);

            sb.AppendLine().AppendLine("--- 网络与 AS 信息 ---");
            if (!string.IsNullOrEmpty(r.TraitsNetwork)) sb.Append("CIDR 网段: ").AppendLine(r.TraitsNetwork);
            sb.Append("ASN: ").AppendLine(string.IsNullOrEmpty(r.ASN) ? "未知" : "AS" + r.ASN);
            sb.Append("AS 组织: ").AppendLine(string.IsNullOrEmpty(r.ASOrg) ? "未知" : r.ASOrg);
            if (r.IsAnonymousProxy == true) sb.AppendLine("⚠ 匿名代理: 是");
            if (r.IsSatelliteProvider == true) sb.AppendLine("⚠ 卫星网络: 是");

            return sb.ToString();
        }

        public void Dispose() { _geoCN?.Dispose(); _geoLite2ASN?.Dispose(); _geoLite2City?.Dispose(); }
    }
}
