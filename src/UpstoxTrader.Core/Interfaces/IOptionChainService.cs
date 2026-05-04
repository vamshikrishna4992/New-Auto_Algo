using UpstoxTrader.Core.Enums;

namespace UpstoxTrader.Core.Interfaces;

public interface IOptionChainService
{
    Task<string> GetAtmOptionSymbolAsync(int strike, OptionType type, CancellationToken ct);
}
