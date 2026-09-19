using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.UserForm;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{

    public partial class SshTunnelPage : UserControl
    {
        public SshTunnelViewModel ViewModel { get; }

        public SshTunnelPage()
        {
            InitializeComponent();
            ViewModel = new SshTunnelViewModel();
            DataContext = ViewModel;

            ViewModel.ShowMessage = msg => ModernMessageBox.Show(msg);


            if (Application.Current.MainWindow is MainForm mf)
            {
                foreach (var s in mf.ActiveSessions) ViewModel.ActiveSessions.Add(s);
                if (ViewModel.ActiveSessions.Count > 0)
                    ViewModel.SelectedSession = ViewModel.ActiveSessions.First();
            }
        }
    }
}
