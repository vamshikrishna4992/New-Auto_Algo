using UpstoxTrader.Core.Models;

namespace UpstoxTrader.Core.Interfaces;

public interface IMarketFeedService
{
    Task ConnectAsync(string[] instrumentKeys, CancellationToken ct);
    Task SubscribeAsync(string[] instrumentKeys, CancellationToken ct);
    IAsyncEnumerable<TickData> GetTickStreamAsync(CancellationToken ct);
}
