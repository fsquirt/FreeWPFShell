using FreeWPFShell.Models;
using Renci.SshNet;

namespace FreeWPFShell.Services.Abstractions
{

    public interface ITunnelService : IDisposable
    {

        void RegisterTunnel(SshTunnelInfo tunnel);


        void CleanupTunnels();
    }
}
