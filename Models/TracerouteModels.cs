using FreeWPFShell.Share;

namespace FreeWPFShell.Models
{
    public class TracerouteHop : ObservableObject
    {
        private string _ip = string.Empty;
        private string _simpleGeo = string.Empty;
        private string _latency = string.Empty;
        private string _status = string.Empty;
        private IpGeoResult? _geoDetail;

        public int Hop { get; set; }

        public string Ip
        {
            get => _ip;
            set => SetField(ref _ip, value);
        }

        public string SimpleGeo
        {
            get => _simpleGeo;
            set => SetField(ref _simpleGeo, value);
        }

        public string Latency
        {
            get => _latency;
            set => SetField(ref _latency, value);
        }

        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        public IpGeoResult? GeoDetail
        {
            get => _geoDetail;
            set => SetField(ref _geoDetail, value);
        }
    }
}
