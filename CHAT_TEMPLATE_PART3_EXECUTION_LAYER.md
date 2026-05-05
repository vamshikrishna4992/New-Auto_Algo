# Chat Template — Part 3: Execution Layer (Order Management)

## Project Overview
UpstoxTrader — C# .NET 8 algo trading bot for Nifty 50 options using the Upstox API.
Architecture: 4 VS projects — Core, Infrastructure, Strategy, Worker.
This chat owns **Part 3: Execution Layer** only.

## What This Layer Does
Responsible for:
- Looking up the correct option instrument key from the Upstox option chain API
- Placing BUY (entry) and SELL (exit) market orders via Upstox v2 REST API
- Paper trading mode (simulated orders with fake order IDs, no real API calls)
- Confirming order status after placement
- Resolving lot size from config

## How This Layer Is Triggered
`BreakoutWorker` (in the Worker project) calls this layer after a breakout signal:
1. `IOptionChainService.GetAtmOptionSymbolAsync(strike, direction)` → gets instrument key + lot size
2. `IOrderService.PlaceMarketOrderAsync(key, qty, Buy)` → places entry order
3. `PositionMonitorWorker` calls `IOrderService.PlaceMarketOrderAsync(key, qty, Sell)` on exit

## Current Files in This Layer

### `src/UpstoxTrader.Infrastructure/Services/UpstoxOrderService.cs`
Implements `IOrderService`. Two methods:

**PlaceMarketOrderAsync(instrumentKey, qty, direction, ct)**
- Paper mode: logs intent, returns `"PAPER-{guid}"` immediately
- Live mode: POSTs to `order/place` with body:
  ```json
  { "quantity": qty, "product": "I", "validity": "DAY", "price": 0,
    "tag": "ORB", "instrument_token": key, "order_type": "MARKET",
    "transaction_type": "BUY|SELL", "disclosed_quantity": 0,
    "trigger_price": 0, "is_amo": false }
  ```
- Extracts `order_id` from `{ "data": { "order_id": "..." } }` response
- Returns empty string on parse failure (caller should check)

**GetOrderStatusAsync(orderId, ct)**
- Paper orders (`PAPER-*`): always returns `"complete"`
- Live: GETs `order/details?order_id={id}`, extracts `data.status`

### `src/UpstoxTrader.Infrastructure/Services/UpstoxOptionChainService.cs`
Implements `IOptionChainService`. One public method:

**GetAtmOptionSymbolAsync(strike, optionType, ct)**
- First call: runs `DiscoverAsync()` to find a valid (underlyingKey, expiry) pair
  - Tries 5 key variants × ~15+ expiry dates (last Thu/Mon/Wed of current + next 2 months
    + weekly Thursdays/Mondays/Wednesdays for 6 weeks)
  - Caches the discovered (key, expiry) for the entire session
- Fetches option chain via `option/chain?instrument_key=...&expiry_date=...`
  - Strike cache: 60-second TTL, keyed by expiry
- Finds nearest strike in chain to the requested ATM strike
- Returns `OptionInstrument { InstrumentKey, LotSize, Strike, Expiry }`
- Fallback: if chain discovery fails entirely, builds a key with pattern
  `NSE_FO|NIFTY{DDMMMYY}{STRIKE}{CE/PE}` (may be invalid)
- Lot size always comes from `TradingSettings.LotSize` (config), not the chain

### `src/UpstoxTrader.Infrastructure/Http/UpstoxHttpClient.cs`
Used by both services above. Injects Bearer token on every call.
Base URL: `https://api.upstox.com/v2/`.
Throws `HttpRequestException` on non-2xx with the response body in the message.

## Key Interfaces (from Core — do not change)
```csharp
public interface IOrderService {
    Task<string> PlaceMarketOrderAsync(string instrumentKey, int qty,
        TradeDirection dir, CancellationToken ct);
    Task<string> GetOrderStatusAsync(string orderId, CancellationToken ct);
}

public interface IOptionChainService {
    Task<OptionInstrument> GetAtmOptionSymbolAsync(int strike, OptionType type,
        CancellationToken ct);
}
```

## Key Models (from Core — read-only)
```csharp
public class OptionInstrument {
    public string InstrumentKey { get; set; }  // e.g. "NSE_FO|NIFTY28MAY2523900CE"
    public int LotSize { get; set; }
    public int Strike { get; set; }
    public DateTime Expiry { get; set; }
}

public class Position {
    public string OrderId { get; set; }
    public string OptionSymbol { get; set; }
    public string InstrumentKey { get; set; }
    public decimal EntryPrice { get; set; }    // set from FIRST live option tick, not order
    public decimal CurrentLtp { get; set; }
    public decimal ExitPrice { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public int Quantity { get; set; }
    public PositionStatus Status { get; set; }
    public string ExitReason { get; set; }
}

public enum TradeDirection { Buy, Sell }
public enum OptionType { CE, PE }
```

## Configuration (appsettings.json)
```json
"Upstox": {
  "ClientId": "85095a03-2bd8-4df5-a745-80760dfff3af",
  "BaseUrl": "https://api.upstox.com/v2"
},
"Trading": {
  "LotSize": 65,
  "PaperTrade": false
},
"Nifty": {
  "InstrumentKey": "NSE_INDEX|Nifty 50",
  "StrikeInterval": 50
}
```

## Known Issues / Notes
- `EntryPrice` on `Position` is NOT set at order placement — it's set from the first
  live option tick in `PositionMonitorWorker`. In paper mode this means entry price
  = first WebSocket tick price after subscription, not the order fill price.
- `GetOrderStatusAsync` is defined but never called after order placement in the current
  flow — there's no fill-confirmation step.
- Option chain discovery can make many HTTP calls on first trade (5 key variants × 15+
  expiry dates) — this adds latency to the first signal→order path.
- `ResolveLotSize()` always returns `_trading.LotSize` — lot size from the chain data
  is ignored. Nifty options lot size changed to 75 in some expiry cycles; keep in sync
  with actual exchange lot sizes.
- Fallback instrument key (`BuildInstrumentKey`) uses date format `ddMMMyy` (e.g.
  `28MAY25`) — verify this matches current Upstox naming conventions.

## What You Can Work On in This Chat
- Add order fill confirmation (poll `GetOrderStatusAsync` after BUY, set entry price
  from confirmed fill rather than first tick)
- Cache or pre-warm the option chain discovery before market open to reduce first-trade latency
- Add retry logic specifically for order placement (Polly policy)
- Handle lot size changes (read actual lot size from option chain response)
- Add limit order support (alternative to pure MARKET orders)
- Add order rejection handling (return type currently `string` — consider a result type)
- Write integration tests against Upstox sandbox API
