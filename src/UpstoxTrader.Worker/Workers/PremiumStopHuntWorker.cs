using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Strategy;

namespace UpstoxTrader.Worker.Workers;

public class PremiumStopHuntWorker : BackgroundService
{
    // ── Injected dependencies ─────────────────────────────────────────────────
    private readonly Channel<TickData> _niftyChannel;
    private readonly Channel<TickData> _optionChannel;
    private readonly IOptionChainService _optionChain;
    private readonly IOrderService _orders;
    private readonly PremiumStopHuntState _state;
    private readonly PremiumStopHuntSettings _settings;
    private readonly NiftySettings _nifty;
    private readonly ILogger<PremiumStopHuntWorker> _logger;
    private readonly IServiceProvider _sp;

    // ── Price history ─────────────────────────────────────────────────────────
    private readonly List<(DateTime Time, decimal Price)> _niftyPrices = new();
    private readonly List<(DateTime Time, decimal Price)> _cePrices    = new();
    private readonly List<(DateTime Time, decimal Price)> _pePrices    = new();

    // ── 1-min candle building ─────────────────────────────────────────────────
    private record MinuteBar(DateTime Minute, decimal Open, decimal High, decimal Low, decimal Close);
    private readonly List<MinuteBar> _minuteBars = new(); // last 10 completed 1-min candles
    private DateTime _currentBarMinute = DateTime.MinValue;
    private decimal _barOpen, _barHigh, _barLow, _barClose;

    // ── ATM tracking ──────────────────────────────────────────────────────────
    private string? _atmCeKey;
    private string? _atmPeKey;
    private int _currentAtmStrike;
    private decimal _lastNiftyPrice;
    private DateTime _lastAtmRefresh = DateTime.MinValue;

    // ── Signal state ──────────────────────────────────────────────────────────
    private (DateTime Time, OptionType Direction)? _premiumSignal;
    private (DateTime Time, OptionType Direction)? _stopHuntSignal;

    // ── Pending exit ──────────────────────────────────────────────────────────
    private string? _pendingExitReason;

    // ── Queues fed by background reader tasks ─────────────────────────────────
    private readonly ConcurrentQueue<TickData> _niftyQueue  = new();
    private readonly ConcurrentQueue<TickData> _optionQueue = new();

    public PremiumStopHuntWorker(
        [FromKeyedServices("premium-nifty-channel")]  Channel<TickData> niftyChannel,
        [FromKeyedServices("premium-option-channel")] Channel<TickData> optionChannel,
        IOptionChainService optionChain,
        IOrderService orders,
        PremiumStopHuntState state,
        IOptions<PremiumStopHuntSettings> settings,
        IOptions<NiftySettings> nifty,
        ILogger<PremiumStopHuntWorker> logger,
        IServiceProvider sp)
    {
        _niftyChannel  = niftyChannel;
        _optionChannel = optionChannel;
        _optionChain   = optionChain;
        _orders        = orders;
        _state         = state;
        _settings      = settings.Value;
        _nifty         = nifty.Value;
        _logger        = logger;
        _sp            = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("[PSH] PremiumStopHuntWorker started");
        _state.Log("Worker started");

        // Start background channel readers
        _ = ReadChannelAsync(_niftyChannel,  _niftyQueue,  ct);
        _ = ReadChannelAsync(_optionChannel, _optionQueue, ct);

        // Wait for first Nifty tick before doing ATM lookup
        _logger.LogInformation("[PSH] Waiting for first Nifty tick...");
        while (_niftyQueue.IsEmpty && !ct.IsCancellationRequested)
            await Task.Delay(500, ct);

        if (ct.IsCancellationRequested) return;

        // Drain the first tick so we have _lastNiftyPrice set
        if (_niftyQueue.TryDequeue(out var firstTick))
        {
            _lastNiftyPrice = firstTick.Ltp;
            RecordNiftyTick(firstTick);
        }

        await RefreshAtmAsync(ct);

        // Main processing loop — every 2 seconds
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                ProcessNiftyQueue();
                ProcessOptionQueue();
                PruneOldHistory();

                var ist = ORBState.GetIST();

                // Hard exit check
                if (TimeSpan.TryParse(_settings.HardExitTime, out var hardExit) &&
                    ist.TimeOfDay >= hardExit)
                {
                    if (_state.ActivePosition is { Status: PositionStatus.Open })
                    {
                        _logger.LogInformation("[PSH] Hard exit time reached — closing position");
                        _state.Log("Hard exit triggered at " + _settings.HardExitTime);
                        await ExitPositionAsync("Hard Exit (15:20)", ct);
                    }
                    // Sleep until next day check — just idle
                    await Task.Delay(TimeSpan.FromMinutes(30), ct);
                    continue;
                }

                // ATM refresh: every 30 min or if Nifty moved >75 pts from current ATM
                bool needsRefresh = false;
                if (_currentAtmStrike > 0 && _lastNiftyPrice > 0)
                    needsRefresh = Math.Abs(_lastNiftyPrice - _currentAtmStrike) > 75;
                if ((ist - _lastAtmRefresh).TotalMinutes >= 30)
                    needsRefresh = true;

                if (needsRefresh && _lastNiftyPrice > 0)
                    await RefreshAtmAsync(ct);

                // Handle pending exit from last evaluation cycle
                if (_pendingExitReason is not null)
                {
                    var reason = _pendingExitReason;
                    _pendingExitReason = null;
                    await ExitPositionAsync(reason, ct);
                    continue;
                }

                // Entry or position monitoring
                if (_state.ActivePosition is null)
                    await EvaluateEntryAsync(ct);
                else
                    EvaluatePositionExit();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PSH] Error in main loop");
                await Task.Delay(5000, ct);
            }
        }

        _logger.LogInformation("[PSH] PremiumStopHuntWorker stopped");
    }

    // ── Channel reader (background task) ─────────────────────────────────────

    private static async Task ReadChannelAsync(
        Channel<TickData> channel,
        ConcurrentQueue<TickData> queue,
        CancellationToken ct)
    {
        await foreach (var tick in channel.Reader.ReadAllAsync(ct))
            queue.Enqueue(tick);
    }

    // ── Queue processors ──────────────────────────────────────────────────────

    private void ProcessNiftyQueue()
    {
        while (_niftyQueue.TryDequeue(out var tick))
        {
            _lastNiftyPrice = tick.Ltp;
            RecordNiftyTick(tick);
        }
    }

    private void RecordNiftyTick(TickData tick)
    {
        var ist = ORBState.GetIST();
        _niftyPrices.Add((ist, tick.Ltp));

        // 1-min candle building
        var barMinute = new DateTime(ist.Year, ist.Month, ist.Day, ist.Hour, ist.Minute, 0);
        if (_currentBarMinute == DateTime.MinValue)
        {
            _currentBarMinute = barMinute;
            _barOpen = _barHigh = _barLow = _barClose = tick.Ltp;
        }
        else if (barMinute == _currentBarMinute)
        {
            if (tick.Ltp > _barHigh) _barHigh = tick.Ltp;
            if (tick.Ltp < _barLow)  _barLow  = tick.Ltp;
            _barClose = tick.Ltp;
        }
        else
        {
            // New minute — complete the old bar
            var completedBar = new MinuteBar(_currentBarMinute, _barOpen, _barHigh, _barLow, _barClose);
            _minuteBars.Add(completedBar);
            if (_minuteBars.Count > 10) _minuteBars.RemoveAt(0);

            CheckStopHunt(completedBar);

            // Start new bar
            _currentBarMinute = barMinute;
            _barOpen = _barHigh = _barLow = _barClose = tick.Ltp;
        }
    }

    private void ProcessOptionQueue()
    {
        while (_optionQueue.TryDequeue(out var tick))
        {
            var ist = ORBState.GetIST();

            if (tick.InstrumentKey == _atmCeKey)
                _cePrices.Add((ist, tick.Ltp));
            else if (tick.InstrumentKey == _atmPeKey)
                _pePrices.Add((ist, tick.Ltp));

            // Update active position LTP
            if (_state.ActivePosition is { Status: PositionStatus.Open } pos &&
                tick.InstrumentKey == pos.InstrumentKey)
            {
                pos.CurrentLtp = tick.Ltp;
            }
        }
    }

    // ── History pruning ───────────────────────────────────────────────────────

    private void PruneOldHistory()
    {
        var cutoff = ORBState.GetIST().AddMinutes(-10);
        _niftyPrices.RemoveAll(p => p.Time < cutoff);
        _cePrices.RemoveAll(p => p.Time < cutoff);
        _pePrices.RemoveAll(p => p.Time < cutoff);
    }

    // ── Stop Hunt detection ───────────────────────────────────────────────────

    private void CheckStopHunt(MinuteBar bar)
    {
        if (_minuteBars.Count < 5) return;

        // Use the 5 candles before the completed bar (they are already in _minuteBars)
        var lookback = _minuteBars.TakeLast(5).ToList();
        if (lookback.Count < 5) return;

        decimal swingHigh = lookback.Max(b => b.High);
        decimal swingLow  = lookback.Min(b => b.Low);

        decimal body = Math.Abs(bar.Close - bar.Open);
        decimal upperWick = bar.High - Math.Max(bar.Open, bar.Close);
        decimal lowerWick = Math.Min(bar.Open, bar.Close) - bar.Low;

        bool bullish = lowerWick > _settings.StopHuntWickPoints &&
                       bar.Low < swingLow &&
                       (body == 0 || lowerWick > 2 * body);

        bool bearish = upperWick > _settings.StopHuntWickPoints &&
                       bar.High > swingHigh &&
                       (body == 0 || upperWick > 2 * body);

        if (bullish)
        {
            var ist = ORBState.GetIST();
            _stopHuntSignal = (ist, OptionType.CE);
            _logger.LogInformation(
                "[PSH] STOP HUNT BULLISH | Wick:{W:F1}pts below SwingLow:{SL:F1} Body:{B:F1}",
                lowerWick, swingLow, body);
            _state.Log($"StopHunt CE | LowerWick {lowerWick:F1}pts | SwingLow {swingLow:F1}");
        }
        else if (bearish)
        {
            var ist = ORBState.GetIST();
            _stopHuntSignal = (ist, OptionType.PE);
            _logger.LogInformation(
                "[PSH] STOP HUNT BEARISH | Wick:{W:F1}pts above SwingHigh:{SH:F1} Body:{B:F1}",
                upperWick, swingHigh, body);
            _state.Log($"StopHunt PE | UpperWick {upperWick:F1}pts | SwingHigh {swingHigh:F1}");
        }
    }

    // ── Premium signal evaluation ─────────────────────────────────────────────

    private OptionType? EvaluatePremiumSignal()
    {
        var ist     = ORBState.GetIST();
        var cutoff  = ist.AddMinutes(-_settings.PremiumLookbackMinutes);

        var recentNifty = _niftyPrices.Where(p => p.Time >= cutoff).ToList();
        var recentCe    = _cePrices.Where(p => p.Time >= cutoff).ToList();
        var recentPe    = _pePrices.Where(p => p.Time >= cutoff).ToList();

        if (recentNifty.Count < 2 || recentCe.Count < 2 || recentPe.Count < 2)
            return null;

        var niftyMove = Math.Abs(recentNifty.Last().Price - recentNifty.First().Price);

        if (niftyMove >= _settings.NiftyFlatPoints)
            return null; // Nifty is not flat

        var ceRise = recentCe.Last().Price - recentCe.First().Price;
        var peRise = recentPe.Last().Price - recentPe.First().Price;

        if (ceRise >= _settings.PremiumRisePoints)
        {
            _logger.LogInformation(
                "[PSH] PREMIUM SIGNAL CE | CE +{Rise:F1}pts | Nifty flat ({Flat:F1}pts)",
                ceRise, niftyMove);
            _state.Log($"PremiumSignal CE | CE +{ceRise:F1}pts | Nifty flat {niftyMove:F1}pts");
            return OptionType.CE;
        }

        if (peRise >= _settings.PremiumRisePoints)
        {
            _logger.LogInformation(
                "[PSH] PREMIUM SIGNAL PE | PE +{Rise:F1}pts | Nifty flat ({Flat:F1}pts)",
                peRise, niftyMove);
            _state.Log($"PremiumSignal PE | PE +{peRise:F1}pts | Nifty flat {niftyMove:F1}pts");
            return OptionType.PE;
        }

        return null;
    }

    // ── Entry evaluation ──────────────────────────────────────────────────────

    private async Task EvaluateEntryAsync(CancellationToken ct)
    {
        var ist = ORBState.GetIST();
        var signalWindow = TimeSpan.FromMinutes(_settings.SignalWindowMinutes);

        // Update premium signal
        var premDir = EvaluatePremiumSignal();
        if (premDir.HasValue)
            _premiumSignal = (ist, premDir.Value);

        // Expire old signals
        if (_premiumSignal.HasValue && (ist - _premiumSignal.Value.Time) > signalWindow)
        {
            _logger.LogInformation("[PSH] Premium signal expired");
            _premiumSignal = null;
        }

        if (_stopHuntSignal.HasValue && (ist - _stopHuntSignal.Value.Time) > signalWindow)
        {
            _logger.LogInformation("[PSH] Stop hunt signal expired");
            _stopHuntSignal = null;
        }

        // Check alignment
        if (_premiumSignal.HasValue && _stopHuntSignal.HasValue &&
            _premiumSignal.Value.Direction == _stopHuntSignal.Value.Direction)
        {
            var direction = _premiumSignal.Value.Direction;
            _logger.LogInformation("[PSH] BOTH SIGNALS ALIGNED → {Dir} — entering trade", direction);
            _state.Log($"Both signals aligned: {direction} — entering trade");
            await EnterTradeAsync(direction, ct);
        }
    }

    // ── Trade entry ───────────────────────────────────────────────────────────

    private async Task EnterTradeAsync(OptionType direction, CancellationToken ct)
    {
        if (_lastNiftyPrice <= 0)
        {
            _logger.LogWarning("[PSH] No Nifty price yet — cannot enter trade");
            return;
        }

        var atm = ATMCalculator.Calculate(_lastNiftyPrice, _nifty.StrikeInterval);

        OptionInstrument instrument;
        try
        {
            instrument = await _optionChain.GetAtmOptionSymbolAsync(atm, direction, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PSH] Failed to fetch option chain");
            _state.Log($"ERROR: Option chain failed — {ex.Message}");
            return;
        }

        string orderId;
        try
        {
            _logger.LogInformation(
                "[PSH] Placing order | Key:{Key} Strike:{Strike} Expiry:{Expiry} Qty:{Qty}",
                instrument.InstrumentKey, instrument.Strike, instrument.Expiry, _settings.LotSize);

            orderId = await _orders.PlaceMarketOrderAsync(
                instrument.InstrumentKey,
                _settings.LotSize,
                TradeDirection.Buy,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PSH] Failed to place order");
            _state.Log($"ERROR: Order failed — {ex.Message}");
            return;
        }

        var ist = ORBState.GetIST();
        _state.ActivePosition = new Position
        {
            OrderId       = orderId,
            OptionSymbol  = instrument.InstrumentKey,
            InstrumentKey = instrument.InstrumentKey,
            EntryPrice    = 0, // set from first option tick
            EntryTime     = ist,
            Quantity      = _settings.LotSize,
            Status        = PositionStatus.Open
        };

        _state.Log($"Order placed | {instrument.InstrumentKey} | OrderId: {orderId}");

        // Clear signals after entry
        _premiumSignal   = null;
        _stopHuntSignal  = null;

        // Subscribe to option ticks via MarketDataWorker
        var mdw = _sp.GetService<MarketDataWorker>();
        if (mdw is not null)
            await mdw.SubscribeToOptionAsync(instrument.InstrumentKey, ct);
    }

    // ── Position exit evaluation ──────────────────────────────────────────────

    private void EvaluatePositionExit()
    {
        var pos = _state.ActivePosition;
        if (pos is null || pos.Status != PositionStatus.Open) return;

        // Set entry price from first meaningful tick
        if (pos.EntryPrice == 0 && pos.CurrentLtp > 0)
        {
            pos.EntryPrice = pos.CurrentLtp;
            _logger.LogInformation("[PSH] Entry price set from first tick: {Price}", pos.EntryPrice);
            _state.Log($"Entry price: {pos.EntryPrice:F2}");
            return;
        }

        if (pos.EntryPrice == 0) return;

        var pts = pos.CurrentLtp - pos.EntryPrice;

        if (pts >= _settings.TakeProfitPoints)
        {
            _pendingExitReason = $"Take Profit (+{pts:F1}pts)";
            _logger.LogInformation("[PSH] Take profit triggered: +{Pts:F1}pts", pts);
            _state.Log($"TP triggered +{pts:F1}pts");
        }
        else if (pts <= -_settings.StopLossPoints)
        {
            _pendingExitReason = $"Stop Loss ({pts:F1}pts)";
            _logger.LogInformation("[PSH] Stop loss triggered: {Pts:F1}pts", pts);
            _state.Log($"SL triggered {pts:F1}pts");
        }
    }

    // ── Trade exit ────────────────────────────────────────────────────────────

    private async Task ExitPositionAsync(string reason, CancellationToken ct)
    {
        var pos = _state.ActivePosition;
        if (pos is null) return;

        try
        {
            await _orders.PlaceMarketOrderAsync(
                pos.InstrumentKey, pos.Quantity, TradeDirection.Sell, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PSH] Failed to place sell order");
        }

        var ist = ORBState.GetIST();
        pos.ExitPrice  = pos.CurrentLtp;
        pos.ExitTime   = ist;
        pos.ExitReason = reason;
        pos.Status     = PositionStatus.TimedOut;

        var pnl = (pos.ExitPrice - pos.EntryPrice) * pos.Quantity;
        _logger.LogInformation("[PSH] Position closed | {Reason} | PnL: ₹{PnL:F2}", reason, pnl);
        _state.Log($"Closed | {reason} | PnL: ₹{pnl:F2}");
        _state.ActivePosition = null;
    }

    // ── ATM refresh ───────────────────────────────────────────────────────────

    private async Task RefreshAtmAsync(CancellationToken ct)
    {
        if (_lastNiftyPrice <= 0) return;

        var atm = ATMCalculator.Calculate(_lastNiftyPrice, _nifty.StrikeInterval);

        _logger.LogInformation("[PSH] Refreshing ATM subscription | Nifty:{Price:F1} ATM:{Atm}", _lastNiftyPrice, atm);

        try
        {
            var ce = await _optionChain.GetAtmOptionSymbolAsync(atm, OptionType.CE, ct);
            var pe = await _optionChain.GetAtmOptionSymbolAsync(atm, OptionType.PE, ct);

            _atmCeKey        = ce.InstrumentKey;
            _atmPeKey        = pe.InstrumentKey;
            _currentAtmStrike = atm;
            _lastAtmRefresh  = ORBState.GetIST();

            // Clear stale price history for old ATM
            _cePrices.Clear();
            _pePrices.Clear();

            var mdw = _sp.GetService<MarketDataWorker>();
            if (mdw is not null)
            {
                await mdw.SubscribeToOptionAsync(ce.InstrumentKey, ct);
                await mdw.SubscribeToOptionAsync(pe.InstrumentKey, ct);
            }

            _logger.LogInformation("[PSH] ATM subscribed | CE:{CeKey} PE:{PeKey}", _atmCeKey, _atmPeKey);
            _state.Log($"ATM refreshed | Strike {atm} | CE: {ce.InstrumentKey} | PE: {pe.InstrumentKey}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PSH] Failed to refresh ATM subscription");
            _state.Log($"ERROR: ATM refresh failed — {ex.Message}");
        }
    }
}
