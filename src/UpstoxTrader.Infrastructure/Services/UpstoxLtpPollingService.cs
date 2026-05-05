using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Infrastructure.Http;

namespace UpstoxTrader.Infrastructure.Services;

public class UpstoxLtpPollingService : IMarketFeedService
{
    private readonly UpstoxHttpClient _http;
    private readonly ILogger<UpstoxLtpPollingService> _logger;

    private readonly Channel<TickData> _tickChannel =
        Channel.CreateBounded<TickData>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly HashSet<string> _keys = new();
    private readonly object _keysLock = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public UpstoxLtpPollingService(UpstoxHttpClient http, ILogger<UpstoxLtpPollingService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task ConnectAsync(string[] instrumentKeys, CancellationToken ct)
    {
        lock (_keysLock)
            foreach (var k in instrumentKeys) _keys.Add(k);

        _logger.LogInformation("LTP polling started for: {Keys}", string.Join(", ", instrumentKeys));
        _ = Task.Run(() => PollLoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string[] instrumentKeys, CancellationToken ct)
    {
        lock (_keysLock)
            foreach (var k in instrumentKeys) _keys.Add(k);

        _logger.LogInformation("Added to LTP poll: {Keys}", string.Join(", ", instrumentKeys));
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<TickData> GetTickStreamAsync(CancellationToken ct)
        => _tickChannel.Reader.ReadAllAsync(ct);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string[] keys;
                lock (_keysLock) keys = _keys.ToArray();

                if (keys.Length > 0)
                {
                    var query = string.Join("&", keys.Select(k =>
                        $"instrument_key={Uri.EscapeDataString(k)}"));

                    var response = await _http.GetAsync<JsonElement>(
                        $"market-quote/ltp?{query}", ct);

                    if (response.TryGetProperty("data", out var data))
                    {
                        var now = DateTime.UtcNow;
                        foreach (var entry in data.EnumerateObject())
                        {
                            if (!entry.Value.TryGetProperty("last_price", out var ltpEl)) continue;
                            var ltp = ltpEl.GetDecimal();
                            if (ltp <= 0) continue;

                            // Response key uses ":" but our instrument keys use "|"
                            var instrumentKey = entry.Name.Replace(":", "|");

                            _tickChannel.Writer.TryWrite(new TickData(instrumentKey, ltp, 0, 0, 0, now));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LTP poll error — retrying in 1s");
            }

            await Task.Delay(PollInterval, ct);
        }
    }
}
