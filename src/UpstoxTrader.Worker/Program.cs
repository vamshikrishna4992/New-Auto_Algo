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

// Which strategy to run — default is "all"
// Usage: dotnet run -- orb | volume | premium | all
var strategy = args.FirstOrDefault()?.ToLower() ?? "all";
bool runOrb     = strategy is "all" or "orb";
bool runVolume  = strategy is "all" or "volume";
bool runPremium = strategy is "all" or "premium";

// ── Logging ──────────────────────────────────────────────────────────────────

const string logTemplate =
    "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}";

static bool IsVolEvent(Serilog.Events.LogEvent e) =>
    e.MessageTemplate.Text.StartsWith("[VOL]") ||
    (e.Properties.TryGetValue("SourceContext", out var sc) &&
     (sc.ToString().Contains("Volume") || sc.ToString().Contains("FuturesCandle")));

static bool IsPshEvent(Serilog.Events.LogEvent e) =>
    e.MessageTemplate.Text.StartsWith("[PSH]") ||
    (e.Properties.TryGetValue("SourceContext", out var sc2) &&
     sc2.ToString().Contains("PremiumStopHunt"));

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: logTemplate)
    .WriteTo.File("logs/combined-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: logTemplate)
    .WriteTo.Logger(lc => lc
        .Filter.ByExcluding(e => IsVolEvent(e) || IsPshEvent(e))
        .WriteTo.File("logs/strategy1-orb-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: logTemplate))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(IsVolEvent)
        .WriteTo.File("logs/strategy2-volume-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: logTemplate))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(IsPshEvent)
        .WriteTo.File("logs/strategy3-premium-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: logTemplate))
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .CreateLogger();

Log.Information("Starting with strategy: {Strategy}", strategy.ToUpper());
OpenLogWindows(runOrb, runVolume, runPremium);

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            var config = context.Configuration;

            // ── Settings ─────────────────────────────────────────────────
            services.Configure<UpstoxSettings>(config.GetSection("Upstox"));
            services.Configure<TradingSettings>(config.GetSection("Trading"));
            services.Configure<NiftySettings>(config.GetSection("Nifty"));
            services.Configure<VolumeStrategySettings>(config.GetSection("VolumeStrategy"));

            // ── Shared infrastructure ─────────────────────────────────────
            services.AddSingleton<TokenManager>();
            services.AddInfrastructure(config);

            // ── Channels — always register all so MarketDataWorker resolves ─
            services.AddKeyedSingleton<Channel<TickData>>("nifty-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("breakout-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("option-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("volume-option-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("premium-nifty-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest }));

            services.AddKeyedSingleton<Channel<TickData>>("premium-option-channel",
                (_, _) => Channel.CreateBounded<TickData>(
                    new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest }));

            // ── Shared state (always registered — needed by DailyResetWorker) ─
            services.AddSingleton<ORBState>();
            services.AddSingleton<BreakoutDetector>();
            services.AddSingleton<ORBCandleBuilder>();
            services.AddSingleton<ExitEvaluator>();
            services.AddSingleton<VolumeBreakoutState>();
            services.AddSingleton<FuturesCandleService>();
            services.AddSingleton<PremiumStopHuntState>();
            services.Configure<PremiumStopHuntSettings>(config.GetSection("PremiumStopHunt"));

            // ── Auth ──────────────────────────────────────────────────────
            services.AddHostedService<TokenService>();

            // ── Shared workers ────────────────────────────────────────────
            services.AddSingleton<MarketDataWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<MarketDataWorker>());
            services.AddHostedService<DailyResetWorker>();

            // ── Strategy 1: ORB ───────────────────────────────────────────
            if (runOrb)
            {
                Log.Information("Strategy 1 (ORB) enabled");
                services.AddHostedService<BreakoutWorker>();
                services.AddHostedService<PositionMonitorWorker>();
            }

            // ── Strategy 2: Volume Breakout ───────────────────────────────
            if (runVolume)
            {
                Log.Information("Strategy 2 (Volume Breakout) enabled");
                services.AddHostedService<VolumeBreakoutWorker>();
                services.AddHostedService<VolumePositionMonitorWorker>();
            }

            // ── Strategy 3: Premium Stop Hunt ─────────────────────────────
            if (runPremium)
            {
                Log.Information("Strategy 3 (Premium Stop Hunt) enabled");
                services.AddHostedService<PremiumStopHuntWorker>();
            }
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

static void OpenLogWindows(bool orb, bool volume, bool premium)
{
    var dir = Directory.GetCurrentDirectory();
    if (orb)     LaunchScript(Path.Combine(dir, "Watch-ORB.ps1"));
    if (volume)  LaunchScript(Path.Combine(dir, "Watch-Volume.ps1"));
    if (premium) LaunchScript(Path.Combine(dir, "Watch-Premium.ps1"));

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
        catch { }
    }
}
