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

    // Cache: keyed by expiry string, valid for 60 s
    private (string Expiry, JsonElement[] Strikes, DateTime CachedAt)? _cache;
    private readonly object _cacheLock = new();

    public UpstoxOptionChainService(
        UpstoxHttpClient http,
        IOptions<NiftySettings> nifty,
        ILogger<UpstoxOptionChainService> logger)
    {
        _http = http;
        _nifty = nifty.Value;
        _logger = logger;
    }

    public async Task<string> GetAtmOptionSymbolAsync(
        int strike, OptionType type, CancellationToken ct)
    {
        var expiry = GetNearestThursdayExpiry();
        _logger.LogInformation(
            "Fetching option chain | expiry {Expiry} | strike {Strike} | {Type}",
            expiry, strike, type);

        var strikes = await GetStrikesAsync(expiry, ct);
        var optionField = type == OptionType.CE ? "call_options" : "put_options";

        // Upstox option chain response per element:
        // { "strike_price": 24350, "call_options": { "instrument_key": "..." }, "put_options": {...} }
        foreach (var el in strikes)
        {
            if (!el.TryGetProperty("strike_price", out var spEl)) continue;

            var strikeVal = (int)spEl.GetDecimal();
            if (strikeVal != strike) continue;

            if (el.TryGetProperty(optionField, out var optEl) &&
                optEl.TryGetProperty("instrument_key", out var ikEl))
            {
                var key = ikEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(key))
                {
                    _logger.LogInformation("Option instrument key: {Key}", key);
                    return key;
                }
            }

            _logger.LogWarning(
                "Found strike {Strike} in chain but '{Field}' or 'instrument_key' is missing",
                strike, optionField);
            break;
        }

        // Fallback: construct key using Upstox naming convention so the order
        // can still be attempted. Log a warning so the operator knows.
        var expDate = DateOnly.ParseExact(expiry, "yyyy-MM-dd");
        var fallback = BuildInstrumentKey(strike, type, expDate);
        _logger.LogWarning(
            "Strike {Strike} {Type} not found in option chain for {Expiry} " +
            "— using constructed key {Key} (verify this is valid before going live)",
            strike, type, expiry, fallback);
        return fallback;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<JsonElement[]> GetStrikesAsync(string expiry, CancellationToken ct)
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

        var url = "/option/chain" +
                  $"?instrument_key={Uri.EscapeDataString(_nifty.InstrumentKey)}" +
                  $"&expiry_date={expiry}";

        _logger.LogInformation("Calling Upstox option chain API: {Url}", url);

        var response = await _http.GetAsync<JsonElement>(url, ct);

        // Upstox wraps array in { "status": "success", "data": [...] }
        JsonElement dataEl;
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("data", out var d))
        {
            dataEl = d;
        }
        else if (response.ValueKind == JsonValueKind.Array)
        {
            dataEl = response;
        }
        else
        {
            _logger.LogError(
                "Unexpected option chain response structure: {Json}",
                response.GetRawText()[..Math.Min(500, response.GetRawText().Length)]);
            return [];
        }

        if (dataEl.ValueKind != JsonValueKind.Array)
        {
            _logger.LogError("Option chain 'data' is not an array: {Kind}", dataEl.ValueKind);
            return [];
        }

        // Clone elements so they survive after the JsonDocument goes out of scope
        var strikes = dataEl.EnumerateArray()
            .Select(el => el.Clone())
            .ToArray();

        _logger.LogInformation("Option chain loaded: {Count} strikes for {Expiry}",
            strikes.Length, expiry);

        lock (_cacheLock)
        {
            _cache = (expiry, strikes, DateTime.UtcNow);
        }

        return strikes;
    }

    private static string GetNearestThursdayExpiry()
    {
        var ist = ORBState.GetIST();
        var date = DateOnly.FromDateTime(ist);

        int daysUntilThursday = ((int)DayOfWeek.Thursday - (int)date.DayOfWeek + 7) % 7;

        if (daysUntilThursday == 0)
        {
            // Today is Thursday — use today if before 15:30, else next week
            if (ist.TimeOfDay < new TimeSpan(15, 30, 0))
                return date.ToString("yyyy-MM-dd");

            daysUntilThursday = 7;
        }

        return date.AddDays(daysUntilThursday).ToString("yyyy-MM-dd");
    }

    // Upstox naming: NSE_FO|NIFTY{DDMMMYY}{STRIKE}{CE/PE}  e.g. NSE_FO|NIFTY30NOV2424150CE
    private static string BuildInstrumentKey(int strike, OptionType type, DateOnly expiry)
    {
        var dateStr = expiry.ToString("ddMMMyy").ToUpper();
        var optStr = type == OptionType.CE ? "CE" : "PE";
        return $"NSE_FO|NIFTY{dateStr}{strike}{optStr}";
    }
}
