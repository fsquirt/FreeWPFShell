using System;
using Microsoft.UI.Xaml.Controls;
using YouShell.Models;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// 路由追踪页。业务逻辑在 TracerouteViewModel，Code-behind 负责资源释放与节点详情展示。
    /// </summary>
    public sealed partial class TraceroutePage : UserControl, IDisposable
    {
        public TracerouteViewModel ViewModel { get; }

        public TraceroutePage()
        {
            InitializeComponent();
            ViewModel = new TracerouteViewModel();
            DataContext = ViewModel;
        }

        /// <summary>点击某个中途节点时，在下方详情区显示该 IP 的完整归属地信息。</summary>
        private void HopsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HopsList.SelectedItem is not TracerouteHop hop) return;

            if (hop.GeoDetail != null && !string.IsNullOrEmpty(hop.GeoDetail.DetailText))
            {
                ViewModel.DetailText = hop.GeoDetail.DetailText;
                return;
            }

            var text = $"跳点 {hop.Hop}\nIP: {hop.Ip}\n状态: {hop.Status}";
            if (!string.IsNullOrEmpty(hop.SimpleGeo)) text += $"\n归属地: {hop.SimpleGeo}";
            if (!string.IsNullOrEmpty(hop.Latency)) text += $"\n延迟: {hop.Latency}";
            ViewModel.DetailText = text;
        }

        public void Dispose()
        {
            ViewModel.CancelCommand.Execute(null);
        }
    }
}
