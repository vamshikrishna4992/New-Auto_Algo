using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;

namespace UpstoxTrader.Strategy;

public class BreakoutDetector
{
    private readonly ORBState _state;
    private readonly TradingSettings _trading;
    private readonly ILogger<BreakoutDetector> _logger;
    private bool _signalFired;
    // tracks which candle's high/low is currently being watched for breakout
    private DateTime _lastCandleEnd;

    public BreakoutDetector(ORBState state, IOptions<TradingSettings> trading, ILogger<BreakoutDetector> logger)
    {
        _state = state;
        _trading = trading.Value;
        _logger = logger;
    }

    public Signal? Detect(TickData tick)
    {
        if (_state.TradeTakenToday) return null;
        if (!_state.CandleReady || _state.Candle is null) return null;

        var ist = ORBState.GetIST();

        // AllCandles: when a new candle closes and becomes the reference, reset the signal gate
        // so each candle gets a fresh breakout check in the next period
        if (_trading.CandleMode == "AllCandles" && _state.Candle.CandleEnd != _lastCandleEnd)
        {
            _signalFired = false;
            _lastCandleEnd = _state.Candle.CandleEnd;
        }

        if (_signalFired) return null;

        // AllCandles: do not fire signals at or after SignalCutoffTime
        if (_trading.CandleMode == "AllCandles" &&
            TimeSpan.TryParse(_trading.SignalCutoffTime, out var cutoff) &&
            ist.TimeOfDay >= cutoff)
            return null;

        if (tick.Ltp > _state.Candle.High)
        {
            _signalFired = true;
            _state.Log($"Breakout UP | LTP: {tick.Ltp:F2} crossed High: {_state.Candle.High:F2}");
            _logger.LogInformation("Breakout UP | {Ltp} > {High}", tick.Ltp, _state.Candle.High);

            return new Signal
            {
                Direction = OptionType.CE,
                NiftyLtp = tick.Ltp,
                DetectedAt = ist
            };
        }

        if (tick.Ltp < _state.Candle.Low)
        {
            _signalFired = true;
            _state.Log($"Breakout DOWN | LTP: {tick.Ltp:F2} crossed Low: {_state.Candle.Low:F2}");
            _logger.LogInformation("Breakout DOWN | {Ltp} < {Low}", tick.Ltp, _state.Candle.Low);

            return new Signal
            {
                Direction = OptionType.PE,
                NiftyLtp = tick.Ltp,
                DetectedAt = ist
            };
        }

        return null;
    }

    public void Reset()
    {
        _signalFired = false;
        _lastCandleEnd = default;
    }
}
