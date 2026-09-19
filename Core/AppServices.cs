using System;
using Microsoft.Extensions.DependencyInjection;

namespace FreeWPFShell.Core
{

    public static class AppServices
    {
        private static ServiceProvider? _provider;


        public static void Initialize(Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            configure?.Invoke(services);
            _provider = services.BuildServiceProvider();
        }

        private static void ConfigureServices(IServiceCollection services)
        {

            services.AddSingleton<Repositories.SettingsRepository>();
            services.AddSingleton<Repositories.HostRepository>();
            services.AddSingleton<Repositories.KeyRepository>();
            services.AddSingleton<Share.IpGeoService>(_ => Share.IpGeoService.Instance);
            services.AddSingleton<Share.SshTunnelManager>(_ => Share.SshTunnelManager.Instance);
        }


        public static T GetService<T>() where T : notnull
        {
            if (_provider == null)
                throw new InvalidOperationException("AppServices 尚未初始化，请在 App 启动时调用 AppServices.Initialize()。");
            return _provider.GetRequiredService<T>();
        }


        public static void Shutdown()
        {
            _provider?.Dispose();
            _provider = null;
        }
    }
}
