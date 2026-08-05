using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.UserForm;
using FreeWPFShell.ViewModels;

namespace FreeWPFShell.Views
{
    /// <summary>
    /// SSH 隧道管理页。业务逻辑已迁移到 SshTunnelViewModel，
    /// Code-behind 负责注入活跃会话列表与消息提示回调。
    /// </summary>
    public partial class SshTunnelPage : UserControl
    {
        public SshTunnelViewModel ViewModel { get; }

        public SshTunnelPage()
        {
            InitializeComponent();
            ViewModel = new SshTunnelViewModel();
            DataContext = ViewModel;

            ViewModel.ShowMessage = msg => ModernMessageBox.Show(msg);

            // 从主窗口填充活跃会话列表
            if (Application.Current.MainWindow is MainForm mf)
            {
                foreach (var s in mf.ActiveSessions) ViewModel.ActiveSessions.Add(s);
                if (ViewModel.ActiveSessions.Count > 0)
                    ViewModel.SelectedSession = ViewModel.ActiveSessions.First();
            }
        }
    }
}
