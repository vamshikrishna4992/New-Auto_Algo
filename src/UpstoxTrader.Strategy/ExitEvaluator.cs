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
        if (position.ProfitPct >= _settings.TakeProfitPct)
            return "Target Hit";

        if (position.LossPct >= _settings.StopLossPct)
            return "Stop Loss Hit";

        if (TimeSpan.TryParse(_settings.HardExitTime, out var hardExit) &&
            istNow.TimeOfDay >= hardExit)
            return "Timed Out at 15:20";

        if (_state.ForceExitRequested)
            return "Manual Exit";

        return null;
    }
}
