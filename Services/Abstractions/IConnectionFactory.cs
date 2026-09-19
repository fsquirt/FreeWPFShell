using FreeWPFShell.Models;
using Renci.SshNet;

namespace FreeWPFShell.Services.Abstractions
{

    public interface IConnectionFactory
    {

        SshClient BuildSshClient(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort);


        SftpClient BuildSftpClient(SshConnectionInfo info, PrivateKeyFile? preloadedKey, ForwardedPortLocal? jumpPort);


        SshClient BuildJumpClient(SshConnectionInfo info, PrivateKeyFile? jumpKey);
    }
}
