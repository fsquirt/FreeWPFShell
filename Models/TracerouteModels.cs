using FreeWPFShell.Share;

namespace FreeWPFShell.Models
{
    public class TracerouteHop
    {
        public int Hop { get; set; }
        public string Ip { get; set; } = string.Empty;
        public string SimpleGeo { get; set; } = string.Empty;
        public string Latency { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public IpGeoResult? GeoDetail { get; set; }
    }
}
