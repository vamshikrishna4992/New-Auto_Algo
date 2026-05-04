using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Strategy;

namespace UpstoxTrader.Worker.Workers;

public class PositionMonitorWorker : BackgroundService
{
    private readonly Channel<TickData> _optionChannel;
    private readonly ExitEvaluator _exitEvaluator;
    private readonly IOrderService _orders;
    private readonly ORBState _state;
    private readonly ILogger<PositionMonitorWorker> _logger;

    private bool _entryPriceSet;

    public PositionMonitorWorker(
        [FromKeyedServices("option-channel")] Channel<TickData> optionChannel,
        ExitEvaluator exitEvaluator,
        IOrderService orders,
        ORBState state,
        ILogger<PositionMonitorWorker> logger)
    {
        _optionChannel = optionChannel;
        _exitEvaluator = exitEvaluator;
        _orders = orders;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PositionMonitorWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_state.ActivePosition is null ||
                    _state.ActivePosition.Status != PositionStatus.Open)
                {
                    _entryPriceSet = false;
                    await Task.Delay(200, stoppingToken);
                    continue;
                }

                // Try to read a tick with timeout
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readCts.CancelAfter(200);

                try
                {
                    var tick = await _optionChannel.Reader.ReadAsync(readCts.Token);
                    await ProcessTickAsync(tick, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // 200ms timeout — check exit conditions with last known LTP
                    var pos = _state.ActivePosition;
                    if (pos is not null && pos.Status == PositionStatus.Open)
                    {
                        var ist = ORBState.GetIST();
                        var exitReason = _exitEvaluator.Evaluate(pos, ist);
                        if (exitReason is not null)
                            await ExecuteExitAsync(pos, exitReason, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PositionMonitorWorker error");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessTickAsync(TickData tick, CancellationToken ct)
    {
        var pos = _state.ActivePosition;
        if (pos is null || pos.Status != PositionStatus.Open) return;

        pos.CurrentLtp = tick.Ltp;

        if (!_entryPriceSet && tick.Ltp > 0)
        {
            pos.EntryPrice = tick.Ltp;
            _entryPriceSet = true;
            _state.Log($"Entry confirmed | {pos.OptionSymbol} | Price: {tick.Ltp:F2}");
        }

        var ist = ORBState.GetIST();
        var exitReason = _exitEvaluator.Evaluate(pos, ist);
        if (exitReason is not null)
            await ExecuteExitAsync(pos, exitReason, ct);
    }

    private async Task ExecuteExitAsync(Position pos, string exitReason, CancellationToken ct)
    {
        _logger.LogInformation("Exiting position: {Reason} | LTP: {Ltp}", exitReason, pos.CurrentLtp);

        try
        {
            await _orders.PlaceMarketOrderAsync(
                pos.InstrumentKey, pos.Quantity, TradeDirection.Sell, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place exit order");
        }

        var ist = ORBState.GetIST();
        pos.ExitPrice = pos.CurrentLtp;
        pos.ExitTime = ist;
        pos.ExitReason = exitReason;
        pos.Status = exitReason switch
        {
            "Target Hit" => PositionStatus.TargetHit,
            "Stop Loss Hit" => PositionStatus.StopLossHit,
            var r when r.StartsWith("Timed Out") => PositionStatus.TimedOut,
            "Manual Exit" => PositionStatus.ManualExit,
            _ => PositionStatus.TimedOut
        };

        var finalPnL = (pos.ExitPrice - pos.EntryPrice) * pos.Quantity;
        _state.Log($"EXIT: {exitReason} | {pos.OptionSymbol} | PnL: ₹{finalPnL:F2}");
        _logger.LogInformation("Position closed | {Reason} | PnL: ₹{PnL:F2} | E:{Entry} X:{Exit}",
            exitReason, finalPnL, pos.EntryPrice, pos.ExitPrice);

        _state.ForceExitRequested = false;
    }
}
