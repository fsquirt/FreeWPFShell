using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YouShell.Models;
using YouShell.Services;
using YouShell.UserForm;
using YouShell.ViewModels;

namespace YouShell.Views
{
    /// <summary>
    /// SSH 隧道管理页。业务逻辑在 SshTunnelViewModel，Code-behind 注入活跃会话与消息提示。
    /// </summary>
    public sealed partial class SshTunnelPage : UserControl
    {
        public SshTunnelViewModel ViewModel { get; }

        public SshTunnelPage(IEnumerable<SshSessionService> activeSessions)
        {
            InitializeComponent();
            ViewModel = new SshTunnelViewModel();
            DataContext = ViewModel;

            ViewModel.ShowMessage = msg => _ = ModernMessageBox.ShowAsync(msg);

            foreach (var s in activeSessions) ViewModel.ActiveSessions.Add(s);
            if (ViewModel.ActiveSessions.Count > 0)
                ViewModel.SelectedSession = ViewModel.ActiveSessions.First();
        }

        private void BtnDeleteTunnel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SshTunnelInfo tunnel)
                ViewModel.DeleteTunnelCommand.Execute(tunnel);
        }
    }
}
