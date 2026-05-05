# Chat Template — Part 2: Strategy Layer (Signal Engine)

## Project Overview
UpstoxTrader — C# .NET 8 algo trading bot for Nifty 50 options using the Upstox API.
Architecture: 4 VS projects — Core, Infrastructure, Strategy, Worker.
This chat owns **Part 2: Strategy Layer** only.

## What This Layer Does
Responsible for:
- Building the Opening Range Breakout (ORB) candle from live Nifty ticks
- Detecting when Nifty LTP crosses the candle's high or low (breakout signal)
- Calculating the ATM (At-The-Money) strike from the current Nifty price
- Evaluating exit conditions (target %, stop loss %, hard exit time, manual exit)

## Data Flow Into This Layer
`BreakoutWorker` (Part 4's worker) reads from `breakout-channel` and passes each
`TickData` into `ORBCandleBuilder.ProcessTick()` then `BreakoutDetector.Detect()`.
The strategy classes are pure logic — no I/O, no HTTP, no async.

## Current Files in This Layer

### `src/UpstoxTrader.Strategy/ATMCalculator.cs`
```csharp
public static class ATMCalculator {
    public static int Calculate(decimal ltp, int strikeInterval)
        => (int)Math.Round(ltp / strikeInterval) * strikeInterval;
}
```
Rounds Nifty LTP to the nearest strike interval (50 points).

### `src/UpstoxTrader.Strategy/ORBCandleBuilder.cs`
Builds OHLC candles from live ticks. Key logic:
- Ignores ticks before 9:15 IST
- Groups ticks into `CandleMinutes`-wide windows (default: 2 min)
- When a new window starts, finalizes the previous candle into `ORBState.PreviousCandle`
- Sets `ORBState.CandleReady = true` on first finalization
- In `FirstOnly` mode: ignores ticks once `CandleReady` is true
- In `AllCandles` mode: keeps building candles, each closed candle becomes the new reference
- `Reset()` clears OHLC and candle timestamps (called by `DailyResetWorker`)
- Live tracking: updates `ORBState.CandleHighSoFar` / `CandleLowSoFar` / `CandleEndTime`

### `src/UpstoxTrader.Strategy/BreakoutDetector.cs`
Detects breakouts from `ORBState.PreviousCandle`. Key logic:
- Returns `null` if: no trade taken today, candle not ready, signal already fired
- **Buffer**: `decimal buffer = 1` — LTP must exceed `candle.High + 1` or go below `candle.Low - 1`
- UP breakout → `Signal { Direction = CE }`, DOWN breakout → `Signal { Direction = PE }`
- In `AllCandles` mode: resets `_signalFired` when a new candle becomes the reference,
  and blocks signals at/after `SignalCutoffTime` (15:00 IST)
- In `FirstOnly` mode: fires signal once per day only
- `Reset()` clears `_signalFired` and `_lastCandleEnd` (called by `DailyResetWorker`)

### `src/UpstoxTrader.Strategy/ExitEvaluator.cs`
Called tick-by-tick by `PositionMonitorWorker`. Returns exit reason string or null.
- **Percent mode**: exits if `(ltp - entry) / entry * 100 >= TakeProfitPct` (10%)
  or `<= -StopLossPct` (-5%)
- **Points mode**: exits if `ltp - entry >= TakeProfitPoints` or `<= -StopLossPoints`
- **Hard exit**: exits at `HardExitTime` (15:20 IST) regardless of P&L
- **Manual exit**: exits if `ORBState.ForceExitRequested == true`
- Returns: `"Target Hit"`, `"Stop Loss Hit"`, `"Timed Out at 15:20"`, `"Manual Exit"`, or `null`

## Key Models (from Core — read-only, do not change)
```csharp
// ORBState.cs — shared singleton, all strategy reads/writes go through here
public class ORBState {
    public decimal LastNiftyLtp { get; set; }
    public decimal PreviousNiftyLtp { get; set; }
    public OpeningCandle? Candle { get; set; }         // current in-progress candle
    public OpeningCandle? PreviousCandle { get; set; } // last CLOSED candle (breakout reference)
    public bool CandleReady { get; set; }
    public decimal CandleHighSoFar { get; set; }
    public decimal CandleLowSoFar { get; set; }
    public DateTime? CandleEndTime { get; set; }
    public Signal? ActiveSignal { get; set; }
    public Position? ActivePosition { get; set; }
    public bool TradeTakenToday { get; set; }
    public bool ForceExitRequested { get; set; }
    public List<string> EventLog { get; }              // in-memory event log, max 100 entries
    public void Reset() { ... }                        // called by DailyResetWorker
    public static DateTime GetIST() { ... }
}

// OpeningCandle.cs
public class OpeningCandle {
    public decimal High, Low, Open, Close { get; set; }
    public DateTime CandleStart, CandleEnd { get; set; }
}

// Signal.cs
public class Signal {
    public OptionType Direction { get; set; }  // CE or PE
    public decimal NiftyLtp { get; set; }
    public int AtmStrike { get; set; }
    public string OptionSymbol { get; set; }
    public DateTime DetectedAt { get; set; }
}
```

## Configuration (appsettings.json)
```json
"Trading": {
  "CandleMinutes": 2,
  "LotSize": 65,
  "ExitMode": "Percent",
  "TakeProfitPct": 10.0,
  "StopLossPct": 5.0,
  "TakeProfitPoints": 10,
  "StopLossPoints": 5,
  "HardExitTime": "15:20",
  "PaperTrade": false,
  "CandleMode": "AllCandles",
  "SignalCutoffTime": "15:00"
},
"Nifty": {
  "InstrumentKey": "NSE_INDEX|Nifty 50",
  "StrikeInterval": 50
}
```

## Known Issues / Notes
- `buffer = 1` is hardcoded in `BreakoutDetector` — not in config.
- `ORBState.Candle` and `ORBState.PreviousCandle` are both set in `FinalizeCandle`;
  `Candle` is described as "UI/debug only" — `PreviousCandle` is the authoritative reference.
- `BreakoutDetector` checks `_state.PreviousNiftyLtp == 0` before firing — this guards
  against signals on the very first tick before two prices exist.
- `ExitEvaluator` requires `EntryPrice > 0` — entry price is confirmed from the first
  live option tick (set by `PositionMonitorWorker`), not from the order placement.

## What You Can Work On in This Chat
- Move `buffer = 1` into `TradingSettings` as a configurable `BreakoutBuffer`
- Add multi-candle confirmation logic (e.g., 2 consecutive closes above high)
- Add volume or momentum filters to `BreakoutDetector`
- Improve `ExitEvaluator` with trailing stop loss logic
- Add partial exit capability (e.g., exit 50% at target, trail the rest)
- Add signal confidence scoring
- Unit test `ORBCandleBuilder`, `BreakoutDetector`, `ExitEvaluator` in isolation
  (they are pure classes — no mocking needed, just inject test doubles for `ORBState`)
