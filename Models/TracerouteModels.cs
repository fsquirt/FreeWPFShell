using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeWPFShell.Models
{
    public partial class TracerouteHop : ObservableObject
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
            set => SetProperty(ref _ip, value);
        }

        public string SimpleGeo
        {
            get => _simpleGeo;
            set => SetProperty(ref _simpleGeo, value);
        }

        public string Latency
        {
            get => _latency;
            set => SetProperty(ref _latency, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public IpGeoResult? GeoDetail
        {
            get => _geoDetail;
            set => SetProperty(ref _geoDetail, value);
        }
    }
}
