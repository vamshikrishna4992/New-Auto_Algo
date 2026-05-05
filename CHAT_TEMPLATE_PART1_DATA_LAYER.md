# Chat Template — Part 1: Data Layer (Market Feed)

## Project Overview
UpstoxTrader — C# .NET 8 algo trading bot for Nifty 50 options using the Upstox API.
Architecture: 4 VS projects — Core, Infrastructure, Strategy, Worker.
This chat owns **Part 1: Data Layer** only.

## What This Layer Does
Responsible for:
- OAuth login (open browser → receive callback → exchange code → store token)
- Connecting to Upstox WebSocket (gRPC/protobuf) for real-time Nifty ticks
- HTTP LTP polling as a fallback
- Routing incoming ticks into 3 named `Channel<TickData>` queues:
  - `nifty-channel` → raw Nifty price (used by BreakoutWorker for candle building)
  - `breakout-channel` → same Nifty tick (used by BreakoutWorker for signal detection)
  - `option-channel` → option ticks after a trade is placed (used by PositionMonitorWorker)

## Current Files in This Layer

### `src/UpstoxTrader.Infrastructure/Auth/TokenManager.cs`
Thread-safe in-memory token store. `GetToken()`, `SetToken()`, `HasToken`.

### `src/UpstoxTrader.Infrastructure/Auth/TokenService.cs`
`IHostedService`. On startup: opens browser to Upstox OAuth URL, listens on
`http://localhost:5000/callback/` (3-min timeout), exchanges auth code for access token,
stores in `TokenManager`.

### `src/UpstoxTrader.Infrastructure/WebSocket/UpstoxWebSocketClient.cs`
Implements `IMarketFeedService`. Connects to Upstox v3 feed via authorized WebSocket URI.
- Calls `/v3/feed/market-data-feed/authorize` to get a short-lived signed URI
- Sends `sub` subscription message (mode: `ltpc`, keys: `["NSE_INDEX|Nifty 50"]`)
- `ReceiveLoopAsync` reads binary frames, parses protobuf `FeedResponse`
- Extracts LTP from `FullFeed.MarketFf`, `FullFeed.IndexFf`, or `Ltpc`
- Drops frames with LTP ≤ 0
- Auto-reconnects (up to 10 attempts, 5s delay) on disconnect
- Writes `TickData` to a bounded `Channel<TickData>` (5000 cap, drop-oldest)
- `GetTickStreamAsync()` → `IAsyncEnumerable<TickData>` consumed by `MarketDataWorker`

### `src/UpstoxTrader.Infrastructure/Services/UpstoxLtpPollingService.cs`
Implements `IMarketFeedService`. Polls `market-quote/ltp` every 1 second as fallback.
Same `IAsyncEnumerable<TickData>` interface as WebSocket client.

### `src/UpstoxTrader.Infrastructure/Http/UpstoxHttpClient.cs`
Thin wrapper around `HttpClient`. Injects Bearer token on every request.
Base URL: `https://api.upstox.com/v2/`. Methods: `GetAsync<T>`, `PostAsync<T>`.

### `src/UpstoxTrader.Worker/Workers/MarketDataWorker.cs`
`BackgroundService`. Waits for token → calls `SeedOpeningCandleIfNeededAsync` →
connects feed → reads tick stream → routes each tick:
- Nifty ticks → `nifty-channel` + `breakout-channel`, updates `ORBState.LastNiftyLtp`
- `NSE_FO` ticks → `option-channel`
- `SubscribeToOptionAsync(key)` — called by `BreakoutWorker` after a trade is placed
- `SeedOpeningCandleIfNeededAsync` — if app starts after ORB window (9:15+),
  fetches 1-minute historical candles and aggregates into `ORBState.PreviousCandle`

## Key Models (from Core, read-only — do not change these)
```csharp
// TickData.cs
public record TickData(string InstrumentKey, decimal Ltp, decimal PreviousLtp,
    long Volume, long OI, DateTime Timestamp);

// IMarketFeedService.cs
public interface IMarketFeedService {
    Task ConnectAsync(string[] instrumentKeys, CancellationToken ct);
    Task SubscribeAsync(string[] instrumentKeys, CancellationToken ct);
    IAsyncEnumerable<TickData> GetTickStreamAsync(CancellationToken ct);
}
```

## Configuration (appsettings.json)
```json
"Upstox": {
  "ClientId": "85095a03-2bd8-4df5-a745-80760dfff3af",
  "ClientSecret": "oql2tsc6ns",
  "RedirectUri": "http://localhost:5000/callback",
  "BaseUrl": "https://api.upstox.com/v2",
  "WebSocketUrl": "wss://api.upstox.com/v2/feed/market-data-feed"
},
"Nifty": {
  "InstrumentKey": "NSE_INDEX|Nifty 50",
  "StrikeInterval": 50
}
```

## Known Issues / Notes
- WebSocket uses v3 authorize endpoint but connects to v2 WebSocket URL — the authorized
  URI returned by the authorize call is what's actually used, not the config URL.
- LTP=0 frames are logged and dropped (protobuf field mismatch guard).
- `_tickCount` is incremented twice per tick (bug — once in ProcessFrame, once in the loop).
- Historical candle seed uses 1-minute intervals aggregated manually.
- `MarketDataWorker` is registered as a singleton so `BreakoutWorker` can call
  `SubscribeToOptionAsync` on it; all other workers are transient hosted services.

## What You Can Work On in This Chat
- Improve WebSocket reconnect logic or backoff strategy
- Fix the `_tickCount` double-increment bug
- Add token refresh / expiry handling (current token lasts ~1 day)
- Add volume/OI to tick processing
- Improve historical candle seeding (handle holidays, weekends)
- Switch from LTP polling to a more efficient mechanism
- Add feed health monitoring / dead-feed detection
