using System;
using System.Collections.Generic;
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

        private readonly Repositories.SettingsRepository _settingsRepo;
        private readonly Dictionary<SshSessionService, List<Window>> _sessionWindows = new();

        public MainWindow()
        {
            InitializeComponent();
            Title = "YouShell";

            // 沉浸式标题栏：内容延伸到标题栏区域，AppTitleBar 作为可拖拽标题栏
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 窗口尺寸：偏正方形（比原 WPF 1280×800 更窄更高）
            Services.WindowManager.ResizeTo(this, Services.WindowManager.WindowWidthDips, Services.WindowManager.WindowHeightDips);

            _settingsRepo = Core.AppServices.GetService<Repositories.SettingsRepository>();
            Services.BackdropService.Apply(this, _settingsRepo.Load().BackdropType);

            WelcomePageControl.OpenSessionRequested = OpenSession;
            Closed += MainWindow_Closed;
        }

        /// <summary>「TAB仅SSH连接」：启用后非 SSH 页面改用独立弹窗。</summary>
        private bool OpenAsWindow => _settingsRepo.Load().TabOnlySsh;

        // ── 会话 / 标签页 ────────────────────────────────────────

        public void OpenSession(SshConnectionInfo hostInfo)
        {
            var session = new SshSessionService(hostInfo);
            ActiveSessions.Add(session);

            var terminalPage = new TerminalAndSFTP(session);
            terminalPage.OpenSftpRequested = OpenSftp;
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

        private void OpenSftp(SshSessionService session, TerminalViewModel viewModel)
        {
            var page = new SftpPage(session, viewModel);
            if (OpenAsWindow) OpenInWindow($"SFTP-{session.DisplayName}", page, session);
            else AddTab($"SFTP-{session.DisplayName}", page);
        }

        private void OpenSshTunnelManager()
        {
            var page = new SshTunnelPage(ActiveSessions);
            if (OpenAsWindow) OpenInWindow("SSH隧道管理", page);
            else AddTab("SSH隧道管理", page);
        }

        private void OpenTraceroutePage(string? initialTarget = null)
        {
            var page = new TraceroutePage();
            if (!string.IsNullOrEmpty(initialTarget)) page.ViewModel.Target = initialTarget;
            if (OpenAsWindow) OpenInWindow("路由追踪", page);
            else AddTab("路由追踪", page);
        }

        private void OpenSystemManagementPage(SshSessionService session)
        {
            var page = new SystemManagementPage(session);
            if (OpenAsWindow) OpenInWindow($"系统管理-{session.DisplayName}", page, session);
            else AddTab($"系统管理-{session.DisplayName}", page);
        }

        /// <summary>以独立窗口打开页面，附加主窗口同款材质；绑定会话的窗口会随会话销毁而关闭。</summary>
        private void OpenInWindow(string title, FrameworkElement content, SshSessionService? session = null)
        {
            var window = Services.WindowManager.OpenSecondary(title, content, _settingsRepo.Load().BackdropType,
                () => CleanupContent(content));

            if (session != null)
            {
                if (!_sessionWindows.TryGetValue(session, out var list))
                {
                    list = new List<Window>();
                    _sessionWindows[session] = list;
                }
                list.Add(window);
            }
        }

        private static void CleanupContent(FrameworkElement content)
        {
            switch (content)
            {
                case SftpPage sftp: sftp.Cleanup(); break;
                case IDisposable d: d.Dispose(); break;
            }
        }

        private void CloseSessionWindows(SshSessionService session)
        {
            if (_sessionWindows.TryGetValue(session, out var windows))
            {
                foreach (var w in windows) w.Close();
                _sessionWindows.Remove(session);
            }
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
                    CloseSessionWindows(session);
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
            Services.WindowManager.CloseAll();
        }
    }
}
