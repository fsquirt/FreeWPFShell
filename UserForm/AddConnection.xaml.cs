using System;
using System.Windows;
using FreeWPFShell.Models;
using FreeWPFShell.Repositories;

namespace FreeWPFShell.UserForm
{
    public partial class AddConnection 
    {
        private readonly HostRepository _hostRepo;
        private string? _editingHostId;

        public bool ConnectAfterSave { get; private set; }
        public SshConnectionInfo? SavedHostInfo { get; private set; }

        public AddConnection()
        {
            InitializeComponent();
            _hostRepo = new HostRepository(new SettingsRepository());
        }

        public AddConnection(SshConnectionInfo editHost) : this()
        {
            _editingHostId = editHost.Id;
            Title = "编辑连接";
            txtHostName.Text = editHost.HostName;
            txtHost.Text = editHost.IpAddress;
            txtPort.Text = editHost.SshPort.ToString();
            txtUsername.Text = editHost.SshUser;
            if (editHost.AuthMethod == SshAuthMethod.PrivateKey) rbKey.IsChecked = true;
            if (editHost.UseProxy && editHost.Proxy != null)
            {
                chkProxy.IsChecked = true;
                if (editHost.Proxy.Type == ProxyType.Http) cmbProxyType.SelectedIndex = 0;
                else if (editHost.Proxy.Type == ProxyType.Socks5) cmbProxyType.SelectedIndex = 2;
                txtProxyHost.Text = editHost.Proxy.ServerAddress;
                txtProxyPort.Text = editHost.Proxy.Port.ToString();
                txtProxyUsername.Text = editHost.Proxy.Username;
                txtProxyPassword.Text = editHost.Proxy.Password;
            }
        }

        private void BtnBrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "密钥文件|*.*", Title = "选择SSH密钥文件" };
            if (dlg.ShowDialog() == true) txtKeyPath.Text = dlg.FileName;
        }

        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(txtHost.Text)) { ModernMessageBox.Show("请输入主机IP地址。", "提示"); txtHost.Focus(); return false; }
            if (string.IsNullOrWhiteSpace(txtUsername.Text)) { ModernMessageBox.Show("请输入用户名。", "提示"); txtUsername.Focus(); return false; }
            if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535) { ModernMessageBox.Show("端口号无效。", "提示"); txtPort.Focus(); return false; }
            if (string.IsNullOrEmpty(_editingHostId))
            {
                if (rbPassword.IsChecked == true && string.IsNullOrEmpty(txtPassword.Text)) { ModernMessageBox.Show("请输入密码。", "提示"); txtPassword.Focus(); return false; }
                if (rbKey.IsChecked == true && string.IsNullOrWhiteSpace(txtKeyPath.Text)) { ModernMessageBox.Show("请选择密钥文件。", "提示"); return false; }
            }
            return true;
        }

        private SshConnectionInfo BuildHostInfo()
        {
            var host = new SshConnectionInfo
            {
                HostName = string.IsNullOrWhiteSpace(txtHostName.Text) ? txtHost.Text : txtHostName.Text,
                IpAddress = txtHost.Text.Trim(), SshPort = int.Parse(txtPort.Text),
                SshUser = txtUsername.Text.Trim(),
                AuthMethod = rbPassword.IsChecked == true ? SshAuthMethod.Password : SshAuthMethod.PrivateKey,
                UseProxy = chkProxy.IsChecked == true
            };
            if (host.UseProxy)
            {
                var pt = ProxyType.None;
                if (cmbProxyType.SelectedIndex == 0) pt = ProxyType.Http;
                else if (cmbProxyType.SelectedIndex == 2) pt = ProxyType.Socks5;
                host.Proxy = new ProxyInfo { Type = pt, ServerAddress = txtProxyHost.Text.Trim(), Port = int.TryParse(txtProxyPort.Text, out int pp) ? pp : 1080, Username = txtProxyUsername.Text.Trim(), Password = txtProxyPassword.Text };
            }
            return host;
        }

        private string GetSecret() => rbPassword.IsChecked == true ? txtPassword.Text : txtKeyPath.Text;

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
            catch (Exception ex) { ModernMessageBox.Show("保存失败: " + ex.Message, "错误"); }
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
            catch (Exception ex) { ModernMessageBox.Show("保存失败: " + ex.Message, "错误"); }
        }

        private void chkProxy_Checked(object sender, RoutedEventArgs e) { if (pnlProxy != null) pnlProxy.IsEnabled = chkProxy.IsChecked == true; }
    }
}
