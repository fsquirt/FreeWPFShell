using System;
using Microsoft.Extensions.DependencyInjection;

namespace YouShell.Core
{
    /// <summary>
    /// 应用级服务容器（服务定位器）。
    /// 渐进式重构的入口：所有跨模块共享的单例服务（IpGeo、设置、隧道管理器等）
    /// 统一在此注册，避免在 View/Service 中散落 new。
    /// </summary>
    public static class AppServices
    {
        private static ServiceProvider? _provider;

        /// <summary>初始化容器，注册所有单例服务。应用启动时调用一次。</summary>
        public static void Initialize(Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            configure?.Invoke(services);
            _provider = services.BuildServiceProvider();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // 基础设施单例
            services.AddSingleton<Repositories.SettingsRepository>();
            services.AddSingleton<Repositories.HostRepository>();
            services.AddSingleton<Repositories.KeyRepository>();
            services.AddSingleton<Share.IpGeoService>(_ => Share.IpGeoService.Instance);
            services.AddSingleton<Share.SshTunnelManager>(_ => Share.SshTunnelManager.Instance);
        }

        /// <summary>解析一个服务。</summary>
        public static T GetService<T>() where T : notnull
        {
            if (_provider == null)
                throw new InvalidOperationException("AppServices 尚未初始化，请在 App 启动时调用 AppServices.Initialize()。");
            return _provider.GetRequiredService<T>();
        }

        /// <summary>应用退出时释放容器。</summary>
        public static void Shutdown()
        {
            _provider?.Dispose();
            _provider = null;
        }
    }
}
