using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using MaxMind.Db;

using FreeWPFShell.Models;

namespace FreeWPFShell.Share
{
    public class IpGeoResult
    {
        public string Ip { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string CountryIso { get; set; } = string.Empty;
        public string Continent { get; set; } = string.Empty;
        public string Province { get; set; } = string.Empty;
        public string ProvinceCode { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string CityCode { get; set; } = string.Empty;
        public string Districts { get; set; } = string.Empty;
        public string DistrictsCode { get; set; } = string.Empty;
        public string ISP { get; set; } = string.Empty;
        public string NetType { get; set; } = string.Empty;
        public string ASN { get; set; } = string.Empty;
        public string ASOrg { get; set; } = string.Empty;
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? AccuracyRadius { get; set; }
        public string Timezone { get; set; } = string.Empty;
        public string PostalCode { get; set; } = string.Empty;
        public string RegisteredCountry { get; set; } = string.Empty;
        public string TraitsNetwork { get; set; } = string.Empty;
        public bool? IsAnonymousProxy { get; set; }
        public bool? IsSatelliteProvider { get; set; }
        public string SimpleGeo { get; set; } = string.Empty;
        public string DetailText { get; set; } = string.Empty;
    }

    public class IpGeoService : IDisposable
    {
        private static readonly Lazy<IpGeoService> _instance = new(() => new IpGeoService());
        public static IpGeoService Instance => _instance.Value;

        private readonly Reader? _geoCN;
        private readonly Reader? _geoLite2ASN;
        private readonly Reader? _geoLite2City;

        private IpGeoService()
        {
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IPDataBase");
            _geoCN = LoadDb(Path.Combine(dbPath, "GeoCN.mmdb"), "GeoCN");
            _geoLite2ASN = LoadDb(Path.Combine(dbPath, "GeoLite2-ASN.mmdb"), "ASN");
            _geoLite2City = LoadDb(Path.Combine(dbPath, "GeoLite2-City.mmdb"), "City");
        }

        private Reader? LoadDb(string path, string name)
        {
            if (!File.Exists(path)) return null;
            try { return new Reader(path); } catch { return null; }
        }

        public IpGeoResult Query(string ip)
        {
            var r = new IpGeoResult { Ip = ip };
            if (!IPAddress.TryParse(ip, out var ipAddr)) return r;
            if (IsPrivate(ipAddr)) { r.SimpleGeo = "内网IP"; r.DetailText = $"IP: {ip}\n这是内网私有地址，无法查询归属地。"; return r; }

            try
            {
                bool isChinese = false;

                // 1. GeoCN
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

                // 2. GeoLite2-ASN
                if (_geoLite2ASN != null)
                {
                    var raw = _geoLite2ASN.Find<Dictionary<string, object>>(ipAddr);
                    if (raw != null)
                    {
                        r.ASN = TryGet(raw, "autonomous_system_number") ?? "";
                        r.ASOrg = TryGet(raw, "autonomous_system_organization") ?? "";
                    }
                }

                // 3. GeoLite2-City
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
            return r;
        }

        private static string? TryGet(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) ? val?.ToString() : null;
        private static double? TryGetDouble(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) ? (val is double dv ? dv : val is float fv ? (double)fv : double.TryParse(val?.ToString(), out var p) ? p : (double?)null) : null;
        private static bool? TryGetBool(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) ? (val is bool bv ? bv : bool.TryParse(val?.ToString(), out var p) ? p : (bool?)null) : null;
        private static Dictionary<string, object>? GetNested(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) && val is Dictionary<string, object> n ? n : null;
        private static List<object>? GetList(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var val) && val is IList l ? l.Cast<object>().ToList() : null;

        private static bool IsPrivate(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            if (b.Length < 4) return false;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        private static string BuildSimpleGeo(IpGeoResult r, bool isChinese)
        {
            var p = new List<string>();
            if (isChinese) { p.Add(r.Province); if (r.City != r.Province) p.Add(r.City); if (!string.IsNullOrEmpty(r.Districts)) p.Add(r.Districts); p.Add(r.ISP); }
            else { p.Add(r.Country); if (r.Province != r.Country) p.Add(r.Province); if (r.City != r.Province) p.Add(r.City); }
            return string.Join("", p.Where(s => !string.IsNullOrEmpty(s)));
        }

        private static string BuildDetailText(IpGeoResult r)
        {
            var l = new List<string> { $"IP: {r.Ip}" };
            if (!string.IsNullOrEmpty(r.Continent)) l.Add($"大洲: {r.Continent}");
            if (!string.IsNullOrEmpty(r.Country)) l.Add($"国家/地区: {r.Country} ({r.CountryIso})");
            if (!string.IsNullOrEmpty(r.Province)) l.Add($"省份/州: {r.Province}{(string.IsNullOrEmpty(r.ProvinceCode) ? "" : " [" + r.ProvinceCode + "]")}");
            if (!string.IsNullOrEmpty(r.City)) l.Add($"城市: {r.City}{(string.IsNullOrEmpty(r.CityCode) ? "" : " [" + r.CityCode + "]")}");
            if (!string.IsNullOrEmpty(r.Districts)) l.Add($"区县: {r.Districts}{(string.IsNullOrEmpty(r.DistrictsCode) ? "" : " [" + r.DistrictsCode + "]")}");
            if (!string.IsNullOrEmpty(r.ISP)) l.Add($"运营商: {r.ISP}");
            if (!string.IsNullOrEmpty(r.NetType)) l.Add($"网络类型: {r.NetType}");
            if (r.Latitude.HasValue && r.Longitude.HasValue) l.Add($"地理坐标: {r.Latitude:F4}, {r.Longitude:F4} (精度半径: {r.AccuracyRadius ?? 0}km)");
            if (!string.IsNullOrEmpty(r.Timezone)) l.Add($"时区: {r.Timezone}");
            if (!string.IsNullOrEmpty(r.PostalCode)) l.Add($"邮政编码: {r.PostalCode}");
            if (!string.IsNullOrEmpty(r.RegisteredCountry)) l.Add($"注册地: {r.RegisteredCountry}");
            
            l.Add("\n--- 网络与 AS 信息 ---");
            if (!string.IsNullOrEmpty(r.TraitsNetwork)) l.Add($"CIDR 网段: {r.TraitsNetwork}");
            l.Add($"ASN: {(string.IsNullOrEmpty(r.ASN) ? "未知" : "AS" + r.ASN)}");
            l.Add($"AS 组织: {(string.IsNullOrEmpty(r.ASOrg) ? "未知" : r.ASOrg)}");
            if (r.IsAnonymousProxy == true) l.Add("⚠ 匿名代理: 是");
            if (r.IsSatelliteProvider == true) l.Add("⚠ 卫星网络: 是");
            
            return string.Join("\n", l);
        }

        public void Dispose() { _geoCN?.Dispose(); _geoLite2ASN?.Dispose(); _geoLite2City?.Dispose(); }
    }
}
