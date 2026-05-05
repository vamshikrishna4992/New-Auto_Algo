using UpstoxTrader.Core.Enums;

namespace UpstoxTrader.Core.Interfaces;

public interface IOptionChainService
{
    Task<OptionInstrument> GetAtmOptionSymbolAsync(
        int strike,
        OptionType type,
        CancellationToken ct);
}
