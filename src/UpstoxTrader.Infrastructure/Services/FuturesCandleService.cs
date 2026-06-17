using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Infrastructure.Auth;
using UpstoxTrader.Infrastructure.Http;

namespace UpstoxTrader.Infrastructure.Services;

public record FuturesCandle(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    DateTime Timestamp);

public class FuturesCandleService
{
    private readonly UpstoxHttpClient _http;
    private readonly TokenManager _tokenManager;
    private readonly ILogger<FuturesCandleService> _logger;

    private string? _futuresKey;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    private static readonly TimeZoneInfo _istZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    private static readonly TimeSpan _marketOpen = new(9, 15, 0);
    private const int CandleMinutes = 5;

    // Upstox instruments CDN — tried with auth/UA headers as fallback
    private static readonly string[] _instrumentsUrls =
    [
        "https://assets.upstox.com/market-quote/instruments/exchange/NSE_FO.json.gz",
        "https://assets.upstox.com/market-quote/instruments/exchange/complete.json.gz",
    ];

    public FuturesCandleService(
        UpstoxHttpClient http,
        TokenManager tokenManager,
        ILogger<FuturesCandleService> logger)
    {
        _http = http;
        _tokenManager = tokenManager;
        _logger = logger;
    }

    // configOverride: pass VolumeStrategySettings.FuturesInstrumentKey if set
    public async Task<string?> GetFuturesKeyAsync(string configOverride, CancellationToken ct)
    {
        // Use manual config key immediately if provided
        if (!string.IsNullOrWhiteSpace(configOverride))
        {
            _futuresKey = configOverride;
            _logger.LogInformation("[VOL] Using configured futures key: {Key}", _futuresKey);
            return _futuresKey;
        }

        if (_futuresKey != null) return _futuresKey;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_futuresKey != null) return _futuresKey;
            _futuresKey = await DiscoverFuturesKeyAsync(ct);
            return _futuresKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    // Returns last 2 completed 5-minute candles (index 0 = most recent, index 1 = previous)
    public async Task<List<FuturesCandle>> GetLast2CandlesAsync(string configOverride, CancellationToken ct)
    {
        var key = await GetFuturesKeyAsync(configOverride, ct);
        if (key == null)
        {
            _logger.LogWarning("[VOL] Futures key not discovered — skipping candle fetch");
            return [];
        }

        var url = $"historical-candle/intraday/{Uri.EscapeDataString(key)}/1minute";

        try
        {
            var response = await _http.GetAsync<JsonElement>(url, ct);

            if (!response.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("candles", out var candlesEl) ||
                candlesEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("[VOL] Unexpected futures candle response");
                return [];
            }

            var minuteCandles = ParseMinuteCandles(candlesEl);
            var fiveMinCandles = AggregateToFiveMin(minuteCandles);

            if (fiveMinCandles.Count >= 2)
                _logger.LogInformation(
                    "[VOL] Candles | C1 [{T1:HH:mm}] C:{Close1} Vol:{V1:N0} | C2 [{T2:HH:mm}] C:{Close2} Vol:{V2:N0}",
                    fiveMinCandles[0].Timestamp, fiveMinCandles[0].Close, fiveMinCandles[0].Volume,
                    fiveMinCandles[1].Timestamp, fiveMinCandles[1].Close, fiveMinCandles[1].Volume);

            return fiveMinCandles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VOL] Failed to fetch futures candles for {Key}", key);
            return [];
        }
    }

    public void ResetKey() => _futuresKey = null;

    // ── Discovery — four paths ───────────────────────────────────────────────

    private async Task<string?> DiscoverFuturesKeyAsync(CancellationToken ct)
    {
        var ist = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istZone);
        _logger.LogInformation("[VOL] Discovering Nifty futures key...");

        // Path 1 — LTP probe: try symbolic key formats and validate via candle endpoint
        try
        {
            var key = await DiscoverViaLtpProbeAsync(ist, ct);
            if (key != null)
            {
                _logger.LogInformation("[VOL] Futures key found via LTP probe: {Key}", key);
                return key;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[VOL] LTP probe failed: {Msg}", ex.Message);
        }

        // Path 2 — authenticated Upstox instruments API (may or may not exist in v2)
        try
        {
            var key = await DiscoverViaAuthApiAsync(ist, ct);
            if (key != null)
            {
                _logger.LogInformation("[VOL] Futures key found via authenticated API: {Key}", key);
                return key;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[VOL] Authenticated instruments API failed: {Msg}", ex.Message);
        }

        // Path 3 — CDN files (with browser UA + Referer + auth token)
        foreach (var fileUrl in _instrumentsUrls)
        {
            try
            {
                var key = await SearchInstrumentsFileAsync(fileUrl, ist, ct);
                if (key != null)
                {
                    _logger.LogInformation("[VOL] Futures key found via CDN: {Key}", key);
                    return key;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("[VOL] Instruments file {Url} failed: {Msg}", fileUrl, ex.Message);
            }
        }

        _logger.LogError(
            "[VOL] Could not auto-discover Nifty futures key. " +
            "Add it manually to appsettings.json: " +
            "\"VolumeStrategy\": {{ \"FuturesInstrumentKey\": \"NSE_FO|XXXXX\" }}. " +
            "Find it: open Upstox web/app → F&O → NIFTY FUT → copy the instrument_key from API logs.");
        return null;
    }

    // Path 1: probe LTP endpoint with known symbolic key formats, then validate with candle endpoint
    private async Task<string?> DiscoverViaLtpProbeAsync(DateTime ist, CancellationToken ct)
    {
        var candidates = BuildSymbolicFuturesCandidates(ist);
        _logger.LogInformation("[VOL] LTP probe — trying {Count} candidate keys: {Keys}",
            candidates.Count, string.Join(", ", candidates));

        foreach (var candidate in candidates)
        {
            try
            {
                var url = $"market-quote/ltp?instrument_key={Uri.EscapeDataString(candidate)}";
                var response = await _http.GetAsync<JsonElement>(url, ct);

                if (!response.TryGetProperty("data", out var data)) continue;

                foreach (var entry in data.EnumerateObject())
                {
                    // Response key uses ":" separator; normalize back to "|"
                    var responseKey = entry.Name.Replace(":", "|");
                    _logger.LogInformation("[VOL] LTP probe hit: sent {Input} → got key {ResponseKey}",
                        candidate, responseKey);

                    // Test if candle endpoint accepts the response key (usually numeric)
                    if (await TestCandleEndpointAsync(responseKey, ct))
                        return responseKey;

                    // Also test if the same symbolic key works for candles
                    if (responseKey != candidate && await TestCandleEndpointAsync(candidate, ct))
                        return candidate;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug("[VOL] LTP probe {Key}: {Msg}", candidate, ex.Message);
            }
        }

        return null;
    }

    private async Task<bool> TestCandleEndpointAsync(string key, CancellationToken ct)
    {
        try
        {
            var url = $"historical-candle/intraday/{Uri.EscapeDataString(key)}/1minute";
            var response = await _http.GetAsync<JsonElement>(url, ct);
            var ok = response.TryGetProperty("data", out var d) &&
                     d.TryGetProperty("candles", out _);
            if (ok) _logger.LogInformation("[VOL] Candle endpoint accepted key: {Key}", key);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[VOL] Candle test {Key}: {Msg}", key, ex.Message);
            return false;
        }
    }

    // Build list of NSE_FO symbolic key candidates for NIFTY FUT based on likely expiry dates
    private static List<string> BuildSymbolicFuturesCandidates(DateTime ist)
    {
        var candidates = new List<string>();
        for (int m = 0; m <= 1; m++)
        {
            var d = ist.AddMonths(m);
            var thursday = GetLastThursdayOfMonth(d.Year, d.Month);
            if (thursday >= DateOnly.FromDateTime(ist.AddDays(-1)))
                candidates.Add($"NSE_FO|NIFTY{thursday:ddMMMyy}FUT".ToUpper());
        }
        return candidates;
    }

    private static DateOnly GetLastThursdayOfMonth(int year, int month)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        int daysBack = ((int)lastDay.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
        return lastDay.AddDays(-daysBack);
    }

    // Path 2: call the authenticated Upstox v2 instruments API (may return 404 in v2)
    private async Task<string?> DiscoverViaAuthApiAsync(DateTime ist, CancellationToken ct)
    {
        _logger.LogInformation("[VOL] Trying authenticated instruments API...");

        var response = await _http.GetAsync<JsonElement>(
            "instruments?exchange=NSE_FO&instrument_type=FUT", ct);

        var instruments = response.ValueKind == JsonValueKind.Array
            ? response
            : response.TryGetProperty("data", out var d) ? d : response;

        if (instruments.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("[VOL] Instruments API response was not an array: {Kind}", instruments.ValueKind);
            return null;
        }

        return FindNiftyFutKey(instruments, ist);
    }

    // Path 3: download gzip'd JSON instruments CDN file
    private async Task<string?> SearchInstrumentsFileAsync(string fileUrl, DateTime ist, CancellationToken ct)
    {
        _logger.LogInformation("[VOL] Downloading instruments from {Url}", fileUrl);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        // Browser-like headers to pass bot/Cloudflare detection
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Add("Accept", "application/json, */*;q=0.8");
        http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        http.DefaultRequestHeaders.Add("Referer", "https://developer.upstox.com/");

        // Try adding auth token in case CDN requires authentication
        var token = _tokenManager.GetToken();
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var bytes = await http.GetByteArrayAsync(fileUrl, ct);

        using var compressed   = new MemoryStream(bytes);
        using var decompressed = new MemoryStream();
        await using var gzip   = new GZipStream(compressed, CompressionMode.Decompress);
        await gzip.CopyToAsync(decompressed, ct);
        decompressed.Position = 0;

        using var doc = await JsonDocument.ParseAsync(decompressed, cancellationToken: ct);
        var root = doc.RootElement;

        var instruments = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var d) ? d : root;

        int scanned = 0;
        foreach (var _ in instruments.EnumerateArray()) scanned++;
        _logger.LogInformation("[VOL] Scanned {Count} instruments from CDN", scanned);

        return FindNiftyFutKey(instruments, ist);
    }

    // Shared: scan an instruments JsonElement array and pick nearest-expiry NIFTY FUT key
    private string? FindNiftyFutKey(JsonElement instruments, DateTime ist)
    {
        var candidates = new List<(string Key, DateOnly Expiry)>();

        foreach (var inst in instruments.EnumerateArray())
        {
            var instType = GetString(inst, "instrument_type") ?? GetString(inst, "type") ?? "";
            if (!instType.Equals("FUT", StringComparison.OrdinalIgnoreCase)) continue;

            var name   = GetString(inst, "name") ?? "";
            var symbol = GetString(inst, "tradingsymbol") ?? GetString(inst, "trading_symbol") ?? "";

            var isNifty =
                name.Equals("NIFTY", StringComparison.OrdinalIgnoreCase) ||
                (symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase) &&
                 !symbol.StartsWith("NIFTYNXT",     StringComparison.OrdinalIgnoreCase) &&
                 !symbol.StartsWith("NIFTYBANK",    StringComparison.OrdinalIgnoreCase) &&
                 !symbol.StartsWith("NIFTYMIDCAP",  StringComparison.OrdinalIgnoreCase));

            if (!isNifty) continue;

            var expiryStr = GetString(inst, "expiry") ?? "";
            if (!DateOnly.TryParse(expiryStr, out var expiry)) continue;
            if (expiry.Month != ist.Month || expiry.Year != ist.Year) continue;

            var key = GetString(inst, "instrument_key") ?? GetString(inst, "key") ?? "";
            if (string.IsNullOrEmpty(key)) continue;

            candidates.Add((key, expiry));
            _logger.LogInformation("[VOL] Candidate: {Key} | Symbol: {Symbol} | Expiry: {Expiry}", key, symbol, expiry);
        }

        if (candidates.Count == 0) return null;

        candidates.Sort((a, b) => a.Expiry.CompareTo(b.Expiry));
        return candidates[0].Key;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    // ── 1-min → 5-min aggregation ────────────────────────────────────────────

    private record MinuteBar(DateTime Ts, decimal O, decimal H, decimal L, decimal C, long V);

    private static List<MinuteBar> ParseMinuteCandles(JsonElement candlesEl)
    {
        var result = new List<MinuteBar>();

        foreach (var row in candlesEl.EnumerateArray())
        {
            var arr = row.EnumerateArray().ToArray();
            if (arr.Length < 6) continue;

            var ts = arr[0].GetString() ?? "";
            if (!DateTime.TryParse(ts, out var candleTime)) continue;

            result.Add(new MinuteBar(
                candleTime,
                arr[1].GetDecimal(),
                arr[2].GetDecimal(),
                arr[3].GetDecimal(),
                arr[4].GetDecimal(),
                arr[5].GetInt64()));
        }

        result.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        return result;
    }

    private static List<FuturesCandle> AggregateToFiveMin(List<MinuteBar> minuteCandles)
    {
        if (minuteCandles.Count == 0) return [];

        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata"));

        var marketOpenDt = nowIst.Date + _marketOpen;
        var groups = new Dictionary<DateTime, List<MinuteBar>>();

        foreach (var c in minuteCandles)
        {
            var candleIst = c.Ts.Kind == DateTimeKind.Utc
                ? TimeZoneInfo.ConvertTimeFromUtc(c.Ts, TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata"))
                : c.Ts;

            if (candleIst < marketOpenDt) continue;

            var elapsed     = (candleIst - marketOpenDt).TotalMinutes;
            var windowIdx   = (int)(elapsed / CandleMinutes);
            var windowStart = marketOpenDt.AddMinutes(windowIdx * CandleMinutes);

            if (!groups.ContainsKey(windowStart))
                groups[windowStart] = [];

            groups[windowStart].Add(c);
        }

        var currentWindowStart = marketOpenDt.AddMinutes(
            (int)((nowIst - marketOpenDt).TotalMinutes / CandleMinutes) * CandleMinutes);

        return groups
            .Where(g => g.Key < currentWindowStart && g.Value.Count > 0)
            .OrderByDescending(g => g.Key)
            .Take(2)
            .Select(g =>
            {
                var bars = g.Value.OrderBy(x => x.Ts).ToList();
                return new FuturesCandle(
                    bars.First().O,
                    bars.Max(x => x.H),
                    bars.Min(x => x.L),
                    bars.Last().C,
                    bars.Sum(x => x.V),
                    g.Key);
            })
            .ToList();
    }
}
