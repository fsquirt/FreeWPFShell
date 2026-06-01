using FreeWPFShell.Models;
using FreeWPFShell.Repositories;
using FreeWPFShell.Services;
using Renci.SshNet;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace FreeWPFShell.UserForm
{
    public partial class AddConnection
    {
        private readonly HostRepository _hostRepo;
        private readonly KeyRepository _keyRepo = new();
        private string? _editingHostId;

        public bool ConnectAfterSave { get; private set; }
        public SshConnectionInfo? SavedHostInfo { get; private set; }

        public AddConnection()
        {
            InitializeComponent();

            var settingsRepo = new Repositories.SettingsRepository();
            BackdropService.ApplyToAllWindows(settingsRepo.Load().BackdropType);

            _hostRepo = new HostRepository(new SettingsRepository());
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

        public AddConnection(SshConnectionInfo editHost) : this()
        {
            _editingHostId = editHost.Id;
            Title = "编辑连接";
            txtHostName.Text = editHost.HostName;
            txtHost.Text = editHost.IpAddress;
            txtPort.Text = editHost.SshPort.ToString();
            txtUsername.Text = editHost.SshUser;
            if (editHost.AuthMethod == SshAuthMethod.PrivateKey)
            {
                rbKey.IsChecked = true;
                // 选中对应的密钥
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
                chkProxy.IsChecked = true;
                if (editHost.Proxy.Type == ProxyType.Http) cmbProxyType.SelectedIndex = 0;
                else if (editHost.Proxy.Type == ProxyType.Socks4) cmbProxyType.SelectedIndex = 1;
                else if (editHost.Proxy.Type == ProxyType.Socks5) cmbProxyType.SelectedIndex = 2;
                else if (editHost.Proxy.Type == ProxyType.Ssh) cmbProxyType.SelectedIndex = 3;

                if (editHost.Proxy.Type == ProxyType.Ssh)
                {
                    txtJumpHost.Text = editHost.Proxy.ServerAddress;
                    txtJumpPort.Text = editHost.Proxy.Port.ToString();
                    txtJumpUser.Text = editHost.Proxy.Username;
                    if (!string.IsNullOrEmpty(editHost.Proxy.SshKeyId))
                    {
                        rbJumpKey.IsChecked = true;
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
                        rbJumpPassword.IsChecked = true;
                        txtJumpPassword.Text = editHost.Proxy.Password;
                    }
                }
                else
                {
                    txtProxyHost.Text = editHost.Proxy.ServerAddress;
                    txtProxyPort.Text = editHost.Proxy.Port.ToString();
                    txtProxyUsername.Text = editHost.Proxy.Username;
                    txtProxyPassword.Text = editHost.Proxy.Password;
                }
            }
        }



        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(txtHost.Text)) { ModernMessageBox.Show("请输入主机IP地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); txtHost.Focus(); return false; }
            if (string.IsNullOrWhiteSpace(txtUsername.Text)) { ModernMessageBox.Show("请输入用户名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); txtUsername.Focus(); return false; }
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535) { ModernMessageBox.Show("端口号无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); txtPort.Focus(); return false; }
            if (string.IsNullOrEmpty(_editingHostId))
            {
                if (rbPassword.IsChecked == true && string.IsNullOrEmpty(txtPassword.Text)) { ModernMessageBox.Show("请输入密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); txtPassword.Focus(); return false; }
            }
            if (rbKey.IsChecked == true && cmbKeySelect.SelectedItem == null) { ModernMessageBox.Show("请选择一个已导入的 SSH 密钥。\n\n请先在主界面的「密钥管理」中导入密钥。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return false; }

            // SSH 隧道代理验证
            if (chkProxy.IsChecked == true && cmbProxyType.SelectedIndex == 3)
            {
                if (string.IsNullOrWhiteSpace(txtJumpHost.Text)) { ModernMessageBox.Show("请输入跳板机IP地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); txtJumpHost.Focus(); return false; }
                if (!int.TryParse(txtJumpPort.Text, out int jp) || jp < 1 || jp > 65535) { ModernMessageBox.Show("跳板机端口号无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); txtJumpPort.Focus(); return false; }
                if (string.IsNullOrWhiteSpace(txtJumpUser.Text)) { ModernMessageBox.Show("请输入跳板机用户名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); txtJumpUser.Focus(); return false; }
                if (rbJumpPassword.IsChecked == true && string.IsNullOrEmpty(_editingHostId) && string.IsNullOrEmpty(txtJumpPassword.Text)) { ModernMessageBox.Show("请输入跳板机密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); txtJumpPassword.Focus(); return false; }
                if (rbJumpKey.IsChecked == true && cmbJumpKeySelect.SelectedItem == null) { ModernMessageBox.Show("请选择跳板机使用的 SSH 密钥。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
            }
            return true;
        }

        private SshConnectionInfo BuildHostInfo()
        {
            var host = new SshConnectionInfo
            {
                HostName = string.IsNullOrWhiteSpace(txtHostName.Text) ? txtHost.Text : txtHostName.Text,
                IpAddress = txtHost.Text.Trim(),
                SshPort = int.Parse(txtPort.Text),
                SshUser = txtUsername.Text.Trim(),
                AuthMethod = rbPassword.IsChecked == true ? SshAuthMethod.Password : SshAuthMethod.PrivateKey,
                UseProxy = chkProxy.IsChecked == true
            };
            // 密钥登录：保存选中的 Key ID
            if (host.AuthMethod == SshAuthMethod.PrivateKey && cmbKeySelect.SelectedItem is SshKeyInfo selectedKey)
            {
                host.SshKeyId = selectedKey.Id;
            }
            if (host.UseProxy)
            {
                var pt = ProxyType.None;
                if (cmbProxyType.SelectedIndex == 0) pt = ProxyType.Http;
                else if (cmbProxyType.SelectedIndex == 1) pt = ProxyType.Socks4;
                else if (cmbProxyType.SelectedIndex == 2) pt = ProxyType.Socks5;
                else if (cmbProxyType.SelectedIndex == 3) pt = ProxyType.Ssh;

                if (pt == ProxyType.Ssh)
                {
                    host.Proxy = new ProxyInfo
                    {
                        Type = pt,
                        ServerAddress = txtJumpHost.Text.Trim(),
                        Port = int.TryParse(txtJumpPort.Text, out int jp) ? jp : 22,
                        Username = txtJumpUser.Text.Trim(),
                        Password = rbJumpPassword.IsChecked == true ? txtJumpPassword.Text : "",
                        SshKeyId = rbJumpKey.IsChecked == true && cmbJumpKeySelect.SelectedItem is SshKeyInfo jumpKey ? jumpKey.Id : null
                    };
                }
                else
                {
                    host.Proxy = new ProxyInfo { Type = pt, ServerAddress = txtProxyHost.Text.Trim(), Port = int.TryParse(txtProxyPort.Text, out int pp) ? pp : 1080, Username = txtProxyUsername.Text.Trim(), Password = txtProxyPassword.Text };
                }
            }
            return host;
        }

        /// <summary>密码模式返回密码，密钥模式返回空（密钥由 KeyRepository 管理）</summary>
        private string GetSecret() => rbPassword.IsChecked == true ? txtPassword.Text : "";

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;
            try
            {
                var host = BuildHostInfo(); string secret = GetSecret();
                if (string.IsNullOrEmpty(_editingHostId)) await _hostRepo.AddAsync(host, secret);
                else await _hostRepo.UpdateAsync(_editingHostId, host, string.IsNullOrEmpty(secret) ? null : secret);
                ConnectAfterSave = false; DialogResult = true;
            }
            catch (Exception ex) { ModernMessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;
            try
            {
                var host = BuildHostInfo(); string secret = GetSecret();
                if (string.IsNullOrEmpty(_editingHostId))
                {
                    await _hostRepo.AddAsync(host, secret);
                    host.DecryptedSshSecret = secret;
                }
                else
                {
                    await _hostRepo.UpdateAsync(_editingHostId, host, string.IsNullOrEmpty(secret) ? null : secret);
                    if (string.IsNullOrEmpty(secret))
                    {
                        var decrypted = await _hostRepo.GetAndDecryptAsync(_editingHostId);
                        host.DecryptedSshSecret = decrypted.DecryptedSshSecret;
                    }
                    else
                        host.DecryptedSshSecret = secret;
                }
                SavedHostInfo = host; ConnectAfterSave = true; DialogResult = true;
            }
            catch (Exception ex) { ModernMessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void chkProxy_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlProxy != null) pnlProxy.IsEnabled = chkProxy.IsChecked == true;
            // 切换代理面板可见性
            if (pnlNetProxy != null && pnlSshProxy != null)
            {
                bool isSsh = chkProxy.IsChecked == true && cmbProxyType.SelectedIndex == 3;
                pnlNetProxy.Visibility = isSsh ? Visibility.Collapsed : Visibility.Visible;
                pnlSshProxy.Visibility = isSsh ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void CmbProxyType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (pnlNetProxy == null || pnlSshProxy == null) return;
            bool isSsh = cmbProxyType.SelectedIndex == 3;
            pnlNetProxy.Visibility = isSsh ? Visibility.Collapsed : Visibility.Visible;
            pnlSshProxy.Visibility = isSsh ? Visibility.Visible : Visibility.Collapsed;
        }

        private void rbPassword_Checked(object sender, RoutedEventArgs e) { if (rbKey != null) rbKey.IsChecked = false; }

        private void rbKey_Checked(object sender, RoutedEventArgs e) { if (rbPassword != null) rbPassword.IsChecked = false; }

        private void rbJumpPassword_Checked(object sender, RoutedEventArgs e)
        {
            if (rbJumpKey != null) rbJumpKey.IsChecked = false;
            if (pnlJumpPassword != null) pnlJumpPassword.Visibility = Visibility.Visible;
            if (pnlJumpKey != null) pnlJumpKey.Visibility = Visibility.Collapsed;
        }

        private void rbJumpKey_Checked(object sender, RoutedEventArgs e)
        {
            if (rbJumpPassword != null) rbJumpPassword.IsChecked = false;
            if (pnlJumpPassword != null) pnlJumpPassword.Visibility = Visibility.Collapsed;
            if (pnlJumpKey != null) pnlJumpKey.Visibility = Visibility.Visible;
        }

        private async void BtnTestProxy_Click(object sender, RoutedEventArgs e)
        {
            if (chkProxy.IsChecked != true)
            {
                ModernMessageBox.Show("请先启用代理设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnTestProxy.IsEnabled = false;
            IconTestProxy.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.Loader2Line;
            TxtTestProxy.Text = "测试中...";

            try
            {
                // SSH 隧道代理测试
                if (cmbProxyType.SelectedIndex == 3)
                {
                    await TestSshProxyAsync();
                }
                else
                {
                    await TestNetProxyAsync();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"测试失败: {ex.Message}", "代理测试错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IconTestProxy.Kind = MahApps.Metro.IconPacks.PackIconRemixIconKind.PlugLine;
                TxtTestProxy.Text = "测试代理";
                BtnTestProxy.IsEnabled = true;
            }
        }

        private async Task TestSshProxyAsync()
        {
            string jumpHost = txtJumpHost.Text.Trim();
            if (string.IsNullOrEmpty(jumpHost))
            {
                ModernMessageBox.Show("请填写跳板机地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!int.TryParse(txtJumpPort.Text, out int jumpPort) || jumpPort < 1 || jumpPort > 65535)
            {
                ModernMessageBox.Show("跳板机端口无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string jumpUser = txtJumpUser.Text.Trim();
            string jumpPassword = txtJumpPassword.Text;

            // 预加载跳板机密钥
            Renci.SshNet.PrivateKeyFile? jumpKey = null;
            if (rbJumpKey.IsChecked == true && cmbJumpKeySelect.SelectedItem is SshKeyInfo selectedJumpKey)
            {
                jumpKey = await _keyRepo.LoadPrivateKeyFileAsync(selectedJumpKey.Id);
                if (jumpKey == null)
                {
                    ModernMessageBox.Show("无法加载跳板机 SSH 密钥。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            string targetHost = txtHost.Text.Trim();
            bool hasTarget = !string.IsNullOrEmpty(targetHost) && int.TryParse(txtPort.Text, out _);

            bool jumpOk = await Task.Run(() =>
            {
                try
                {
                    Renci.SshNet.AuthenticationMethod[] authMethods = jumpKey != null
                        ? new Renci.SshNet.AuthenticationMethod[] { new Renci.SshNet.PrivateKeyAuthenticationMethod(jumpUser, jumpKey) }
                        : new Renci.SshNet.AuthenticationMethod[] { new Renci.SshNet.PasswordAuthenticationMethod(jumpUser, jumpPassword) };

                    var connInfo = new Renci.SshNet.ConnectionInfo(jumpHost, jumpPort, jumpUser, authMethods);
                    connInfo.Timeout = TimeSpan.FromSeconds(8);

                    using var client = new Renci.SshNet.SshClient(connInfo);
                    client.Connect();
                    client.Disconnect();
                    return true;
                }
                catch { return false; }
            });

            if (!jumpOk)
            {
                ModernMessageBox.Show($"无法连接到跳板机 {jumpHost}:{jumpPort}\n\n请检查跳板机地址、端口及认证信息是否正确。", "跳板机连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (hasTarget)
            {
                // 测试通过跳板机的端口转发连通性
                bool tunnelOk = await Task.Run(() =>
                {
                    Renci.SshNet.SshClient? jumpClient = null;
                    Renci.SshNet.ForwardedPortLocal? port = null;
                    try
                    {
                        Renci.SshNet.AuthenticationMethod[] authMethods = jumpKey != null
                            ? new Renci.SshNet.AuthenticationMethod[] { new Renci.SshNet.PrivateKeyAuthenticationMethod(jumpUser, jumpKey) }
                            : new Renci.SshNet.AuthenticationMethod[] { new Renci.SshNet.PasswordAuthenticationMethod(jumpUser, jumpPassword) };

                        var connInfo = new Renci.SshNet.ConnectionInfo(jumpHost, jumpPort, jumpUser, authMethods);
                        connInfo.Timeout = TimeSpan.FromSeconds(8);

                        jumpClient = new Renci.SshNet.SshClient(connInfo);
                        jumpClient.Connect();

                        int.TryParse(txtPort.Text, out int targetPort);
                        uint localPort = (uint)Random.Shared.Next(40000, 60000);
                        port = new Renci.SshNet.ForwardedPortLocal("127.0.0.1", localPort, targetHost, (uint)targetPort);
                        jumpClient.AddForwardedPort(port);
                        port.Start();

                        // 尝试连接转发端口
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
                    ModernMessageBox.Show($"跳板机可达，且成功通过跳板机连接到 {targetHost}:{txtPort.Text}", "SSH隧道测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    ModernMessageBox.Show($"跳板机可达，但无法通过跳板机连接到 {targetHost}:{txtPort.Text}\n\n可能原因：目标主机不可达、或防火墙阻止了转发。", "SSH隧道测试部分成功", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ModernMessageBox.Show($"跳板机 {jumpHost}:{jumpPort} 连接成功！\n\n提示：填写目标主机 IP 和端口后可进一步测试隧道连通性。", "跳板机测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task TestNetProxyAsync()
        {
            string proxyHost = txtProxyHost.Text.Trim();
            if (string.IsNullOrEmpty(proxyHost))
            {
                ModernMessageBox.Show("请填写代理服务器地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!int.TryParse(txtProxyPort.Text, out int proxyPort) || proxyPort < 1 || proxyPort > 65535)
            {
                ModernMessageBox.Show("代理端口无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                ModernMessageBox.Show($"无法连接到代理服务器 {proxyHost}:{proxyPort}\n\n请检查代理地址和端口是否正确，以及代理服务是否正在运行。", "代理测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string targetHost = txtHost.Text.Trim();
            int proxyTypeIndex = cmbProxyType.SelectedIndex;
            string proxyUsername = txtProxyUsername.Text.Trim();
            string proxyPassword = txtProxyPassword.Text;

            if (!string.IsNullOrEmpty(targetHost) && int.TryParse(txtPort.Text, out int targetPort))
            {
                bool tunnelOk = await Task.Run(() =>
                {
                    try
                    {
                        Renci.SshNet.ProxyTypes sshProxyType = proxyTypeIndex switch
                        {
                            0 => Renci.SshNet.ProxyTypes.Http,
                            1 => Renci.SshNet.ProxyTypes.Socks4,
                            2 => Renci.SshNet.ProxyTypes.Socks5,
                            _ => Renci.SshNet.ProxyTypes.None
                        };

                        var connInfo = new Renci.SshNet.ConnectionInfo(
                            targetHost, targetPort, "__proxy_test__",
                            sshProxyType, proxyHost, proxyPort,
                            proxyUsername, proxyPassword,
                            new Renci.SshNet.NoneAuthenticationMethod("__proxy_test__"));
                        connInfo.Timeout = TimeSpan.FromSeconds(8);

                        using var client = new Renci.SshNet.SshClient(connInfo);
                        try { client.Connect(); client.Disconnect(); }
                        catch (Renci.SshNet.Common.SshAuthenticationException) { return true; }
                        catch (Renci.SshNet.Common.SshConnectionException ex) when (ex.Message.Contains("denied") || ex.Message.Contains("auth")) { return true; }
                        return true;
                    }
                    catch { return false; }
                });

                if (tunnelOk)
                    ModernMessageBox.Show($"代理服务器可达，且成功通过代理连接到 {targetHost}:{targetPort}", "代理测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    ModernMessageBox.Show($"代理服务器可达，但无法通过代理连接到 {targetHost}:{targetPort}\n\n可能原因：代理类型选择错误、代理认证失败、或远程主机不可达。", "代理测试部分成功", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ModernMessageBox.Show($"代理服务器 {proxyHost}:{proxyPort} 可达！\n\n提示：填写目标主机 IP 和端口后可进一步测试代理隧道连通性。", "代理测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
