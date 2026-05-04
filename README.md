# UpstoxTrader — ORB Strategy Bot

Opening Range Breakout (ORB) trading automation for Nifty 50 options, built on .NET 8.
Pure Worker Service — all output goes to the console and rolling log files.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- An [Upstox developer account](https://developer.upstox.com/)

---

## Step 1 — Get Your Upstox API Credentials

1. Log in at https://developer.upstox.com/
2. Go to **My Apps** → **Create New App**
3. Set **Redirect URL** to exactly: `http://localhost:5000/auth/callback`
4. Note your **Client ID** and **Client Secret**

---

## Step 2 — Configure

Open `src/UpstoxTrader.Worker/appsettings.json` and fill in:

```json
"Upstox": {
  "ClientId":     "YOUR_CLIENT_ID",
  "ClientSecret": "YOUR_CLIENT_SECRET"
}
```

All other defaults are production-ready. See **Configuration** below.

---

## Step 3 — Run

```bash
dotnet run --project src/UpstoxTrader.Worker
```

On first run a browser window opens for Upstox OAuth login.
After you approve, the token is stored and the bot starts automatically.
All output is written to the console and to `logs/upstox-YYYYMMDD.log`.

---

## Configuration Reference

All settings live in `src/UpstoxTrader.Worker/appsettings.json`:

| Key | Default | Description |
|-----|---------|-------------|
| `Trading:CandleMinutes` | `15` | Opening range window in minutes (e.g. 5, 15, 30) |
| `Trading:LotSize` | `75` | Units per order (1 Nifty lot = 75) |
| `Trading:TakeProfitPct` | `10.0` | Exit when option gains this % from entry |
| `Trading:StopLossPct` | `5.0` | Exit when option loses this % from entry |
| `Trading:HardExitTime` | `"15:20"` | Force-exit all positions at this IST time |
| `Trading:PaperTrade` | `true` | `true` = log orders only, `false` = live orders |
| `Nifty:StrikeInterval` | `50` | Nifty strike gap (50 points) |
| `Nifty:InstrumentKey` | `"NSE_INDEX\|Nifty 50"` | Upstox instrument key for Nifty index |

---

## Going Live

> **Warning:** Live trading places real orders and uses real money.

1. In `appsettings.json`, set:
   ```json
   "PaperTrade": false
   ```
2. Restart the application.
3. The console will show `[LIVE]` instead of `[PAPER]` on order logs.

Verify `LotSize`, `TakeProfitPct`, and `StopLossPct` before going live.

---

## Strategy Summary

| Phase | Time (IST) | Action |
|-------|-----------|--------|
| Wait | Before 09:15 | Bot idle |
| Build candle | 09:15 → 09:15+N min | Track Nifty High/Low |
| Detect breakout | After candle closes | Watch for LTP crossing High or Low |
| Enter | On breakout | Buy ATM CE (up) or PE (down), market order |
| Monitor | After entry | Track P&L every tick |
| Exit | First condition met | Market sell |
| Reset | 09:14 next day | Auto-reset all state |

Exit triggers (first one wins):
- Profit ≥ `TakeProfitPct` %
- Loss ≥ `StopLossPct` %
- Time ≥ `HardExitTime`

---

## Solution Structure

```
UpstoxTrader/
├── UpstoxTrader.sln
└── src/
    ├── UpstoxTrader.Core/           Models, interfaces, settings, enums
    ├── UpstoxTrader.Infrastructure/ Auth, WebSocket, HTTP client, order/option services
    ├── UpstoxTrader.Strategy/       Candle builder, breakout detector, exit evaluator
    └── UpstoxTrader.Worker/         Background workers (pure Worker Service)
```

---

## Logs

Rolling daily logs are written to `logs/upstox-YYYYMMDD.log` in the working directory.
Console output uses the same format: `[HH:mm:ss INF] [SourceContext] Message`.
