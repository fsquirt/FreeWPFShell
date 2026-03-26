using System;
using System.Windows;
using FreeWPFShell.Share;

namespace FreeWPFShell.UserForm
{
    public partial class AddConnection 
    {
        private SshManager.SshConnectionManager _sshManager;
        private string _editingHostId;

        public bool ConnectAfterSave { get; private set; }
        public SshManager.SshConnectionInfo SavedHostInfo { get; private set; }

        public AddConnection()
        {
            InitializeComponent();
            _sshManager = new SshManager.SshConnectionManager();
        }

        public AddConnection(SshManager.SshConnectionInfo editHost) : this()
        {
            _editingHostId = editHost.Id;
            Title = "编辑连接";

            txtHostName.Text = editHost.HostName;
            txtHost.Text = editHost.IpAddress;
            txtPort.Text = editHost.SshPort.ToString();
            txtUsername.Text = editHost.SshUser;

            if (editHost.AuthMethod == SshManager.SshAuthMethod.PrivateKey)
            {
                rbKey.IsChecked = true;
                // Note: password/key path is not retrieved for security/simplicity.
                // User must re-enter if editing!
            }

            if (editHost.UseProxy)
            {
                chkProxy.IsChecked = true;
                if (editHost.Proxy != null)
                {
                    if (editHost.Proxy.Type == SshManager.ProxyType.Http) cmbProxyType.SelectedIndex = 0;
                    else if (editHost.Proxy.Type == SshManager.ProxyType.Socks5) cmbProxyType.SelectedIndex = 2;

                    txtProxyHost.Text = editHost.Proxy.ServerAddress;
                    txtProxyPort.Text = editHost.Proxy.Port.ToString();
                    txtProxyUsername.Text = editHost.Proxy.Username;
                    txtProxyPassword.Text = editHost.Proxy.Password;
                }
            }
        }

        private void BtnBrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "密钥文件|*.*",
                Title = "选择SSH密钥文件"
            };
            if (dlg.ShowDialog() == true)
            {
                txtKeyPath.Text = dlg.FileName;
            }
        }

        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(txtHost.Text))
            {
                ModernMessageBox.Show("请输入主机IP地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtHost.Focus();
                return false;
            }
            if (string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                ModernMessageBox.Show("请输入用户名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtUsername.Focus();
                return false;
            }
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
            {
                ModernMessageBox.Show("端口号无效，请输入1-65535之间的数字。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtPort.Focus();
                return false;
            }
            
            // If editing, they might not enter a new password if they want to keep the old one.
            // But SshManager EditHost API needs to know if we are changing it. 
            // Our rule: if editing & password empty, we keep old secret.
            if (string.IsNullOrEmpty(_editingHostId))
            {
                if (rbPassword.IsChecked == true && string.IsNullOrEmpty(txtPassword.Text))
                {
                    ModernMessageBox.Show("请输入密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtPassword.Focus();
                    return false;
                }
                if (rbKey.IsChecked == true && string.IsNullOrWhiteSpace(txtKeyPath.Text))
                {
                    ModernMessageBox.Show("请选择密钥文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            return true;
        }

        private SshManager.SshConnectionInfo BuildHostInfo()
        {
            var host = new SshManager.SshConnectionInfo
            {
                HostName = string.IsNullOrWhiteSpace(txtHostName.Text) ? txtHost.Text : txtHostName.Text,
                IpAddress = txtHost.Text.Trim(),
                SshPort = int.Parse(txtPort.Text),
                SshUser = txtUsername.Text.Trim(),
                AuthMethod = rbPassword.IsChecked == true
                    ? SshManager.SshAuthMethod.Password
                    : SshManager.SshAuthMethod.PrivateKey,
                UseProxy = chkProxy.IsChecked == true
            };

            if (host.UseProxy)
            {
                var proxyType = SshManager.ProxyType.None;
                if (cmbProxyType.SelectedIndex == 0) proxyType = SshManager.ProxyType.Http;
                else if (cmbProxyType.SelectedIndex == 2) proxyType = SshManager.ProxyType.Socks5;

                host.Proxy = new SshManager.ProxyInfo
                {
                    Type = proxyType,
                    ServerAddress = txtProxyHost.Text.Trim(),
                    Port = int.TryParse(txtProxyPort.Text, out int pp) ? pp : 1080,
                    Username = txtProxyUsername.Text.Trim(),
                    Password = txtProxyPassword.Text
                };
            }

            return host;
        }

        private string GetSecret()
        {
            if (rbPassword.IsChecked == true)
                return txtPassword.Text;
            return txtKeyPath.Text; 
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            try
            {
                var host = BuildHostInfo();
                string secret = GetSecret();
                if (string.IsNullOrEmpty(_editingHostId))
                {
                    _sshManager.AddHost(host, secret);
                }
                else
                {
                    _sshManager.EditHost(_editingHostId, host, string.IsNullOrEmpty(secret) ? null : secret);
                }

                ConnectAfterSave = false;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            try
            {
                var host = BuildHostInfo();
                string secret = GetSecret();
                
                if (string.IsNullOrEmpty(_editingHostId))
                {
                    _sshManager.AddHost(host, secret);
                    host.DecryptedSshSecret = secret;
                }
                else
                {
                    _sshManager.EditHost(_editingHostId, host, string.IsNullOrEmpty(secret) ? null : secret);
                    if (string.IsNullOrEmpty(secret))
                    {
                        // Needs to fetch old secret from vault so we can connect
                        var decryptedHost = System.Threading.Tasks.Task.Run(
                            () => _sshManager.GetHostAndDecryptAsync(_editingHostId)
                        ).GetAwaiter().GetResult();
                        host.DecryptedSshSecret = decryptedHost.DecryptedSshSecret;
                    }
                    else
                    {
                         host.DecryptedSshSecret = secret;
                    }
                }

                SavedHostInfo = host;
                ConnectAfterSave = true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show("保存失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void chkProxy_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlProxy != null)
                pnlProxy.IsEnabled = chkProxy.IsChecked == true;
        }
    }
}
