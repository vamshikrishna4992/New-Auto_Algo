using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.Threading.Channels;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;
using UpstoxTrader.Infrastructure.DependencyInjection;
using UpstoxTrader.Strategy;
using UpstoxTrader.Worker.Workers;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}")
    .WriteTo.File("logs/upstox-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}")
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .CreateLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            var config = context.Configuration;

            // ── Settings ────────────────────────────────────────────────
            services.Configure<UpstoxSettings>(config.GetSection("Upstox"));
            services.Configure<TradingSettings>(config.GetSection("Trading"));
            services.Configure<NiftySettings>(config.GetSection("Nifty"));

            // ── Core singletons ─────────────────────────────────────────
            services.AddSingleton<ORBState>();
            services.AddSingleton<BreakoutDetector>();
            services.AddSingleton<ORBCandleBuilder>();
            services.AddSingleton<ExitEvaluator>();

            // ── Infrastructure ──────────────────────────────────────────
            services.AddSingleton<TokenManager>();
            services.AddInfrastructure(config);

            // ── Keyed tick channels ─────────────────────────────────────
            services.AddKeyedSingleton<Channel<TickData>>("nifty-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000)
                    { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("breakout-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000)
                    { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("option-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000)
                    { FullMode = BoundedChannelFullMode.DropOldest }));

            // ── Auth (OAuth flow at startup) ─────────────────────────────
            services.AddHostedService<TokenService>();

            // ── Workers ─────────────────────────────────────────────────
            // MarketDataWorker registered as singleton so BreakoutWorker
            // can call SubscribeToOptionAsync on it directly.
            services.AddSingleton<MarketDataWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<MarketDataWorker>());
            services.AddHostedService<BreakoutWorker>();
            services.AddHostedService<PositionMonitorWorker>();
            services.AddHostedService<DailyResetWorker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
