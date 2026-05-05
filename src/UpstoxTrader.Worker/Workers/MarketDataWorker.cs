using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;
using UpstoxTrader.Infrastructure.Http;

namespace UpstoxTrader.Worker.Workers;

public class MarketDataWorker : BackgroundService
{
    private readonly IMarketFeedService _feed;
    private readonly ORBState _state;
    private readonly Channel<TickData> _niftyChannel;
    private readonly Channel<TickData> _breakoutChannel;
    private readonly Channel<TickData> _optionChannel;
    private readonly TokenManager _tokenManager;
    private readonly UpstoxHttpClient _http;
    private readonly NiftySettings _nifty;
    private readonly TradingSettings _trading;
    private readonly ILogger<MarketDataWorker> _logger;

    private static readonly TimeZoneInfo _istZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    public MarketDataWorker(
        IMarketFeedService feed,
        ORBState state,
        [FromKeyedServices("nifty-channel")] Channel<TickData> niftyChannel,
        [FromKeyedServices("breakout-channel")] Channel<TickData> breakoutChannel,
        [FromKeyedServices("option-channel")] Channel<TickData> optionChannel,
        TokenManager tokenManager,
        UpstoxHttpClient http,
        IOptions<NiftySettings> nifty,
        IOptions<TradingSettings> trading,
        ILogger<MarketDataWorker> logger)
    {
        _feed = feed;
        _state = state;
        _niftyChannel = niftyChannel;
        _breakoutChannel = breakoutChannel;
        _optionChannel = optionChannel;
        _tokenManager = tokenManager;
        _http = http;
        _nifty = nifty.Value;
        _trading = trading.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataWorker starting — waiting for token...");

        while (!_tokenManager.HasToken && !stoppingToken.IsCancellationRequested)
            await Task.Delay(2000, stoppingToken);

        if (stoppingToken.IsCancellationRequested) return;

        _logger.LogInformation("Token available — connecting to market feed");

        await SeedOpeningCandleIfNeededAsync(stoppingToken);

        try
        {
            await _feed.ConnectAsync([_nifty.InstrumentKey], stoppingToken);

            await foreach (var tick in _feed.GetTickStreamAsync(stoppingToken))
            {
                try
                {
                    ProcessTick(tick);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing tick for {Key}", tick.InstrumentKey);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarketDataWorker fatal error");
        }
    }

   private void ProcessTick(TickData tick)
{
    // NIFTY routing
    if (tick.InstrumentKey == _nifty.InstrumentKey)
    {
        _state.PreviousNiftyLtp = _state.LastNiftyLtp;
        _state.LastNiftyLtp = tick.Ltp;

        _niftyChannel.Writer.TryWrite(tick);
        _breakoutChannel.Writer.TryWrite(tick);
    }

    // ✅ FIXED: Always push option ticks
    if (tick.InstrumentKey.StartsWith("NSE_FO"))
    {
        _optionChannel.Writer.TryWrite(tick);

        _logger.LogInformation(
            "OPTION TICK → {Key} | LTP: {Ltp}",
            tick.InstrumentKey,
            tick.Ltp);
    }
}
    public async Task SubscribeToOptionAsync(string instrumentKey, CancellationToken ct)
    {
        _logger.LogInformation("Subscribing to option feed: {Key}", instrumentKey);
        await _feed.SubscribeAsync([instrumentKey], ct);
    }

    private async Task SeedOpeningCandleIfNeededAsync(CancellationToken ct)
    {
        var ist = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istZone);
        var today = DateOnly.FromDateTime(ist);
        var orbStart = new DateTime(today.Year, today.Month, today.Day, 9, 15, 0);
        var orbEnd   = orbStart.AddMinutes(_trading.CandleMinutes);

        if (ist < orbEnd) return; // still within opening candle — live ticks will build it
        if (_state.CandleReady) return;

        _logger.LogInformation("App started after ORB window — fetching 9:15 candle from history");

        try
        {
            // Intraday API only supports 1minute and 30minute intervals.
            // Fetch 1-minute candles and aggregate the 09:15–09:30 window.
            var url = $"historical-candle/intraday/{Uri.EscapeDataString(_nifty.InstrumentKey)}/1minute";
            var response = await _http.GetAsync<JsonElement>(url, ct);

            if (!response.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("candles", out var candlesEl) ||
                candlesEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Unexpected historical candle response — live ticks will be used");
                return;
            }

            var datePrefix = $"{today:yyyy-MM-dd}T";
            decimal open = 0, high = 0, low = decimal.MaxValue, close = 0;
            bool found = false;

            // Candles are descending (most recent first); collect all minutes in [09:15, 09:30)
            foreach (var row in candlesEl.EnumerateArray())
            {
                var arr = row.EnumerateArray().ToArray();
                if (arr.Length < 5) continue;

                var ts = arr[0].GetString() ?? "";
                if (!ts.StartsWith(datePrefix)) continue;

                // Parse HH:mm from the timestamp (format: yyyy-MM-ddTHH:mm:ss+05:30)
                if (!TimeSpan.TryParse(ts[11..16], out var t)) continue;
                if (t < new TimeSpan(9, 15, 0) || t >= orbEnd.TimeOfDay) continue;

                var o = arr[1].GetDecimal();
                var h = arr[2].GetDecimal();
                var l = arr[3].GetDecimal();
                var c = arr[4].GetDecimal();

                if (!found)
                {
                    // First match in descending order = last minute of the window = close
                    close = c;
                    found = true;
                }

                if (h > high) high = h;
                if (l < low)  low  = l;
                open = o; // last assigned = earliest minute = 09:15 open
            }

            if (!found)
            {
                _logger.LogWarning("No 1-minute candles found for 09:15–09:30 — may be a holiday or pre-market start");
                return;
            }

            var candle = new OpeningCandle
{
    Open  = open,
    High  = high,
    Low   = low,
    Close = close,
    CandleStart = orbStart,
    CandleEnd   = orbEnd
};

// 🔥 IMPORTANT: initialize both
_state.Candle = candle;
_state.PreviousCandle = candle;
_state.CandleReady = true;

            _logger.LogInformation(
                "Opening candle seeded from history [09:15–09:30] H:{High} L:{Low} O:{Open} C:{Close}",
                high, low, open, close);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch historical opening candle — proceeding without seed");
        }
    }
}
