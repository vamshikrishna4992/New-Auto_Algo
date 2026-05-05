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

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readCts.CancelAfter(200);

                try
                {
                    var tick = await _optionChannel.Reader.ReadAsync(readCts.Token);
                    await ProcessTickAsync(tick, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    if (!_entryPriceSet) continue;

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

        // Update LTP
        pos.CurrentLtp = tick.Ltp;

        // ✅ Step 1: Set Entry Price FIRST
        if (!_entryPriceSet && tick.Ltp > 0)
        {
            pos.EntryPrice = tick.Ltp;
            _entryPriceSet = true;

            _state.Log($"Entry confirmed | {pos.OptionSymbol} | Price: {tick.Ltp:F2}");

            _logger.LogInformation(
                "ENTRY SET → {Symbol} | Entry: {Entry:F2}",
                pos.OptionSymbol,
                pos.EntryPrice);
        }

        // ✅ Step 2: Calculate & Log PnL (ONLY after entry is set)
        if (_entryPriceSet)
        {
            var entry = pos.EntryPrice;
            var ltp = pos.CurrentLtp;

            var pnlPerUnit = ltp - entry;
            var totalPnl = pnlPerUnit * pos.Quantity;
            var pnlPct = (pnlPerUnit / entry) * 100;

            _logger.LogInformation(
    "📊 LTP: {Ltp:F2} | Entry: {Entry:F2} | PnL: {Pct:F2}% (₹{Pnl:F2})",
    ltp,
    entry,
    pnlPct,
    totalPnl);
        }

        // ✅ Step 3: Exit evaluation
        var ist = ORBState.GetIST();
        var exitReason = _exitEvaluator.Evaluate(pos, ist);

        if (exitReason is not null)
            await ExecuteExitAsync(pos, exitReason, ct);
    }

    private async Task ExecuteExitAsync(Position pos, string exitReason, CancellationToken ct)
    {
        _logger.LogInformation(
            "🚪 EXIT → {Reason} | LTP: {Ltp:F2}",
            exitReason,
            pos.CurrentLtp);

        try
        {
            // Always SELL to exit
            await _orders.PlaceMarketOrderAsync(
                pos.InstrumentKey,
                pos.Quantity,
                TradeDirection.Sell,
                ct);
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

        var pnl = (pos.ExitPrice - pos.EntryPrice) * pos.Quantity;

        _state.Log($"EXIT: {exitReason} | {pos.OptionSymbol} | PnL: ₹{pnl:F2}");

        _logger.LogInformation(
            "Position closed | {Reason} | PnL: ₹{PnL:F2} | Entry: {Entry:F2} Exit: {Exit:F2}",
            exitReason,
            pnl,
            pos.EntryPrice,
            pos.ExitPrice);

        // Reset state
        _state.ActivePosition = null;
        _state.ForceExitRequested = false;
        _entryPriceSet = false;
    }
}