using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;
using UpstoxTrader.Infrastructure.Http;
using UpstoxTrader.Infrastructure.Services;

namespace UpstoxTrader.Infrastructure.DependencyInjection;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<UpstoxSettings>(config.GetSection("Upstox"));
        services.Configure<TradingSettings>(config.GetSection("Trading"));
        services.Configure<NiftySettings>(config.GetSection("Nifty"));

        // TokenManager is registered by the host; TokenService is registered as hosted service by host
        services.AddSingleton<IMarketFeedService, UpstoxLtpPollingService>();
        services.AddSingleton<IOptionChainService, UpstoxOptionChainService>();
        services.AddSingleton<IOrderService, UpstoxOrderService>();

        services.AddHttpClient<UpstoxHttpClient>();

        return services;
    }
}
