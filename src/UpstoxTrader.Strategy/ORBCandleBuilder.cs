using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;

namespace UpstoxTrader.Strategy;

public class ORBCandleBuilder
{
    private readonly ORBState _state;
    private readonly TradingSettings _trading;
    private readonly NiftySettings _nifty;
    private readonly ILogger<ORBCandleBuilder> _logger;

    private decimal _open;
    private decimal _high;
    private decimal _low;
    private decimal _close;
    private DateTime _currentCandleStart = DateTime.MinValue;
    private DateTime _currentCandleEnd = DateTime.MinValue;

    private static readonly TimeZoneInfo _istZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    public ORBCandleBuilder(ORBState state,
        IOptions<TradingSettings> trading,
        IOptions<NiftySettings> nifty,
        ILogger<ORBCandleBuilder> logger)
    {
        _state = state;
        _trading = trading.Value;
        _nifty = nifty.Value;
        _logger = logger;
    }

    public void ProcessTick(TickData tick)
    {
        if (tick.InstrumentKey != _nifty.InstrumentKey) return;

        // FirstOnly: once the opening candle is locked, ignore all further ticks
        if (_trading.CandleMode == "FirstOnly" && _state.CandleReady) return;

        var tickIst = TimeZoneInfo.ConvertTimeFromUtc(tick.Timestamp, _istZone);
        var today = DateOnly.FromDateTime(tickIst);
        var marketOpen = new DateTime(today.Year, today.Month, today.Day, 9, 15, 0);

        if (tickIst < marketOpen) return;

        // Determine which candle window this tick belongs to
        var elapsed = (int)(tickIst - marketOpen).TotalMinutes;
        var windowIndex = elapsed / _trading.CandleMinutes;
        var candleStart = marketOpen.AddMinutes(windowIndex * _trading.CandleMinutes);
        var candleEnd = candleStart.AddMinutes(_trading.CandleMinutes);

        // New window detected — finalize the candle that just closed, reset OHLC
        if (_currentCandleStart != DateTime.MinValue && candleStart != _currentCandleStart)
        {
            FinalizeCandle(_currentCandleStart, _currentCandleEnd);
            _open = tick.Ltp;
            _high = tick.Ltp;
            _low = tick.Ltp;
            _close = tick.Ltp;
        }
        else
        {
            // Accumulate within the same window
            if (_currentCandleStart == DateTime.MinValue)
            {
                // First tick ever — initialize OHLC
                _open = tick.Ltp;
                _high = tick.Ltp;
                _low = tick.Ltp;
            }
            else
            {
                if (tick.Ltp > _high) _high = tick.Ltp;
                if (tick.Ltp < _low) _low = tick.Ltp;
            }
            _close = tick.Ltp;
        }

        _currentCandleStart = candleStart;
        _currentCandleEnd = candleEnd;
        _state.CandleEndTime = candleEnd;
        _state.CandleHighSoFar = _high;
        _state.CandleLowSoFar = _low;
    }

    private void FinalizeCandle(DateTime start, DateTime end)
    {
        var candle = new OpeningCandle
        {
            High = _high,
            Low = _low,
            Open = _open,
            Close = _close,
            CandleStart = start,
            CandleEnd = end
        };

        _state.Candle = candle;
        _state.CandleReady = true;
        _state.Log($"Candle [{start:HH:mm}–{end:HH:mm}] | H:{_high:F2} L:{_low:F2}");
        _logger.LogInformation("Candle locked [{Start:HH:mm}–{End:HH:mm}] H:{High} L:{Low} O:{Open} C:{Close}",
            start, end, _high, _low, _open, _close);
    }

    public void Reset()
    {
        _currentCandleStart = DateTime.MinValue;
        _currentCandleEnd = DateTime.MinValue;
        _open = _high = _low = _close = 0;
    }
}
