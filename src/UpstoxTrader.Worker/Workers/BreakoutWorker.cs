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

public class BreakoutWorker : BackgroundService
{
    private readonly Channel<TickData> _breakoutChannel;
    private readonly ORBCandleBuilder _candleBuilder;
    private readonly BreakoutDetector _breakoutDetector;
    private readonly IOptionChainService _optionChain;
    private readonly IOrderService _orders;
    private readonly ORBState _state;
    private readonly TradingSettings _trading;
    private readonly NiftySettings _nifty;
    private readonly ILogger<BreakoutWorker> _logger;
    private readonly IServiceProvider _sp;

    public BreakoutWorker(
        [FromKeyedServices("breakout-channel")] Channel<TickData> breakoutChannel,
        ORBCandleBuilder candleBuilder,
        BreakoutDetector breakoutDetector,
        IOptionChainService optionChain,
        IOrderService orders,
        ORBState state,
        IOptions<TradingSettings> trading,
        IOptions<NiftySettings> nifty,
        ILogger<BreakoutWorker> logger,
        IServiceProvider sp)
    {
        _breakoutChannel = breakoutChannel;
        _candleBuilder = candleBuilder;
        _breakoutDetector = breakoutDetector;
        _optionChain = optionChain;
        _orders = orders;
        _state = state;
        _trading = trading.Value;
        _nifty = nifty.Value;
        _logger = logger;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BreakoutWorker started");

        await foreach (var tick in _breakoutChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                _candleBuilder.ProcessTick(tick);

                if (!_state.CandleReady) continue;

                if (_state.PreviousCandle is not null)
{
    _logger.LogInformation(
        "LTP: {Ltp:F2} | REF H: {High:F2} L: {Low:F2}",
        tick.Ltp,
        _state.PreviousCandle.High,
        _state.PreviousCandle.Low);
}
                var signal = _breakoutDetector.Detect(tick);
                if (signal is null) continue;

                await HandleSignalAsync(signal, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BreakoutWorker error processing tick");
            }
        }
    }

    private async Task HandleSignalAsync(Signal signal, CancellationToken ct)
    {
        var atm = ATMCalculator.Calculate(signal.NiftyLtp, _nifty.StrikeInterval);
        signal.AtmStrike = atm;

        _state.Log($"Signal: {signal.Direction} | Nifty: {signal.NiftyLtp:F2} | ATM: {atm}");
        _logger.LogInformation("Signal detected: {Dir} | ATM Strike: {Atm}", signal.Direction, atm);

        // ✅ Get full instrument (NOT string)
        OptionInstrument instrument;
        try
        {
            _state.Log($"Fetching {signal.Direction} option at strike {atm}");
            instrument = await _optionChain.GetAtmOptionSymbolAsync(atm, signal.Direction, ct);

            signal.OptionSymbol = instrument.InstrumentKey;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch option chain");
            _state.Log($"ERROR: Could not fetch option symbol — {ex.Message}");
            return;
        }

        _state.ActiveSignal = signal;

        // ✅ Correct quantity calculation
        int quantity = instrument.LotSize;

        string orderId;
        try
        {
            _logger.LogInformation(
                "Order Debug → Key: {Key}, Strike: {Strike}, Expiry: {Expiry}, LotSize: {Lot}, Qty: {Qty}",
                instrument.InstrumentKey,
                instrument.Strike,
                instrument.Expiry,
                instrument.LotSize,
                quantity);

            orderId = await _orders.PlaceMarketOrderAsync(
                instrument.InstrumentKey,
                quantity,
                TradeDirection.Buy,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place order");
            _state.Log($"ERROR: Order placement failed — {ex.Message}");
            return;
        }

        var ist = ORBState.GetIST();

        var position = new Position
        {
            OrderId = orderId,
            OptionSymbol = instrument.InstrumentKey,
            InstrumentKey = instrument.InstrumentKey,
            EntryPrice = _state.LastNiftyLtp,
            EntryTime = ist,
            Quantity = quantity,
            Status = PositionStatus.Open
        };

        _state.ActivePosition = position;
        _state.TradeTakenToday = true;

        _state.Log($"Order placed | {instrument.InstrumentKey} | Qty: {quantity} | OrderId: {orderId}");

        // Subscribe to option ticks
        var mdw = _sp.GetService<MarketDataWorker>();
        if (mdw is not null)
        {
            await mdw.SubscribeToOptionAsync(instrument.InstrumentKey, ct);
        }
    }
}