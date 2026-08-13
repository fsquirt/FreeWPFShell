using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YouShell.Models;
using YouShell.Repositories;
using YouShell.UserForm;
using Renci.SshNet;

namespace YouShell.Views
{
    /// <summary>
    /// 新建/编辑连接对话框（WinUI 3 ContentDialog，加宽）。
    /// Primary="保存并连接"、Secondary="保存配置"。关闭后通过 SavedHostInfo/ConnectAfterSave 取结果。
    /// </summary>
    public sealed partial class AddConnectionPage : ContentDialog
    {
        private readonly HostRepository _hostRepo;
        private readonly KeyRepository _keyRepo = new();
        private string? _editingHostId;

        // 认证方式开关互斥时的重入保护
        private bool _syncingAuth;
        private bool _syncingJumpAuth;

        public bool ConnectAfterSave { get; private set; }
        public SshConnectionInfo? SavedHostInfo { get; private set; }

        public AddConnectionPage()
        {
            InitializeComponent();
            XamlRoot = ModernMessageBox.Root;
            _hostRepo = Core.AppServices.GetService<HostRepository>();
            LoadKeys();
        }

        private void LoadKeys()
        {
            var keys = _keyRepo.GetAll();
            cmbKeySelect.ItemsSource = keys;
            if (keys.Count > 0) cmbKeySelect.SelectedIndex = 0;

            cmbJumpKeySelect.ItemsSource = keys;
            if (keys.Count > 0) cmbJumpKeySelect.SelectedIndex = 0;
        }

        public AddConnectionPage(SshConnectionInfo editHost) : this()
        {
            _editingHostId = editHost.Id;
            Title = "编辑连接";
            txtHostName.Text = editHost.HostName;
            txtHost.Text = editHost.IpAddress;
            txtPort.Text = editHost.SshPort.ToString();
            txtUsername.Text = editHost.SshUser;
            if (editHost.AuthMethod == SshAuthMethod.PrivateKey)
            {
                rbKey.IsOn = true;
                rbPassword.IsOn = false;
                if (!string.IsNullOrEmpty(editHost.SshKeyId))
                {
                    for (int i = 0; i < cmbKeySelect.Items.Count; i++)
                    {
                        if (cmbKeySelect.Items[i] is SshKeyInfo k && k.Id == editHost.SshKeyId)
                        {
                            cmbKeySelect.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            if (editHost.UseProxy && editHost.Proxy != null)
            {
                chkProxy.IsOn = true;
                cmbProxyType.SelectedIndex = editHost.Proxy.Type switch
                {
                    ProxyType.Http => 0,
                    ProxyType.Socks4 => 1,
                    ProxyType.Socks5 => 2,
                    ProxyType.Ssh => 3,
                    _ => 0,
                };

                if (editHost.Proxy.Type == ProxyType.Ssh)
                {
                    txtJumpHost.Text = editHost.Proxy.ServerAddress;
                    txtJumpPort.Text = editHost.Proxy.Port.ToString();
                    txtJumpUser.Text = editHost.Proxy.Username;
                    if (!string.IsNullOrEmpty(editHost.Proxy.SshKeyId))
                    {
                        rbJumpKey.IsOn = true;
                        rbJumpPassword.IsOn = false;
                        for (int i = 0; i < cmbJumpKeySelect.Items.Count; i++)
                        {
                            if (cmbJumpKeySelect.Items[i] is SshKeyInfo k && k.Id == editHost.Proxy.SshKeyId)
                            {
                                cmbJumpKeySelect.SelectedIndex = i;
                                break;
                            }
                        }
                    }
                    else
                    {
                        rbJumpPassword.IsOn = true;
                        rbJumpKey.IsOn = false;
                        txtJumpPassword.Password = editHost.Proxy.Password ?? "";
                    }
                }
                else
                {
                    txtProxyHost.Text = editHost.Proxy.ServerAddress;
                    txtProxyPort.Text = editHost.Proxy.Port.ToString();
                    txtProxyUsername.Text = editHost.Proxy.Username;
                    txtProxyPassword.Password = editHost.Proxy.Password ?? "";
                }
            }
            // 面板可见性按当前代理类型刷新
            UpdateProxyPanels();
        }

        // ── 保存/连接 ────────────────────────────────────────────

        private async void AddConnection_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try { if (!await TrySaveAsync(connectAfter: true)) args.Cancel = true; }
            finally { deferral.Complete(); }
        }

        private async void AddConnection_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try { if (!await TrySaveAsync(connectAfter: false)) args.Cancel = true; }
            finally { deferral.Complete(); }
        }

        private async Task<bool> TrySaveAsync(bool connectAfter)
        {
            if (!await ValidateInputsAsync()) return false;
            try
            {
                var host = BuildHostInfo();
                string secret = GetSecret();
                if (string.IsNullOrEmpty(_editingHostId))
                {
                    await _hostRepo.AddAsync(host, secret);
                    if (connectAfter) host.DecryptedSshSecret = secret;
                }
                else
                {
                    await _hostRepo.UpdateAsync(_editingHostId, host, string.IsNullOrEmpty(secret) ? null : secret);
                    if (connectAfter)
                    {
                        if (string.IsNullOrEmpty(secret))
                        {
                            var decrypted = await _hostRepo.GetAndDecryptAsync(_editingHostId);
                            host.DecryptedSshSecret = decrypted.DecryptedSshSecret;
                        }
                        else host.DecryptedSshSecret = secret;
                    }
                }
                SavedHostInfo = host;
                ConnectAfterSave = connectAfter;
                return true;
            }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<bool> ValidateInputsAsync()
        {
            if (string.IsNullOrWhiteSpace(txtHost.Text)) { await WarnAsync("请输入主机IP地址。", "提示", MessageBoxImage.Information, txtHost); return false; }
            if (string.IsNullOrWhiteSpace(txtUsername.Text)) { await WarnAsync("请输入用户名。", "提示", MessageBoxImage.Information, txtUsername); return false; }
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535) { await WarnAsync("端口号无效。", "提示", MessageBoxImage.Warning, txtPort); return false; }
            if (string.IsNullOrEmpty(_editingHostId))
            {
                if (rbPassword.IsOn && string.IsNullOrEmpty(txtPassword.Password)) { await WarnAsync("请输入密码。", "提示", MessageBoxImage.Information, txtPassword); return false; }
            }
            if (rbKey.IsOn && cmbKeySelect.SelectedItem == null) { await WarnAsync("请选择一个已导入的 SSH 密钥。\n\n请先在主界面的「密钥管理」中导入密钥。", "提示", MessageBoxImage.Information, null); return false; }

            if (chkProxy.IsOn && cmbProxyType.SelectedIndex == 3)
            {
                if (string.IsNullOrWhiteSpace(txtJumpHost.Text)) { await WarnAsync("请输入跳板机IP地址。", "提示", MessageBoxImage.Information, txtJumpHost); return false; }
                if (!int.TryParse(txtJumpPort.Text, out int jp) || jp < 1 || jp > 65535) { await WarnAsync("跳板机端口号无效。", "提示", MessageBoxImage.Warning, txtJumpPort); return false; }
                if (string.IsNullOrWhiteSpace(txtJumpUser.Text)) { await WarnAsync("请输入跳板机用户名。", "提示", MessageBoxImage.Information, txtJumpUser); return false; }
                if (rbJumpPassword.IsOn && string.IsNullOrEmpty(_editingHostId) && string.IsNullOrEmpty(txtJumpPassword.Password)) { await WarnAsync("请输入跳板机密码。", "提示", MessageBoxImage.Information, txtJumpPassword); return false; }
                if (rbJumpKey.IsOn && cmbJumpKeySelect.SelectedItem == null) { await WarnAsync("请选择跳板机使用的 SSH 密钥。", "提示", MessageBoxImage.Information, null); return false; }
            }
            return true;
        }

        private static async Task WarnAsync(string msg, string title, MessageBoxImage image, Control? focusTarget)
        {
            await ModernMessageBox.ShowAsync(msg, title, MessageBoxButton.OK, image);
            focusTarget?.Focus(FocusState.Programmatic);
        }

        private SshConnectionInfo BuildHostInfo()
        {
            var host = new SshConnectionInfo
            {
                HostName = string.IsNullOrWhiteSpace(txtHostName.Text) ? txtHost.Text : txtHostName.Text,
                IpAddress = txtHost.Text.Trim(),
                SshPort = int.Parse(txtPort.Text),
                SshUser = txtUsername.Text.Trim(),
                AuthMethod = rbPassword.IsOn ? SshAuthMethod.Password : SshAuthMethod.PrivateKey,
                UseProxy = chkProxy.IsOn
            };
            if (host.AuthMethod == SshAuthMethod.PrivateKey && cmbKeySelect.SelectedItem is SshKeyInfo selectedKey)
                host.SshKeyId = selectedKey.Id;

            if (host.UseProxy)
            {
                var pt = cmbProxyType.SelectedIndex switch
                {
                    0 => ProxyType.Http,
                    1 => ProxyType.Socks4,
                    2 => ProxyType.Socks5,
                    3 => ProxyType.Ssh,
                    _ => ProxyType.None,
                };

                if (pt == ProxyType.Ssh)
                {
                    host.Proxy = new ProxyInfo
                    {
                        Type = pt,
                        ServerAddress = txtJumpHost.Text.Trim(),
                        Port = int.TryParse(txtJumpPort.Text, out int jp) ? jp : 22,
                        Username = txtJumpUser.Text.Trim(),
                        Password = rbJumpPassword.IsOn ? txtJumpPassword.Password : "",
                        SshKeyId = rbJumpKey.IsOn && cmbJumpKeySelect.SelectedItem is SshKeyInfo jumpKey ? jumpKey.Id : null
                    };
                }
                else
                {
                    host.Proxy = new ProxyInfo
                    {
                        Type = pt,
                        ServerAddress = txtProxyHost.Text.Trim(),
                        Port = int.TryParse(txtProxyPort.Text, out int pp) ? pp : 1080,
                        Username = txtProxyUsername.Text.Trim(),
                        Password = txtProxyPassword.Password
                    };
                }
            }
            return host;
        }

        private string GetSecret() => rbPassword.IsOn ? txtPassword.Password : "";

        // ── 面板/开关交互 ────────────────────────────────────────

        private void chkProxy_Toggled(object sender, RoutedEventArgs e)
        {
            if (pnlProxy != null) pnlProxy.IsEnabled = chkProxy.IsOn;
            UpdateProxyPanels();
        }

        private void CmbProxyType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateProxyPanels();
        }

        private void UpdateProxyPanels()
        {
            if (pnlNetProxy == null || pnlSshProxy == null) return;
            bool isSsh = chkProxy.IsOn && cmbProxyType.SelectedIndex == 3;
            pnlNetProxy.Visibility = isSsh ? Visibility.Collapsed : Visibility.Visible;
            pnlSshProxy.Visibility = isSsh ? Visibility.Visible : Visibility.Collapsed;
        }

        // 密码登录 / 密钥登录 互斥且至少启用一个：启用一个会禁用另一个，
        // 反过来禁用已启用的一个也会自动启用另一个（原实现只处理了「启用即禁用对方」这一半）。
        private void rbPassword_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingAuth) return;
            if (rbKey == null) return;
            _syncingAuth = true;
            try { rbKey.IsOn = !rbPassword.IsOn; }
            finally { _syncingAuth = false; }
        }

        private void rbKey_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingAuth) return;
            if (rbPassword == null) return;
            _syncingAuth = true;
            try { rbPassword.IsOn = !rbKey.IsOn; }
            finally { _syncingAuth = false; }
        }

        private void rbJumpPassword_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingJumpAuth) return;
            if (rbJumpKey == null) return;
            _syncingJumpAuth = true;
            try
            {
                rbJumpKey.IsOn = !rbJumpPassword.IsOn;
                UpdateJumpKeyPanel();
            }
            finally { _syncingJumpAuth = false; }
        }

        private void rbJumpKey_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingJumpAuth) return;
            if (rbJumpPassword == null) return;
            _syncingJumpAuth = true;
            try
            {
                rbJumpPassword.IsOn = !rbJumpKey.IsOn;
                UpdateJumpKeyPanel();
            }
            finally { _syncingJumpAuth = false; }
        }

        private void UpdateJumpKeyPanel()
        {
            if (pnlJumpKey != null)
                pnlJumpKey.Visibility = rbJumpKey.IsOn ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 代理测试 ─────────────────────────────────────────────

        private async void BtnTestProxy_Click(object sender, RoutedEventArgs e)
        {
            if (!chkProxy.IsOn)
            {
                await ModernMessageBox.ShowAsync("请先启用代理设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnTestProxy.IsEnabled = false;
            TxtTestProxy.Text = "测试中...";

            try
            {
                if (cmbProxyType.SelectedIndex == 3) await TestSshProxyAsync();
                else await TestNetProxyAsync();
            }
            catch (Exception ex)
            {
                await ModernMessageBox.ShowAsync($"测试失败: {ex.Message}", "代理测试错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TxtTestProxy.Text = "测试代理";
                BtnTestProxy.IsEnabled = true;
            }
        }

        private async Task TestSshProxyAsync()
        {
            string jumpHost = txtJumpHost.Text.Trim();
            if (string.IsNullOrEmpty(jumpHost))
            {
                await ModernMessageBox.ShowAsync("请填写跳板机地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!int.TryParse(txtJumpPort.Text, out int jumpPort) || jumpPort < 1 || jumpPort > 65535)
            {
                await ModernMessageBox.ShowAsync("跳板机端口无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string jumpUser = txtJumpUser.Text.Trim();
            string jumpPassword = txtJumpPassword.Password;

            PrivateKeyFile? jumpKey = null;
            if (rbJumpKey.IsOn && cmbJumpKeySelect.SelectedItem is SshKeyInfo selectedJumpKey)
            {
                jumpKey = await _keyRepo.LoadPrivateKeyFileAsync(selectedJumpKey.Id);
                if (jumpKey == null)
                {
                    await ModernMessageBox.ShowAsync("无法加载跳板机 SSH 密钥。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string targetHost = txtHost.Text.Trim();
            bool hasTarget = !string.IsNullOrEmpty(targetHost) && int.TryParse(txtPort.Text, out _);

            bool jumpOk = await Task.Run(() =>
            {
                try
                {
                    AuthenticationMethod[] authMethods = jumpKey != null
                        ? new AuthenticationMethod[] { new PrivateKeyAuthenticationMethod(jumpUser, jumpKey) }
                        : new AuthenticationMethod[] { new PasswordAuthenticationMethod(jumpUser, jumpPassword) };

                    var connInfo = new ConnectionInfo(jumpHost, jumpPort, jumpUser, authMethods) { Timeout = TimeSpan.FromSeconds(8) };
                    using var client = new SshClient(connInfo);
                    client.Connect();
                    client.Disconnect();
                    return true;
                }
                catch { return false; }
            });

            if (!jumpOk)
            {
                await ModernMessageBox.ShowAsync($"无法连接到跳板机 {jumpHost}:{jumpPort}\n\n请检查跳板机地址、端口及认证信息是否正确。", "跳板机连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (hasTarget)
            {
                bool tunnelOk = await Task.Run(() =>
                {
                    SshClient? jumpClient = null;
                    ForwardedPortLocal? port = null;
                    try
                    {
                        AuthenticationMethod[] authMethods = jumpKey != null
                            ? new AuthenticationMethod[] { new PrivateKeyAuthenticationMethod(jumpUser, jumpKey) }
                            : new AuthenticationMethod[] { new PasswordAuthenticationMethod(jumpUser, jumpPassword) };

                        var connInfo = new ConnectionInfo(jumpHost, jumpPort, jumpUser, authMethods) { Timeout = TimeSpan.FromSeconds(8) };
                        jumpClient = new SshClient(connInfo);
                        jumpClient.Connect();

                        int.TryParse(txtPort.Text, out int targetPort);
                        uint localPort = (uint)Random.Shared.Next(40000, 60000);
                        port = new ForwardedPortLocal("127.0.0.1", localPort, targetHost, (uint)targetPort);
                        jumpClient.AddForwardedPort(port);
                        port.Start();

                        using var tcp = new System.Net.Sockets.TcpClient();
                        var result = tcp.BeginConnect("127.0.0.1", (int)localPort, null, null);
                        bool connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                        if (connected && tcp.Connected) { tcp.EndConnect(result); return true; }
                        return false;
                    }
                    catch { return false; }
                    finally
                    {
                        try { port?.Stop(); } catch { }
                        try { jumpClient?.Disconnect(); jumpClient?.Dispose(); } catch { }
                    }
                });

                if (tunnelOk)
                    await ModernMessageBox.ShowAsync($"跳板机可达，且成功通过跳板机连接到 {targetHost}:{txtPort.Text}", "SSH隧道测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    await ModernMessageBox.ShowAsync($"跳板机可达，但无法通过跳板机连接到 {targetHost}:{txtPort.Text}\n\n可能原因：目标主机不可达、或防火墙阻止了转发。", "SSH隧道测试部分成功", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                await ModernMessageBox.ShowAsync($"跳板机 {jumpHost}:{jumpPort} 连接成功！\n\n提示：填写目标主机 IP 和端口后可进一步测试隧道连通性。", "跳板机测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task TestNetProxyAsync()
        {
            string proxyHost = txtProxyHost.Text.Trim();
            if (string.IsNullOrEmpty(proxyHost))
            {
                await ModernMessageBox.ShowAsync("请填写代理服务器地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!int.TryParse(txtProxyPort.Text, out int proxyPort) || proxyPort < 1 || proxyPort > 65535)
            {
                await ModernMessageBox.ShowAsync("代理端口无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool reachable = await Task.Run(() =>
            {
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    var result = tcp.BeginConnect(proxyHost, proxyPort, null, null);
                    bool connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    if (connected && tcp.Connected) { tcp.EndConnect(result); return true; }
                    return false;
                }
                catch { return false; }
            });

            if (!reachable)
            {
                await ModernMessageBox.ShowAsync($"无法连接到代理服务器 {proxyHost}:{proxyPort}\n\n请检查代理地址和端口是否正确，以及代理服务是否正在运行。", "代理测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string targetHost = txtHost.Text.Trim();
            int proxyTypeIndex = cmbProxyType.SelectedIndex;
            string proxyUsername = txtProxyUsername.Text.Trim();
            string proxyPassword = txtProxyPassword.Password;

            if (!string.IsNullOrEmpty(targetHost) && int.TryParse(txtPort.Text, out int targetPort))
            {
                bool tunnelOk = await Task.Run(() =>
                {
                    try
                    {
                        ProxyTypes sshProxyType = proxyTypeIndex switch
                        {
                            0 => ProxyTypes.Http,
                            1 => ProxyTypes.Socks4,
                            2 => ProxyTypes.Socks5,
                            _ => ProxyTypes.None
                        };

                        var connInfo = new ConnectionInfo(
                            targetHost, targetPort, "__proxy_test__",
                            sshProxyType, proxyHost, proxyPort,
                            proxyUsername, proxyPassword,
                            new NoneAuthenticationMethod("__proxy_test__")) { Timeout = TimeSpan.FromSeconds(8) };

                        using var client = new SshClient(connInfo);
                        try { client.Connect(); client.Disconnect(); }
                        catch (Renci.SshNet.Common.SshAuthenticationException) { return true; }
                        catch (Renci.SshNet.Common.SshConnectionException ex) when (ex.Message.Contains("denied") || ex.Message.Contains("auth")) { return true; }
                        return true;
                    }
                    catch { return false; }
                });

                if (tunnelOk)
                    await ModernMessageBox.ShowAsync($"代理服务器可达，且成功通过代理连接到 {targetHost}:{targetPort}", "代理测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    await ModernMessageBox.ShowAsync($"代理服务器可达，但无法通过代理连接到 {targetHost}:{targetPort}\n\n可能原因：代理类型选择错误、代理认证失败、或远程主机不可达。", "代理测试部分成功", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                await ModernMessageBox.ShowAsync($"代理服务器 {proxyHost}:{proxyPort} 可达！\n\n提示：填写目标主机 IP 和端口后可进一步测试代理隧道连通性。", "代理测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
