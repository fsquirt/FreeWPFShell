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
        public string Province { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string ISP { get; set; } = string.Empty;
        public string ASN { get; set; } = string.Empty;
        public string ASOrg { get; set; } = string.Empty;
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string Timezone { get; set; } = string.Empty;
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
        private bool _dumped;

        private IpGeoService()
        {
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IPDataBase");
            Debug.WriteLine($"[IpGeo] DB path: {dbPath}, exists: {Directory.Exists(dbPath)}");

            _geoCN = LoadDb(Path.Combine(dbPath, "GeoCN.mmdb"), "GeoCN");
            _geoLite2ASN = LoadDb(Path.Combine(dbPath, "GeoLite2-ASN.mmdb"), "ASN");
            _geoLite2City = LoadDb(Path.Combine(dbPath, "GeoLite2-City.mmdb"), "City");
        }

        private Reader? LoadDb(string path, string name)
        {
            if (!File.Exists(path))
            {
                Debug.WriteLine($"[IpGeo] {name}: file not found at {path}");
                return null;
            }
            try
            {
                var r = new Reader(path);
                Debug.WriteLine($"[IpGeo] {name}: loaded OK from {path}");
                return r;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IpGeo] {name}: FAIL - {ex}");
                return null;
            }
        }

        public IpGeoResult Query(string ip)
        {
            var r = new IpGeoResult { Ip = ip };

            Debug.WriteLine($"[IpGeo] Query({ip}) called");

            if (!IPAddress.TryParse(ip, out var ipAddr))
            {
                Debug.WriteLine($"[IpGeo] Query({ip}): invalid IP, returning empty");
                return r;
            }

            // Private IPs: no GeoIP database covers them
            if (IsPrivate(ipAddr))
            {
                r.SimpleGeo = "内网IP";
                r.DetailText = $"IP: {ip}\n\n这是内网私有地址，无法查询归属地。";
                return r;
            }

            try
            {
                bool isChinese = false;

                // Step 1: GeoCN (Chinese optimized)
                if (_geoCN != null)
                {
                    try
                    {
                        var raw = _geoCN.Find<Dictionary<string, object>>(ipAddr);
                        Debug.WriteLine($"[IpGeo] GeoCN.Find returned: {raw?.GetType().Name ?? "null"}, count={raw?.Count ?? 0}");

                        if (raw != null && raw.Count > 0)
                        {
                            if (!_dumped) { DumpDict("GeoCN", raw, ""); _dumped = true; }

                            // Try every possible field name
                            r.Country = TryGet(raw, "country_name") ?? TryGet(raw, "country") ?? "";
                            r.Province = TryGet(raw, "province") ?? TryGet(raw, "region") ?? TryGet(raw, "region_name") ?? "";
                            r.City = TryGet(raw, "city") ?? TryGet(raw, "city_name") ?? "";
                            r.ISP = TryGet(raw, "isp") ?? TryGet(raw, "isp_name") ?? TryGet(raw, "operator") ?? TryGet(raw, "org") ?? "";

                            Debug.WriteLine($"[IpGeo] GeoCN result: country=[{r.Country}] province=[{r.Province}] city=[{r.City}] isp=[{r.ISP}]");
                            if (!string.IsNullOrEmpty(r.Province) || !string.IsNullOrEmpty(r.City) || !string.IsNullOrEmpty(r.ISP))
                                isChinese = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[IpGeo] GeoCN error: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Step 2: GeoLite2-ASN
                if (_geoLite2ASN != null)
                {
                    try
                    {
                        var raw = _geoLite2ASN.Find<Dictionary<string, object>>(ipAddr);

                        if (raw != null && raw.Count > 0)
                        {
                            if (!_dumped) { DumpDict("ASN", raw, ""); _dumped = true; }

                            r.ASN = TryGet(raw, "autonomous_system_number") ?? TryGet(raw, "asn") ?? "";
                            r.ASOrg = TryGet(raw, "autonomous_system_organization") ?? TryGet(raw, "as_org") ?? "";

                            Debug.WriteLine($"[IpGeo] ASN result: asn=[{r.ASN}] org=[{r.ASOrg}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[IpGeo] ASN error: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Step 3: GeoLite2-City for non-Chinese IPs
                if (!isChinese && _geoLite2City != null)
                {
                    try
                    {
                        var raw = _geoLite2City.Find<Dictionary<string, object>>(ipAddr);

                        if (raw != null && raw.Count > 0)
                        {
                            if (!_dumped) { DumpDict("City", raw, ""); _dumped = true; }

                            // GeoLite2-City has nested structure: country.names.zh-CN, subdivisions[0].names.en, city.names.zh-CN, etc.
                            var country = GetNested(raw, "country");
                            if (country != null)
                            {
                                var names = GetNested(country, "names");
                                if (names != null)
                                {
                                    r.Country = TryGet(names, "zh-CN") ?? TryGet(names, "zh") ?? TryGet(names, "en") ?? "";
                                    if (string.IsNullOrEmpty(r.Country))
                                        r.Country = FirstStringValue(names);
                                }
                            }

                            // registered_country
                            if (string.IsNullOrEmpty(r.Country))
                            {
                                var rc = GetNested(raw, "registered_country");
                                if (rc != null)
                                {
                                    var names = GetNested(rc, "names");
                                    if (names != null) r.Country = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? FirstStringValue(names) ?? "";
                                }
                            }

                            // subdivisions
                            var subs = GetList(raw, "subdivisions");
                            if (subs != null && subs.Count > 0 && subs[0] is Dictionary<string, object> sub0Dict)
                            {
                                var names = GetNested(sub0Dict, "names");
                                if (names != null) r.Province = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? FirstStringValue(names) ?? "";
                            }

                            // city
                            var cityObj = GetNested(raw, "city");
                            if (cityObj != null)
                            {
                                var names = GetNested(cityObj, "names");
                                if (names != null) r.City = TryGet(names, "zh-CN") ?? TryGet(names, "en") ?? FirstStringValue(names) ?? "";
                            }

                            // location
                            var loc = GetNested(raw, "location");
                            if (loc != null)
                            {
                                r.Latitude = TryGetDouble(loc, "latitude");
                                r.Longitude = TryGetDouble(loc, "longitude");
                                r.Timezone = TryGet(loc, "time_zone") ?? "";
                            }

                            Debug.WriteLine($"[IpGeo] City result: country=[{r.Country}] province=[{r.Province}] city=[{r.City}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[IpGeo] City error: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                r.SimpleGeo = BuildSimpleGeo(r, isChinese);
                r.DetailText = BuildDetailText(r);
                Debug.WriteLine($"[IpGeo] Final SimpleGeo=[{r.SimpleGeo}] ASN=[{r.ASN}]");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IpGeo] Query FATAL error for {ip}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            return r;
        }

        // --- Helper: safely get a string value from a Dictionary<string, object> ---
        private static string? TryGet(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var val) && val != null)
                return val.ToString();
            return null;
        }

        private static double? TryGetDouble(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var val) && val != null)
            {
                if (val is double dv) return dv;
                if (val is float fv) return fv;
                if (double.TryParse(val.ToString(), out var parsed)) return parsed;
            }
            return null;
        }

        private static Dictionary<string, object>? GetNested(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var val) && val is Dictionary<string, object> nested)
                return nested;
            return null;
        }

        private static List<object>? GetList(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var val) && val is IList list)
            {
                var result = new List<object>();
                foreach (var item in list) result.Add(item);
                return result;
            }
            return null;
        }

        private static string? FirstStringValue(Dictionary<string, object> d)
        {
            foreach (var kv in d)
                if (kv.Value is string s && !string.IsNullOrEmpty(s))
                    return s;
            return null;
        }

        // --- Dump dictionary structure for debugging ---
        private void DumpDict(string tag, Dictionary<string, object> d, string prefix)
        {
            if (!_dumped) Debug.WriteLine($"[IpGeo] === {tag} dump ===");
            foreach (var kv in d)
            {
                string fullKey = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}.{kv.Key}";
                if (kv.Value is Dictionary<string, object> nested)
                    DumpDict(tag, nested, fullKey);
                else if (kv.Value is IList list)
                {
                    Debug.WriteLine($"[IpGeo] {fullKey}: [{list.Count}]");
                    for (int i = 0; i < Math.Min(list.Count, 3); i++)
                    {
                        if (list[i] is Dictionary<string, object> item)
                            DumpDict(tag, item, $"{fullKey}[{i}]");
                        else
                            Debug.WriteLine($"[IpGeo] {fullKey}[{i}] = {list[i]} ({list[i]?.GetType().Name})");
                    }
                }
                else
                    Debug.WriteLine($"[IpGeo] {fullKey} = {kv.Value} ({kv.Value?.GetType().Name})");
            }
            if (string.IsNullOrEmpty(prefix)) Debug.WriteLine($"[IpGeo] === {tag} end ===");
        }

        private static bool IsPrivate(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 127.0.0.0/8
            if (b[0] == 127) return true;
            // 169.254.0.0/16 (link-local)
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        private static string BuildSimpleGeo(IpGeoResult r, bool isChinese)
        {
            var parts = new List<string>();
            if (isChinese)
            {
                if (!string.IsNullOrEmpty(r.Province)) parts.Add(r.Province);
                if (!string.IsNullOrEmpty(r.City) && r.City != r.Province) parts.Add(r.City);
                if (!string.IsNullOrEmpty(r.ISP)) parts.Add(r.ISP);
            }
            else
            {
                if (!string.IsNullOrEmpty(r.Country)) parts.Add(r.Country);
                if (!string.IsNullOrEmpty(r.Province) && r.Province != r.Country) parts.Add(r.Province);
                if (!string.IsNullOrEmpty(r.City) && r.City != r.Province) parts.Add(r.City);
            }
            return string.Join("", parts);
        }

        private static string BuildDetailText(IpGeoResult r)
        {
            var lines = new List<string> { $"IP: {r.Ip}" };
            if (!string.IsNullOrEmpty(r.Country)) lines.Add($"国家/地区: {r.Country}");
            if (!string.IsNullOrEmpty(r.Province)) lines.Add($"省份/州: {r.Province}");
            if (!string.IsNullOrEmpty(r.City)) lines.Add($"城市: {r.City}");
            if (!string.IsNullOrEmpty(r.ISP)) lines.Add($"ISP: {r.ISP}");
            if (r.Latitude.HasValue && r.Longitude.HasValue)
                lines.Add($"坐标: {r.Latitude.Value:F4}, {r.Longitude.Value:F4}");
            if (!string.IsNullOrEmpty(r.Timezone)) lines.Add($"时区: {r.Timezone}");
            lines.Add("");
            lines.Add("--- AS 信息 ---");
            lines.Add(!string.IsNullOrEmpty(r.ASN) ? $"ASN: AS{r.ASN}" : "ASN: 未知");
            lines.Add(!string.IsNullOrEmpty(r.ASOrg) ? $"AS 所属: {r.ASOrg}" : "AS 所属: 未知");
            return string.Join("\n", lines);
        }

        public void Dispose()
        {
            _geoCN?.Dispose();
            _geoLite2ASN?.Dispose();
            _geoLite2City?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
