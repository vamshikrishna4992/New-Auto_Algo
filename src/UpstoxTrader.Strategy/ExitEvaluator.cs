using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;

namespace UpstoxTrader.Strategy;

public class ExitEvaluator
{
    private readonly TradingSettings _settings;
    private readonly ORBState _state;

    public ExitEvaluator(IOptions<TradingSettings> settings, ORBState state)
    {
        _settings = settings.Value;
        _state = state;
    }

    public string? Evaluate(Position position, DateTime istNow)
    {
        var entry = position.EntryPrice;
        var ltp = position.CurrentLtp;

        // Safety check
        if (entry <= 0 || ltp <= 0)
            return null;

        // =========================
        // 🎯 PERCENT MODE
        // =========================
        if (_settings.ExitMode == "Percent")
        {
            var changePct = ((ltp - entry) / entry) * 100;

            if (changePct >= _settings.TakeProfitPct)
                return "Target Hit";

            if (changePct <= -_settings.StopLossPct)
                return "Stop Loss Hit";
        }

        // =========================
        // 🎯 POINTS MODE
        // =========================
        else if (_settings.ExitMode == "Points")
        {
            var diff = ltp - entry;

            if (diff >= _settings.TakeProfitPoints)
                return "Target Hit";

            if (diff <= -_settings.StopLossPoints)
                return "Stop Loss Hit";
        }

        // =========================
        // ⏰ HARD EXIT (time-based)
        // =========================
        if (TimeSpan.TryParse(_settings.HardExitTime, out var hardExit) &&
            istNow.TimeOfDay >= hardExit)
        {
            return "Timed Out at " + _settings.HardExitTime;
        }

        // =========================
        // 🛑 MANUAL EXIT
        // =========================
        if (_state.ForceExitRequested)
        {
            return "Manual Exit";
        }

        return null;
    }
}