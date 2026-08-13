using Microsoft.UI.Xaml;
using YouShell.Models;
using YouShell.Services;
using YouShell.Share;
using YouShell.Terminal;

namespace YouShell
{
    public sealed partial class MainWindow : Window
    {
        private SshSessionService? _session;

        public MainWindow()
        {
            InitializeComponent();
            Title = "YouShell";
            Terminal.Loaded += Terminal_Loaded;
            Closed += MainWindow_Closed;
        }

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            // 终端宿主已挂载，先应用主题（避免连接前黑屏）
            Terminal.SetTheme(BuildTheme(), "Cascadia Code", 12);
            Terminal.FocusTerminal();
        }

        private void LocalBtn_Click(object sender, RoutedEventArgs e)
        {
            // 本地 ConPTY 快速验证：无需 SSH 凭据，验证终端渲染/中文/输入。
            DisconnectSession();
            Terminal.Connection = new ConPtyConnection("cmd.exe", 120, 30);
            Terminal.SetTheme(BuildTheme(), "Cascadia Code", 12);
            Terminal.FocusTerminal();
            StatusText.Text = "本地 cmd.exe";
        }

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            string host = HostBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                StatusText.Text = "请输入主机/IP";
                return;
            }

            // 断开上一个会话
            DisconnectSession();

            var info = new SshConnectionInfo
            {
                HostName = host,
                IpAddress = host,
                SshPort = int.TryParse(PortBox.Text, out int p) ? p : 22,
                SshUser = UserBox.Text.Trim(),
                AuthMethod = SshAuthMethod.Password,
                DecryptedSshSecret = PassBox.Password,
            };

            _session = new SshSessionService(info);
            _session.OnConnected = () =>
            {
                Terminal.Connection = _session.TerminalConnection;
                Terminal.SetTheme(BuildTheme(), "Cascadia Code", 12);
                Terminal.FocusTerminal();
                StatusText.Text = "已连接";
            };
            _session.OnConnectFailed = ex =>
            {
                StatusText.Text = "连接失败: " + ex.Message;
            };

            StatusText.Text = "连接中...";
            _session.ConnectAsync();
        }

        private static TerminalTheme BuildTheme() => new()
        {
            DefaultBackground = 0x0c0c0c,
            DefaultForeground = 0xcccccc,
            DefaultSelectionBackground = 0xcccccc,
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = new uint[]
            {
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
            },
        };

        private void DisconnectSession()
        {
            _session?.Disconnect();
            _session = null;
            Terminal.Connection = null;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            DisconnectSession();
        }
    }
}
