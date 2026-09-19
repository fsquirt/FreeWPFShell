using System.Windows.Controls;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{

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
