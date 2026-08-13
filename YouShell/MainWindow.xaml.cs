using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YouShell.Models;
using YouShell.Services;
using YouShell.ViewModels;
using YouShell.Views;

namespace YouShell
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<SshSessionService> ActiveSessions { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            Title = "YouShell";

            var settingsRepo = Core.AppServices.GetService<Repositories.SettingsRepository>();
            Services.BackdropService.Apply(this, settingsRepo.Load().BackdropType);

            WelcomePageControl.OpenSessionRequested = OpenSession;
            Closed += MainWindow_Closed;
        }

        // ── 会话 / 标签页 ────────────────────────────────────────

        public void OpenSession(SshConnectionInfo hostInfo)
        {
            var session = new SshSessionService(hostInfo);
            ActiveSessions.Add(session);

            var terminalPage = new TerminalAndSFTP(session);
            terminalPage.OpenSftpRequested = OpenSftpTab;
            terminalPage.OpenSshTunnelRequested = OpenSshTunnelManager;
            terminalPage.OpenTracerouteRequested = OpenTraceroutePage;
            terminalPage.OpenSystemManagementRequested = OpenSystemManagementPage;
            terminalPage.CloseTabRequested = CloseTabByContent;

            var tab = AddTab(session.DisplayName, terminalPage);

            session.OnConnected = () => terminalPage.BindSession();
            session.OnConnectFailed = async (ex) =>
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                await UserForm.ModernMessageBox.ShowAsync(
                    $"无法连接到 {hostInfo.HostName} ({hostInfo.IpAddress})\n\n错误信息: {msg}",
                    "连接失败", UserForm.MessageBoxButton.OK, UserForm.MessageBoxImage.Error);

                if (SessionTabs.TabItems.Contains(tab))
                {
                    SessionTabs.TabItems.Remove(tab);
                    ActiveSessions.Remove(session);
                    session.Disconnect();
                }
            };

            session.ConnectAsync();
        }

        private void OpenSftpTab(SshSessionService session, TerminalViewModel viewModel)
        {
            AddTab($"SFTP-{session.DisplayName}", new SftpPage(session, viewModel));
        }

        private void OpenSshTunnelManager()
        {
            AddTab("SSH隧道管理", new SshTunnelPage(ActiveSessions));
        }

        private void OpenTraceroutePage(string? initialTarget = null)
        {
            var page = new TraceroutePage();
            AddTab("路由追踪", page);
            if (!string.IsNullOrEmpty(initialTarget)) page.ViewModel.Target = initialTarget;
        }

        private void OpenSystemManagementPage(SshSessionService session)
        {
            AddTab($"系统管理-{session.DisplayName}", new SystemManagementPage(session));
        }

        private TabViewItem AddTab(string header, FrameworkElement content)
        {
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            content.VerticalAlignment = VerticalAlignment.Stretch;
            var tab = new TabViewItem { Header = header, Content = content, Tag = content };
            SessionTabs.TabItems.Add(tab);
            SessionTabs.SelectedItem = tab;
            return tab;
        }

        private void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is TabViewItem tab) CloseTab(tab);
        }

        private void CloseTabByContent(TerminalAndSFTP page)
        {
            foreach (var item in SessionTabs.TabItems)
                if (item is TabViewItem tab && tab.Tag == page) { CloseTab(tab); return; }
        }

        private void CloseTab(TabViewItem tab)
        {
            if (tab == WelcomeTab) return;
            SessionTabs.TabItems.Remove(tab);

            if (tab.Tag is TerminalAndSFTP termPage)
            {
                var session = termPage.Session;
                // 关闭该会话对应的 SFTP 标签
                if (session != null)
                {
                    foreach (var item in SessionTabs.TabItems.ToList())
                        if (item is TabViewItem t && t.Tag is SftpPage sp && sp.Session == session)
                            CloseTab(t);
                }
                termPage.Cleanup();
                if (session != null)
                {
                    ActiveSessions.Remove(session);
                    session.Dispose();
                }
            }
            else if (tab.Tag is SftpPage sftpPage)
            {
                sftpPage.Cleanup();
            }
            else if (tab.Tag is IDisposable d)
            {
                d.Dispose();
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            foreach (var s in ActiveSessions) try { s.Dispose(); } catch { }
            ActiveSessions.Clear();
        }
    }
}
