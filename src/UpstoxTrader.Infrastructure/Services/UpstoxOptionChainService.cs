using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Enums;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Http;

namespace UpstoxTrader.Infrastructure.Services;

public class UpstoxOptionChainService : IOptionChainService
{
    private readonly UpstoxHttpClient _http;
    private readonly NiftySettings _nifty;
    private readonly ILogger<UpstoxOptionChainService> _logger;


    // Strike cache: valid for 60 s
    private (string Expiry, JsonElement[] Strikes, DateTime CachedAt)? _cache;
    private readonly object _cacheLock = new();

    // Session-level discovered (underlying key, expiry) — found once, reused all day
    private (string UnderlyingKey, string Expiry)? _session;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly TradingSettings _trading;

    public UpstoxOptionChainService(
    UpstoxHttpClient http,
    IOptions<NiftySettings> nifty,
    IOptions<TradingSettings> trading,
    ILogger<UpstoxOptionChainService> logger)
{
    _http = http;
    _nifty = nifty.Value;
    _trading = trading.Value;   // ✅ ADD THIS
    _logger = logger;
}

    public async Task<OptionInstrument> GetAtmOptionSymbolAsync(
    int strike, OptionType type, CancellationToken ct)
{
    var (underlyingKey, expiry) = await GetSessionAsync(ct);

    _logger.LogInformation(
        "Fetching option chain | expiry {Expiry} | strike {Strike} | {Type}",
        expiry, strike, type);

    var strikesList = await GetStrikesAsync(underlyingKey, expiry, ct);
    var optionField = type == OptionType.CE ? "call_options" : "put_options";

    string? bestKey = null;
    int bestStrikeVal = 0;
    int bestDiff = int.MaxValue;

    foreach (var el in strikesList)
    {
        if (!el.TryGetProperty("strike_price", out var spEl)) continue;

        var sv = (int)spEl.GetDecimal();
        var diff = Math.Abs(sv - strike);
        if (diff >= bestDiff) continue;

        if (el.TryGetProperty(optionField, out var optEl) &&
            optEl.TryGetProperty("instrument_key", out var ikEl))
        {
            var key = ikEl.GetString() ?? "";
            if (!string.IsNullOrEmpty(key))
            {
                bestDiff = diff;
                bestKey = key;
                bestStrikeVal = sv;
            }
        }
    }

    if (!string.IsNullOrEmpty(bestKey))
    {
        int lotSize = ResolveLotSize(bestKey);

        if (bestStrikeVal == strike)
            _logger.LogInformation(
                "Option key: {Key} (strike {Strike} {Type} exp {Expiry})",
                bestKey, strike, type, expiry);
        else
            _logger.LogWarning(
                "Exact strike {Strike} not in chain — using nearest {Best} (diff {Diff}) | key {Key}",
                strike, bestStrikeVal, bestDiff, bestKey);

        return new OptionInstrument
        {
            InstrumentKey = bestKey,
            LotSize = lotSize,
            Strike = bestStrikeVal,
            Expiry = DateTime.Parse(expiry)
        };
    }

    // fallback
    var expDate = DateOnly.ParseExact(expiry, "yyyy-MM-dd");
    var fallback = BuildInstrumentKey(strike, type, expDate);

    _logger.LogWarning(
        "No strikes found in chain for {Expiry} — fallback key {Key} (may be invalid)",
        expiry, fallback);

    int fallbackLotSize = ResolveLotSize(fallback);

    return new OptionInstrument
    {
        InstrumentKey = fallback,
        LotSize = fallbackLotSize,
        Strike = strike,
        Expiry = DateTime.Parse(expiry)
    };
}
    // ── Session discovery ────────────────────────────────────────────────────

    private async Task<(string UnderlyingKey, string Expiry)> GetSessionAsync(CancellationToken ct)
    {
        if (_session.HasValue) return _session.Value;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_session.HasValue) return _session.Value;

            _session = await DiscoverAsync(ct);
            _logger.LogInformation(
                "Option chain session: underlying={Key} expiry={Expiry}",
                _session.Value.UnderlyingKey, _session.Value.Expiry);
            return _session.Value;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // Probe multiple underlying-key variants and expiry candidates until we get data.
    private async Task<(string UnderlyingKey, string Expiry)> DiscoverAsync(CancellationToken ct)
    {
        var ist = ORBState.GetIST();
        var today = DateOnly.FromDateTime(ist);

        // Upstox sometimes uses a different key spelling for the index in option chain queries.
        // The LTP feed uses "NSE_INDEX|Nifty 50"; option chain may need one of the variants below.
        var underlyingCandidates = new[]
        {
            _nifty.InstrumentKey,       // e.g. NSE_INDEX|Nifty 50  (from config)
            "NSE_INDEX|Nifty 50",
            "NSE_INDEX|NIFTY 50",
            "NSE_INDEX|NIFTY",
            "NSE_INDEX|Nifty50",
        };

        // Build expiry candidates: last Thu/Mon/Wed of current and next two months,
        // plus every Thu and Mon in the next six weeks (covers weekly contracts too).
        var expirySet = new SortedSet<DateOnly>();
        for (int m = 0; m <= 2; m++)
        {
            var d = today.AddMonths(m);
            expirySet.Add(LastDayOfWeekOfMonth(d.Year, d.Month, DayOfWeek.Thursday));
            expirySet.Add(LastDayOfWeekOfMonth(d.Year, d.Month, DayOfWeek.Monday));
            expirySet.Add(LastDayOfWeekOfMonth(d.Year, d.Month, DayOfWeek.Wednesday));
        }
        for (int week = 0; week <= 6; week++)
        {
            foreach (var dow in new[] { DayOfWeek.Thursday, DayOfWeek.Monday, DayOfWeek.Wednesday })
            {
                int days = ((int)dow - (int)today.DayOfWeek + 7) % 7;
                if (days == 0) days = 7;
                expirySet.Add(today.AddDays(days + week * 7));
            }
        }

        var expiryCandidates = expirySet.Where(d => d >= today).ToArray();

        _logger.LogInformation(
            "Probing option chain: {KeyCount} key variants × {DateCount} expiry dates",
            underlyingCandidates.Length, expiryCandidates.Length);

        foreach (var underlying in underlyingCandidates)
        {
            foreach (var expDate in expiryCandidates)
            {
                var expiry = expDate.ToString("yyyy-MM-dd");
                var url = "option/chain" +
                          $"?instrument_key={Uri.EscapeDataString(underlying)}" +
                          $"&expiry_date={expiry}";
                try
                {
                    var response = await _http.GetAsync<JsonElement>(url, ct);
                    if (HasData(response))
                    {
                        _logger.LogInformation(
                            "Option chain probe succeeded: key={Key} expiry={Expiry}",
                            underlying, expiry);
                        return (underlying, expiry);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        "Option chain probe key={Key} expiry={Expiry}: {Msg}",
                        underlying, expiry, ex.Message);
                }
            }
        }

        // Nothing worked — return best calculated guess and let the caller handle fallback
        var bestExpiry = LastDayOfWeekOfMonth(today.Year, today.Month, DayOfWeek.Thursday);
        if (bestExpiry < today) bestExpiry = LastDayOfWeekOfMonth(today.AddMonths(1).Year, today.AddMonths(1).Month, DayOfWeek.Thursday);
        _logger.LogWarning(
            "Option chain discovery failed for all candidates — falling back to {Key}/{Expiry}",
            _nifty.InstrumentKey, bestExpiry.ToString("yyyy-MM-dd"));
        return (_nifty.InstrumentKey, bestExpiry.ToString("yyyy-MM-dd"));
    }

    private static bool HasData(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("data", out var d))
            return d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0;

        return response.ValueKind == JsonValueKind.Array && response.GetArrayLength() > 0;
    }

    // ── Strike fetching ──────────────────────────────────────────────────────

    private async Task<JsonElement[]> GetStrikesAsync(
        string underlyingKey, string expiry, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cache.HasValue &&
                _cache.Value.Expiry == expiry &&
                DateTime.UtcNow - _cache.Value.CachedAt < TimeSpan.FromSeconds(60))
            {
                _logger.LogDebug("Option chain cache hit for {Expiry}", expiry);
                return _cache.Value.Strikes;
            }
        }

        var url = "option/chain" +
                  $"?instrument_key={Uri.EscapeDataString(underlyingKey)}" +
                  $"&expiry_date={expiry}";

        _logger.LogInformation("Calling Upstox option chain API: {Url}", url);

        var response = await _http.GetAsync<JsonElement>(url, ct);

        var raw = response.GetRawText();
        _logger.LogInformation("Option chain raw response (first 500): {Raw}",
            raw[..Math.Min(500, raw.Length)]);

        JsonElement dataEl;
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("data", out var d))
            dataEl = d;
        else if (response.ValueKind == JsonValueKind.Array)
            dataEl = response;
        else
        {
            _logger.LogError("Unexpected option chain response: {Json}", raw[..Math.Min(500, raw.Length)]);
            return [];
        }

        if (dataEl.ValueKind != JsonValueKind.Array)
        {
            _logger.LogError("Option chain 'data' is not an array: {Kind}", dataEl.ValueKind);
            return [];
        }

        var strikes = dataEl.EnumerateArray().Select(el => el.Clone()).ToArray();

        _logger.LogInformation("Option chain loaded: {Count} strikes for {Expiry}",
            strikes.Length, expiry);

        lock (_cacheLock)
            _cache = (expiry, strikes, DateTime.UtcNow);

        return strikes;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DateOnly LastDayOfWeekOfMonth(int year, int month, DayOfWeek dow)
    {
        var lastDay = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        int daysBack = ((int)lastDay.DayOfWeek - (int)dow + 7) % 7;
        return lastDay.AddDays(-daysBack);
    }

    // Upstox naming: NSE_FO|NIFTY{DDMMMYY}{STRIKE}{CE/PE}  e.g. NSE_FO|NIFTY28MAY2623900PE
    private static string BuildInstrumentKey(int strike, OptionType type, DateOnly expiry)
    {
        var dateStr = expiry.ToString("ddMMMyy").ToUpper();
        var optStr = type == OptionType.CE ? "CE" : "PE";
        return $"NSE_FO|NIFTY{dateStr}{strike}{optStr}";
    }


   private int ResolveLotSize(string instrumentKey)
{
    return _trading.LotSize; // ✅ always from config
}
}
