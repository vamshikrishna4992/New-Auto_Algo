using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Http;

namespace UpstoxTrader.Infrastructure.Services;

public class UpstoxOrderService : IOrderService
{
    private readonly UpstoxHttpClient _http;
    private readonly TradingSettings _trading;
    private readonly ILogger<UpstoxOrderService> _logger;

    public UpstoxOrderService(
        UpstoxHttpClient http,
        IOptions<TradingSettings> trading,
        ILogger<UpstoxOrderService> logger)
    {
        _http = http;
        _trading = trading.Value;
        _logger = logger;
    }

    public async Task<string> PlaceMarketOrderAsync(
        string instrumentKey, int qty, TradeDirection dir, CancellationToken ct)
    {
        var direction = dir == TradeDirection.Buy ? "BUY" : "SELL";

        if (_trading.PaperTrade)
        {
            // Paper mode: log the intent, return a fake order ID.
            // Fill price is NOT available here — it will be set from the
            // first live option tick received by PositionMonitorWorker.
            var paperId = $"PAPER-{Guid.NewGuid():N}";
            _logger.LogInformation(
                "[PAPER] {Dir} {Qty} x {Key} | OrderId: {Id} | " +
                "Fill price will be confirmed from live option tick",
                direction, qty, instrumentKey, paperId);
            return paperId;
        }

        // ── Live order ───────────────────────────────────────────────────────
        var body = new
        {
            quantity           = qty,
            product            = "I",         // intraday
            validity           = "DAY",
            price              = 0,            // 0 = MARKET order, no limit price
            tag                = "ORB",
            instrument_token   = instrumentKey,
            order_type         = "MARKET",
            transaction_type   = direction,
            disclosed_quantity = 0,
            trigger_price      = 0,            // 0 = not a stop order
            is_amo             = false
        };

        try
        {
            var response = await _http.PostAsync<JsonElement>("order/place", body, ct);

            var orderId = ExtractOrderId(response);
            if (string.IsNullOrEmpty(orderId))
            {
                _logger.LogError(
                    "Order placed but no order_id in response: {Json}",
                    response.GetRawText());
                return "";
            }

            _logger.LogInformation(
                "Order placed | {Dir} {Qty} x {Key} | OrderId: {Id}",
                direction, qty, instrumentKey, orderId);
            return orderId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to place {Dir} order for {Key}", direction, instrumentKey);
            throw;
        }
    }

    public async Task<string> GetOrderStatusAsync(string orderId, CancellationToken ct)
    {
        if (orderId.StartsWith("PAPER-"))
            return "complete";

        try
        {
            // Upstox v2: GET /order/details?order_id={orderId}
            var response = await _http.GetAsync<JsonElement>(
                $"order/details?order_id={Uri.EscapeDataString(orderId)}", ct);

            if (response.TryGetProperty("data", out var data) &&
                data.TryGetProperty("status", out var status))
            {
                return status.GetString() ?? "unknown";
            }

            _logger.LogWarning(
                "Order status response missing 'data.status' for {OrderId}: {Json}",
                orderId,
                response.GetRawText()[..Math.Min(500, response.GetRawText().Length)]);
            return "unknown";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get order status for {OrderId}", orderId);
            return "error";
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ExtractOrderId(JsonElement response)
    {
        // Upstox wraps in { "status": "success", "data": { "order_id": "..." } }
        if (response.TryGetProperty("data", out var data) &&
            data.TryGetProperty("order_id", out var oid))
            return oid.GetString() ?? "";

        // Fallback: flat response (older API versions)
        if (response.TryGetProperty("order_id", out var oid2))
            return oid2.GetString() ?? "";

        return "";
    }
}
