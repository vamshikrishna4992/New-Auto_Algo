using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;

namespace UpstoxTrader.Worker.Workers;

public class VolumePositionMonitorWorker : BackgroundService
{
    private readonly Channel<TickData> _optionChannel;
    private readonly IOrderService _orders;
    private readonly VolumeBreakoutState _state;
    private readonly VolumeStrategySettings _settings;
    private readonly ILogger<VolumePositionMonitorWorker> _logger;

    private bool _entryPriceSet;

    public VolumePositionMonitorWorker(
        [FromKeyedServices("volume-option-channel")] Channel<TickData> optionChannel,
        IOrderService orders,
        VolumeBreakoutState state,
        IOptions<VolumeStrategySettings> settings,
        ILogger<VolumePositionMonitorWorker> logger)
    {
        _optionChannel = optionChannel;
        _orders        = orders;
        _state         = state;
        _settings      = settings.Value;
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[VOL] VolumePositionMonitorWorker started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_state.ActivePosition is null ||
                    _state.ActivePosition.Status != PositionStatus.Open)
                {
                    _entryPriceSet = false;
                    await Task.Delay(200, ct);
                    continue;
                }

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(200);

                try
                {
                    var tick = await _optionChannel.Reader.ReadAsync(readCts.Token);
                    await ProcessTickAsync(tick, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout — still check time-based exit
                    if (!_entryPriceSet) continue;

                    var pos = _state.ActivePosition;
                    if (pos is { Status: PositionStatus.Open })
                    {
                        var exitReason = EvaluateExit(pos);
                        if (exitReason is not null)
                            await ExecuteExitAsync(pos, exitReason, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VOL] VolumePositionMonitorWorker error");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ProcessTickAsync(TickData tick, CancellationToken ct)
    {
        var pos = _state.ActivePosition;
        if (pos is null || pos.Status != PositionStatus.Open) return;

        // Only process ticks for our active option
        if (tick.InstrumentKey != pos.InstrumentKey) return;

        pos.CurrentLtp = tick.Ltp;

        // Set entry price from first tick
        if (!_entryPriceSet && tick.Ltp > 0)
        {
            pos.EntryPrice = tick.Ltp;
            _entryPriceSet = true;

            _logger.LogInformation(
                "[VOL] ENTRY SET → {Symbol} | Entry: {Entry:F2}",
                pos.OptionSymbol, pos.EntryPrice);
            _state.Log($"Entry confirmed | {pos.OptionSymbol} | ₹{pos.EntryPrice:F2}");
        }

        if (_entryPriceSet)
        {
            var pnlPerUnit = pos.CurrentLtp - pos.EntryPrice;
            var totalPnl   = pnlPerUnit * pos.Quantity;

            _logger.LogInformation(
                "[VOL] LTP: {Ltp:F2} | Entry: {Entry:F2} | PnL: {Pts:+0.00;-0.00} pts (₹{Pnl:F2})",
                pos.CurrentLtp, pos.EntryPrice, pnlPerUnit, totalPnl);
        }

        var exitReason = EvaluateExit(pos);
        if (exitReason is not null)
            await ExecuteExitAsync(pos, exitReason, ct);
    }

    private string? EvaluateExit(Position pos)
    {
        if (pos.EntryPrice <= 0 || pos.CurrentLtp <= 0) return null;

        var pts = pos.CurrentLtp - pos.EntryPrice;

        if (pts >= _settings.TakeProfitPoints)  return "Target Hit";
        if (pts <= -_settings.StopLossPoints)   return "Stop Loss Hit";

        var ist = ORBState.GetIST();
        if (TimeSpan.TryParse(_settings.HardExitTime, out var hardExit) &&
            ist.TimeOfDay >= hardExit)
            return $"Timed Out at {_settings.HardExitTime}";

        if (_state.ForceExitRequested) return "Manual Exit";

        return null;
    }

    private async Task ExecuteExitAsync(Position pos, string exitReason, CancellationToken ct)
    {
        _logger.LogInformation(
            "[VOL] EXIT → {Reason} | LTP: {Ltp:F2}", exitReason, pos.CurrentLtp);

        try
        {
            await _orders.PlaceMarketOrderAsync(
                pos.InstrumentKey, pos.Quantity, TradeDirection.Sell, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VOL] Failed to place exit order");
        }

        var ist = ORBState.GetIST();
        pos.ExitPrice  = pos.CurrentLtp;
        pos.ExitTime   = ist;
        pos.ExitReason = exitReason;
        pos.Status     = exitReason switch
        {
            "Target Hit"    => PositionStatus.TargetHit,
            "Stop Loss Hit" => PositionStatus.StopLossHit,
            "Manual Exit"   => PositionStatus.ManualExit,
            _               => PositionStatus.TimedOut
        };

        var pnl = (pos.ExitPrice - pos.EntryPrice) * pos.Quantity;

        _state.Log($"EXIT: {exitReason} | {pos.OptionSymbol} | PnL: ₹{pnl:F2}");
        _logger.LogInformation(
            "[VOL] Position closed | {Reason} | PnL: ₹{PnL:F2} | Entry: {Entry:F2} Exit: {Exit:F2}",
            exitReason, pnl, pos.EntryPrice, pos.ExitPrice);

        _state.ActivePosition  = null;
        _state.ForceExitRequested = false;
        _entryPriceSet         = false;
    }
}
