using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using FreeWPFShell.Models;
using FreeWPFShell.Services.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FreeWPFShell.Services
{

    public class ConnectionFactory : IConnectionFactory
    {

        private static readonly TimeSpan SftpKeepAliveInterval = TimeSpan.FromSeconds(2);

        public SshClient BuildSshClient(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort)
        {
            var client = new SshClient(BuildConnectionInfo(info, preloadedKey, jumpPort));
            client.ErrorOccurred += OnClientError;
            return client;
        }

        public SftpClient BuildSftpClient(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort)
        {
            var client = new SftpClient(BuildConnectionInfo(info, preloadedKey, jumpPort));
            client.ErrorOccurred += OnClientError;

            client.KeepAliveInterval = SftpKeepAliveInterval;
            return client;
        }

        public SshClient BuildJumpClient(SshConnectionInfo info, PrivateKeyFile? jumpKey)
        {
            if (info.Proxy == null || info.Proxy.Type != ProxyType.Ssh)
                throw new Exception("跳板机配置无效。");

            var authMethods = new List<AuthenticationMethod>(1);
            if (jumpKey != null)
                authMethods.Add(new PrivateKeyAuthenticationMethod(info.Proxy.Username, jumpKey));
            else
                authMethods.Add(new PasswordAuthenticationMethod(info.Proxy.Username, info.Proxy.Password));

            var connInfo = new ConnectionInfo(
                info.Proxy.ServerAddress, info.Proxy.Port, info.Proxy.Username,
                authMethods.ToArray())
            {
                Encoding = Encoding.UTF8,
                Timeout = TimeSpan.FromSeconds(15)
            };

            var client = new SshClient(connInfo);
            client.ErrorOccurred += OnClientError;
            return client;
        }

        private static ConnectionInfo BuildConnectionInfo(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort)
        {
            var authMethods = new List<AuthenticationMethod>(2);

            if (info.AuthMethod == SshAuthMethod.Password)
            {
                authMethods.Add(new PasswordAuthenticationMethod(
                    info.SshUser, info.DecryptedSshSecret ?? ""));
            }
            else
            {
                if (preloadedKey == null)
                    throw new Exception("密钥未预加载，请确保在连接前已加载密钥。");
                authMethods.Add(new PrivateKeyAuthenticationMethod(info.SshUser, preloadedKey));
            }


            if (info.UseProxy && info.Proxy?.Type == ProxyType.Ssh && jumpPort != null)
            {
                var conn = new ConnectionInfo(
                    "127.0.0.1", (int)jumpPort.BoundPort, info.SshUser,
                    authMethods.ToArray())
                {
                    Encoding = Encoding.UTF8,
                    Timeout = TimeSpan.FromSeconds(30)
                };
                return conn;
            }

            if (info.UseProxy && info.Proxy != null)
            {
                ProxyTypes proxyType = info.Proxy.Type switch
                {
                    ProxyType.Http => ProxyTypes.Http,
                    ProxyType.Socks4 => ProxyTypes.Socks4,
                    ProxyType.Socks5 => ProxyTypes.Socks5,
                    _ => ProxyTypes.None
                };
                return new ConnectionInfo(
                    info.IpAddress, info.SshPort, info.SshUser,
                    proxyType, info.Proxy.ServerAddress, info.Proxy.Port,
                    info.Proxy.Username, info.Proxy.Password,
                    authMethods.ToArray());
            }

            return new ConnectionInfo(
                info.IpAddress, info.SshPort, info.SshUser,
                authMethods.ToArray())
            {
                Encoding = Encoding.UTF8
            };
        }

        private static void OnClientError(object? sender, ExceptionEventArgs e)
        {
            try { Debug.WriteLine($"[SshClient Error] {e.Exception?.Message}"); } catch { }
        }
    }
}
