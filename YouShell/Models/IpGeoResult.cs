namespace YouShell.Models
{
    /// <summary>IP 归属地查询结果（纯数据模型）。</summary>
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
}
