# Chat Template — Part 4: Risk & Lifecycle Layer (Monitor + Reset)

## Project Overview
UpstoxTrader — C# .NET 8 algo trading bot for Nifty 50 options using the Upstox API.
Architecture: 4 VS projects — Core, Infrastructure, Strategy, Worker.
This chat owns **Part 4: Risk & Lifecycle Layer** only.

## What This Layer Does
Responsible for:
- Monitoring open positions tick-by-tick (P&L calculation)
- Confirming entry price from first live option tick
- Evaluating exit conditions and executing exit orders
- Resetting all state daily at 9:14 IST for the next trading day
- Wiring all workers and services together (Program.cs / DI setup)

## How This Layer Fits In
After `BreakoutWorker` (Part 2/3) places a BUY order and writes `ORBState.ActivePosition`,
`PositionMonitorWorker` takes over: it reads `option-channel` ticks, sets entry price,
calculates P&L, and calls `ExitEvaluator` (Part 2) to decide when to exit.
When exit fires, it calls `IOrderService.PlaceMarketOrderAsync(SELL)` (Part 3) and
closes the position by nulling `ORBState.ActivePosition`.

## Current Files in This Layer

### `src/UpstoxTrader.Worker/Workers/PositionMonitorWorker.cs`
`BackgroundService`. Main loop:
1. Polls every 200ms if no open position → waits for `ORBState.ActivePosition` to appear
2. Reads option ticks from `option-channel` (200ms read timeout)
3. On first tick: sets `Position.EntryPrice = tick.Ltp`, sets `_entryPriceSet = true`
4. Every tick: updates `Position.CurrentLtp`, logs P&L (%, ₹ total)
5. Every tick + on read timeout: calls `ExitEvaluator.Evaluate(position, ist)`
6. On exit signal: calls `ExecuteExitAsync()`

**ExecuteExitAsync():**
- Places SELL market order via `IOrderService`
- Sets `Position.ExitPrice`, `ExitTime`, `ExitReason`, `Status`
- Calculates final P&L: `(ExitPrice - EntryPrice) * Quantity`
- Logs exit to `ORBState.EventLog`
- Nulls `ORBState.ActivePosition`, resets `ForceExitRequested`, `_entryPriceSet`

**Exit status mapping:**
```
"Target Hit"          → PositionStatus.TargetHit
"Stop Loss Hit"       → PositionStatus.StopLossHit
"Timed Out at 15:20"  → PositionStatus.TimedOut
"Manual Exit"         → PositionStatus.ManualExit
anything else         → PositionStatus.TimedOut
```

### `src/UpstoxTrader.Worker/Workers/DailyResetWorker.cs`
`BackgroundService`. Calculates time until 9:14 IST → `Task.Delay` → calls:
- `ORBState.Reset()` — clears all trade state, candle, signal, position, event log
- `ORBCandleBuilder.Reset()` — clears OHLC and candle timestamps
- `BreakoutDetector.Reset()` — clears signal fired flag and last candle reference
- Logs "Daily reset executed at 09:14 IST"
- Waits 24h, then recalculates for next day

### `src/UpstoxTrader.Worker/Program.cs`
Entry point. Configures Serilog (console + rolling file at `logs/upstox-{date}.log`),
then registers all services:

**Settings:**
- `UpstoxSettings` → `"Upstox"` config section
- `TradingSettings` → `"Trading"` config section
- `NiftySettings` → `"Nifty"` config section

**Singletons:**
- `ORBState` — shared state across all workers
- `BreakoutDetector`, `ORBCandleBuilder`, `ExitEvaluator` — strategy classes
- `TokenManager` — OAuth token store
- `MarketDataWorker` — registered as singleton so `BreakoutWorker` can call
  `SubscribeToOptionAsync` on it directly

**Keyed Channels (bounded, drop-oldest):**
- `"nifty-channel"` — cap 2000 (unused in current code, reserved)
- `"breakout-channel"` — cap 2000 (consumed by `BreakoutWorker`)
- `"option-channel"` — cap 2000 (consumed by `PositionMonitorWorker`)

**Hosted Services (startup order):**
1. `TokenService` — OAuth login
2. `MarketDataWorker` — WebSocket feed + tick routing
3. `BreakoutWorker` — candle build + signal detection + order entry
4. `PositionMonitorWorker` — P&L monitor + exit execution
5. `DailyResetWorker` — daily state reset

**Infrastructure:**
- `services.AddInfrastructure(config)` — registers `UpstoxHttpClient`, `IOrderService`,
  `IOptionChainService`, `IMarketFeedService` (see `InfrastructureExtensions.cs`)

## Key Models (from Core — read-only)
```csharp
public class ORBState {
    public Position? ActivePosition { get; set; }
    public Signal? ActiveSignal { get; set; }
    public bool TradeTakenToday { get; set; }
    public bool ForceExitRequested { get; set; }
    public bool BotRunning { get; set; }
    public List<string> EventLog { get; }    // max 100 entries, newest first
    public void Reset() { ... }
    public void RequestForceExit() => ForceExitRequested = true;
    public static DateTime GetIST() { ... }
}

public class Position {
    public string OrderId { get; set; }
    public string InstrumentKey { get; set; }
    public decimal EntryPrice { get; set; }    // confirmed from first option tick
    public decimal CurrentLtp { get; set; }
    public decimal ExitPrice { get; set; }
    public int Quantity { get; set; }
    public PositionStatus Status { get; set; } // Open, TargetHit, StopLossHit, TimedOut, ManualExit
    public string ExitReason { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
}

public enum PositionStatus { Open, TargetHit, StopLossHit, TimedOut, ManualExit }
```

## Configuration (appsettings.json)
```json
"Trading": {
  "ExitMode": "Percent",
  "TakeProfitPct": 10.0,
  "StopLossPct": 5.0,
  "TakeProfitPoints": 10,
  "StopLossPoints": 5,
  "HardExitTime": "15:20",
  "PaperTrade": false,
  "CandleMode": "AllCandles",
  "SignalCutoffTime": "15:00"
}
```

## Known Issues / Notes
- Entry price is set from the first WebSocket option tick AFTER subscription, not from
  the actual order fill. In live mode this can differ from the real fill price if there's
  a delay between order placement and first tick.
- `PositionMonitorWorker` uses a 200ms read timeout + `Task.Delay(200)` loop — if option
  ticks arrive faster there may be queued ticks; if slower the exit evaluator still fires
  on timeout (using last known LTP).
- No trade history persistence — all position data is in-memory. Restart = data loss.
- `DailyResetWorker` waits exactly 24h after the reset fires, then recalculates. This
  means on weekends/holidays the reset still fires at 9:14 IST every day.
- The `nifty-channel` is registered and injected into `MarketDataWorker` but is not
  consumed by any worker currently.
- `_state.PreviousCandle` is not reset in `ORBState.Reset()` — it is cleared via
  `ORBCandleBuilder.Reset()` which nulls the `_currentCandleStart` fields but does NOT
  null `ORBState.PreviousCandle` directly. The first new day candle overwrites it.

## What You Can Work On in This Chat
- Add trade history persistence (write to SQLite or a JSON log file on each exit)
- Add a simple HTTP dashboard/API endpoint to expose `ORBState` (current LTP, P&L,
  event log, bot status) — Minimal API or Blazor Server
- Add manual exit trigger via HTTP endpoint (`POST /force-exit`)
- Add weekend/holiday detection so `DailyResetWorker` skips non-trading days
- Consume the `nifty-channel` for a Nifty price dashboard
- Improve entry price confirmation (wait for order `complete` status, then use fill price)
- Add daily P&L summary logging at end of session (15:30 IST)
- Add Telegram/email notification on trade entry and exit
