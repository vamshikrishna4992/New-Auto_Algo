using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.Threading.Channels;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;
using UpstoxTrader.Infrastructure.DependencyInjection;
using UpstoxTrader.Infrastructure.Services;
using UpstoxTrader.Strategy;
using UpstoxTrader.Worker.Workers;

const string logTemplate =
    "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}";

static bool IsVolEvent(Serilog.Events.LogEvent e) =>
    e.MessageTemplate.Text.StartsWith("[VOL]") ||
    (e.Properties.TryGetValue("SourceContext", out var sc) &&
     (sc.ToString().Contains("Volume") || sc.ToString().Contains("FuturesCandle")));

Log.Logger = new LoggerConfiguration()
    // Console — all strategies combined
    .WriteTo.Console(outputTemplate: logTemplate)
    // Combined file — everything
    .WriteTo.File("logs/combined-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: logTemplate)
    // Strategy 1 file — ORB (exclude [VOL] lines and Volume source contexts)
    .WriteTo.Logger(lc => lc
        .Filter.ByExcluding(IsVolEvent)
        .WriteTo.File("logs/strategy1-orb-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: logTemplate))
    // Strategy 2 file — Volume Breakout (only [VOL] lines and Volume source contexts)
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(IsVolEvent)
        .WriteTo.File("logs/strategy2-volume-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: logTemplate))
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .CreateLogger();

OpenLogWindows();

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
            services.Configure<VolumeStrategySettings>(config.GetSection("VolumeStrategy"));

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

            services.AddKeyedSingleton<Channel<TickData>>("volume-option-channel",
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

            // ── Volume Strategy (Strategy 2) ─────────────────────────────
            services.AddSingleton<VolumeBreakoutState>();
            services.AddSingleton<FuturesCandleService>();
            services.AddHostedService<VolumeBreakoutWorker>();
            services.AddHostedService<VolumePositionMonitorWorker>();
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

static void OpenLogWindows()
{
    var dir = Directory.GetCurrentDirectory();
    LaunchScript(Path.Combine(dir, "Watch-ORB.ps1"));
    LaunchScript(Path.Combine(dir, "Watch-Volume.ps1"));

    static void LaunchScript(string script)
    {
        if (!File.Exists(script)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoExit -File \"{script}\"",
                UseShellExecute = true
            });
        }
        catch { /* silently ignore if terminal can't open */ }
    }
}
