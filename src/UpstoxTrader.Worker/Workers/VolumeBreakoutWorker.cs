using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Services;
using UpstoxTrader.Strategy;

namespace UpstoxTrader.Worker.Workers;

public class VolumeBreakoutWorker : BackgroundService
{
    private readonly FuturesCandleService _candles;
    private readonly IOptionChainService _optionChain;
    private readonly IOrderService _orders;
    private readonly VolumeBreakoutState _state;
    private readonly VolumeStrategySettings _settings;
    private readonly NiftySettings _nifty;
    private readonly ILogger<VolumeBreakoutWorker> _logger;
    private readonly IServiceProvider _sp;

    private static readonly TimeZoneInfo _istZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    public VolumeBreakoutWorker(
        FuturesCandleService candles,
        IOptionChainService optionChain,
        IOrderService orders,
        VolumeBreakoutState state,
        IOptions<VolumeStrategySettings> settings,
        IOptions<NiftySettings> nifty,
        ILogger<VolumeBreakoutWorker> logger,
        IServiceProvider sp)
    {
        _candles    = candles;
        _optionChain = optionChain;
        _orders     = orders;
        _state      = state;
        _settings   = settings.Value;
        _nifty      = nifty.Value;
        _logger     = logger;
        _sp         = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[VOL] VolumeBreakoutWorker started");

        // Discover futures key on startup
        await _candles.GetFuturesKeyAsync(_settings.FuturesInstrumentKey, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WaitForNextCandleCloseAsync(ct);

                if (ct.IsCancellationRequested) break;

                var ist = GetIST();

                // Hard exit time — stop new signals
                if (TimeSpan.TryParse(_settings.HardExitTime, out var hardExit) &&
                    ist.TimeOfDay >= hardExit)
                {
                    _logger.LogInformation("[VOL] Past hard exit time — no new signals");
                    await Task.Delay(TimeSpan.FromMinutes(60), ct);
                    continue;
                }

                await EvaluateCandleAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VOL] VolumeBreakoutWorker error");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task EvaluateCandleAsync(CancellationToken ct)
    {
        var candles = await _candles.GetLast2CandlesAsync(_settings.FuturesInstrumentKey, ct);

        if (candles.Count < 2)
        {
            _logger.LogDebug("[VOL] Not enough candles yet");
            return;
        }

        var current  = candles[0]; // most recent closed candle
        var previous = candles[1]; // one before

        // Avoid re-processing the same candle
        if (current.Timestamp == _state.LastProcessedCandleTime)
        {
            _logger.LogDebug("[VOL] Candle [{T:HH:mm}] already processed — skipping", current.Timestamp);
            return;
        }

        _logger.LogInformation(
            "[VOL] Evaluating | Current [{CT:HH:mm}] C:{CC} Vol:{CV} | Prev [{PT:HH:mm}] C:{PC} Vol:{PV}",
            current.Timestamp, current.Close, current.Volume,
            previous.Timestamp, previous.Close, previous.Volume);

        // Volume condition: current >= multiplier × previous
        if (previous.Volume <= 0 || current.Volume < (long)(previous.Volume * _settings.VolumeMultiplier))
        {
            _logger.LogInformation(
                "[VOL] Volume condition NOT met | {CV} < {Mult}× {PV}",
                current.Volume, _settings.VolumeMultiplier, previous.Volume);
            _state.LastProcessedCandleTime = current.Timestamp;
            return;
        }

        // Direction condition
        OptionType? direction = null;
        if (current.Close > previous.Close)
            direction = OptionType.CE;
        else if (current.Close < previous.Close)
            direction = OptionType.PE;

        if (direction is null)
        {
            _logger.LogInformation("[VOL] Volume spike but close == previous close — no signal");
            _state.LastProcessedCandleTime = current.Timestamp;
            return;
        }

        _logger.LogInformation(
            "[VOL] SIGNAL {Dir} | Vol spike {CV} >= {Mult}× {PV} | Close {CC} vs {PC}",
            direction, current.Volume, _settings.VolumeMultiplier, previous.Volume,
            current.Close, previous.Close);

        _state.LastProcessedCandleTime = current.Timestamp;
        _state.Log($"Signal {direction} | Vol {current.Volume} >= {_settings.VolumeMultiplier}x {previous.Volume}");

        await HandleSignalAsync(direction.Value, current.Close, ct);
    }

    private async Task HandleSignalAsync(OptionType direction, decimal futuresLtp, CancellationToken ct)
    {
        // Close existing position before opening new one
        if (_state.ActivePosition is { Status: PositionStatus.Open })
        {
            _logger.LogInformation("[VOL] Closing existing position before new signal");
            await ClosePositionAsync(_state.ActivePosition, "New Signal — Replaced", ct);
        }

        // ATM strike from futures LTP
        var atm = ATMCalculator.Calculate(futuresLtp, _nifty.StrikeInterval);
        _logger.LogInformation("[VOL] ATM Strike: {Atm} | Direction: {Dir}", atm, direction);

        OptionInstrument instrument;
        try
        {
            instrument = await _optionChain.GetAtmOptionSymbolAsync(atm, direction, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VOL] Failed to fetch option chain");
            _state.Log($"ERROR: Option chain failed — {ex.Message}");
            return;
        }

        string orderId;
        try
        {
            _logger.LogInformation(
                "[VOL] Placing order | Key:{Key} Strike:{Strike} Expiry:{Expiry} Qty:{Qty}",
                instrument.InstrumentKey, instrument.Strike, instrument.Expiry, _settings.LotSize);

            orderId = await _orders.PlaceMarketOrderAsync(
                instrument.InstrumentKey,
                _settings.LotSize,
                TradeDirection.Buy,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VOL] Failed to place order");
            _state.Log($"ERROR: Order failed — {ex.Message}");
            return;
        }

        var ist = GetIST();
        _state.ActivePosition = new Position
        {
            OrderId       = orderId,
            OptionSymbol  = instrument.InstrumentKey,
            InstrumentKey = instrument.InstrumentKey,
            EntryPrice    = 0,   // set from first option tick in VolumePositionMonitorWorker
            EntryTime     = ist,
            Quantity      = _settings.LotSize,
            Status        = PositionStatus.Open
        };

        _state.Log($"Order placed | {instrument.InstrumentKey} | OrderId: {orderId}");

        // Subscribe to option ticks
        var mdw = _sp.GetService<MarketDataWorker>();
        if (mdw is not null)
            await mdw.SubscribeToOptionAsync(instrument.InstrumentKey, ct);
    }

    private async Task ClosePositionAsync(Position pos, string reason, CancellationToken ct)
    {
        try
        {
            await _orders.PlaceMarketOrderAsync(
                pos.InstrumentKey, pos.Quantity, TradeDirection.Sell, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VOL] Failed to place close order");
        }

        var ist = GetIST();
        pos.ExitPrice  = pos.CurrentLtp;
        pos.ExitTime   = ist;
        pos.ExitReason = reason;
        pos.Status     = PositionStatus.TimedOut;

        var pnl = (pos.ExitPrice - pos.EntryPrice) * pos.Quantity;
        _logger.LogInformation("[VOL] Position closed | {Reason} | PnL: ₹{PnL:F2}", reason, pnl);
        _state.Log($"Closed | {reason} | PnL: ₹{pnl:F2}");
        _state.ActivePosition = null;
    }

    // ── Timing ───────────────────────────────────────────────────────────────

    private async Task WaitForNextCandleCloseAsync(CancellationToken ct)
    {
        var ist        = GetIST();
        var marketOpen = ist.Date.AddHours(9).AddMinutes(15);

        DateTime nextFire;

        if (ist < marketOpen)
        {
            nextFire = marketOpen.AddMinutes(5).AddSeconds(15);
        }
        else
        {
            var elapsed   = (ist - marketOpen).TotalMinutes;
            var nextIndex = (int)(elapsed / 5) + 1;
            nextFire      = marketOpen.AddMinutes(nextIndex * 5).AddSeconds(15);
        }

        var delay = nextFire - ist;
        if (delay > TimeSpan.Zero)
        {
            _logger.LogDebug("[VOL] Next candle check at {T:HH:mm:ss} (in {S:F0}s)",
                nextFire, delay.TotalSeconds);
            await Task.Delay(delay, ct);
        }
    }

    private static DateTime GetIST() => ORBState.GetIST();
}
