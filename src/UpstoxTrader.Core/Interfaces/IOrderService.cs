using UpstoxTrader.Core.Enums;

namespace UpstoxTrader.Core.Interfaces;

public interface IOrderService
{
    Task<string> PlaceMarketOrderAsync(string instrumentKey, int qty,
        TradeDirection dir, CancellationToken ct);

    Task<string> GetOrderStatusAsync(string orderId, CancellationToken ct);
}
