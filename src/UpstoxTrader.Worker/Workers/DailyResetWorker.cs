using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Strategy;

namespace UpstoxTrader.Worker.Workers;

public class DailyResetWorker : BackgroundService
{
    private readonly ORBState _state;
    private readonly ORBCandleBuilder _candleBuilder;
    private readonly BreakoutDetector _breakoutDetector;
    private readonly ILogger<DailyResetWorker> _logger;

    public DailyResetWorker(
        ORBState state,
        ORBCandleBuilder candleBuilder,
        BreakoutDetector breakoutDetector,
        ILogger<DailyResetWorker> logger)
    {
        _state = state;
        _candleBuilder = candleBuilder;
        _breakoutDetector = breakoutDetector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyResetWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = TimeUntilNextReset();
                _logger.LogInformation("Next daily reset in {Minutes:F1} minutes", delay.TotalMinutes);

                await Task.Delay(delay, stoppingToken);

                _state.Reset();
                _candleBuilder.Reset();
                _breakoutDetector.Reset();
                _state.Log("Daily auto-reset complete");
                _logger.LogInformation("Daily reset executed at 09:14 IST");

                // Wait 24h before next reset calculation
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyResetWorker error");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private static TimeSpan TimeUntilNextReset()
    {
        var ist = ORBState.GetIST();
        var today = DateOnly.FromDateTime(ist);
        var resetToday = new DateTime(today.Year, today.Month, today.Day, 9, 14, 0);

        // If reset time has already passed today, schedule for tomorrow
        if (ist >= resetToday)
            resetToday = resetToday.AddDays(1);

        var istZone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
        var resetUtc = TimeZoneInfo.ConvertTimeToUtc(resetToday, istZone);

        var delay = resetUtc - DateTime.UtcNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
