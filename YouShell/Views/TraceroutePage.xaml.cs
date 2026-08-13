using System;
using Microsoft.UI.Xaml.Controls;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// 路由追踪页。业务逻辑在 TracerouteViewModel，Code-behind 负责资源释放。
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

        public void Dispose()
        {
            ViewModel.CancelCommand.Execute(null);
        }
    }
}
