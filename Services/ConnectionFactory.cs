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
    /// <summary>
    /// SSH/SFTP 客户端工厂（单一职责）。
    /// 集中封装认证方式、代理类型、跳板机转发端口等连接参数构建逻辑，
    /// 避免连接编排代码中散落大量配置解析。
    /// </summary>
    public class ConnectionFactory : IConnectionFactory
    {
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

            // 跳板机模式：通过本地转发端口连接目标主机，不使用 SSH.NET 的 Proxy 机制
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
