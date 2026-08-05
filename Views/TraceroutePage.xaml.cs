using System.Windows.Controls;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{
    /// <summary>
    /// 路由追踪页。业务逻辑已迁移到 TracerouteViewModel，
    /// Code-behind 仅负责 ViewModel 绑定与资源释放。
    /// </summary>
    public partial class TraceroutePage : UserControl, System.IDisposable
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
