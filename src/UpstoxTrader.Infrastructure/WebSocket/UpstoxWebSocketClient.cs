using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;
using UpstoxTrader.Infrastructure.Protos;

namespace UpstoxTrader.Infrastructure.WebSocket;

public class UpstoxWebSocketClient : IMarketFeedService
{
    private readonly TokenManager _tokenManager;
    private readonly UpstoxSettings _settings;
    private readonly ILogger<UpstoxWebSocketClient> _logger;

    private ClientWebSocket? _ws;
    private readonly Channel<TickData> _tickChannel =
        Channel.CreateBounded<TickData>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly object _subscribeLock = new();
    private readonly HashSet<string> _subscribedKeys = new();
    private long _tickCount;

    private const string AuthorizeUrl =
        "https://api.upstox.com/v3/feed/market-data-feed/authorize";

    public UpstoxWebSocketClient(
        TokenManager tokenManager,
        IOptions<UpstoxSettings> settings,
        ILogger<UpstoxWebSocketClient> logger)
    {
        _tokenManager = tokenManager;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ConnectAsync(string[] instrumentKeys, CancellationToken ct)
    {
        int attempt = 0;
        const int maxRetries = 10;

        while (attempt < maxRetries && !ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                var wsUri = await GetAuthorizedUriAsync(ct);

                _ws?.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(wsUri, ct);
                _logger.LogInformation("WebSocket connected (attempt {Attempt})", attempt);

                lock (_subscribeLock)
                {
                    foreach (var k in instrumentKeys) _subscribedKeys.Add(k);
                }

                await SendSubscriptionAsync(instrumentKeys, ct);
                _logger.LogInformation("Subscribed to {Keys}", string.Join(", ", instrumentKeys));

                _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "WebSocket connect attempt {Attempt}/{Max} failed — retrying in 5s",
                    attempt, maxRetries);
                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        _logger.LogError(
            "WebSocket failed to connect after {Max} attempts — no live market data",
            maxRetries);
    }

    public async Task SubscribeAsync(string[] instrumentKeys, CancellationToken ct)
    {
        lock (_subscribeLock)
        {
            foreach (var k in instrumentKeys) _subscribedKeys.Add(k);
        }

        if (_ws?.State == WebSocketState.Open)
            await SendSubscriptionAsync(instrumentKeys, ct);
        else
            _logger.LogWarning(
                "SubscribeAsync called but WebSocket is not open — keys queued for reconnect: {Keys}",
                string.Join(", ", instrumentKeys));
    }

    public IAsyncEnumerable<TickData> GetTickStreamAsync(CancellationToken ct)
        => _tickChannel.Reader.ReadAllAsync(ct);

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task<Uri> GetAuthorizedUriAsync(CancellationToken ct)
    {
        var token = _tokenManager.GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "No access token available — cannot authorize WebSocket feed");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await http.GetAsync(AuthorizeUrl, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Feed authorize returned {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("authorizedRedirectUri", out var uriEl))
            throw new InvalidOperationException(
                $"Unexpected authorize response — missing authorizedRedirectUri: {json}");

        var uriStr = uriEl.GetString()
            ?? throw new InvalidOperationException("authorizedRedirectUri is null");

        _logger.LogDebug("Authorized WebSocket URI obtained");
        return new Uri(uriStr);
    }

    private async Task SendSubscriptionAsync(string[] keys, CancellationToken ct)
    {
        var payload = new
        {
            guid = Guid.NewGuid().ToString(),
            method = "sub",
            data = new
            {
                mode = "full",
                instrumentKeys = keys
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket close frame received");
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    _logger.LogDebug("Skipping non-binary WebSocket frame (type={Type})",
                        result.MessageType);
                    continue;
                }

                ProcessFrame(ms.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "WebSocket disconnected — scheduling reconnect");
            _ = Task.Run(() => ReconnectAsync(ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket receive loop fatal error");
        }
    }

    private void ProcessFrame(byte[] data)
    {
        FeedResponse feed;
        try
        {
            feed = FeedResponse.Parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _logger.LogWarning(ex,
                "Protobuf parse failed on {Bytes}-byte frame — frame dropped", data.Length);
            return;
        }

        var now = DateTime.UtcNow;

        foreach (var (key, value) in feed.Feeds)
        {
            if (!TryExtractLtp(key, value, out var ltp, out var vol, out var oi))
                continue;

            // Never emit a zero LTP — it means the frame was malformed or the
            // field was missing. Log it so the operator knows the feed is broken.
            if (ltp <= 0)
            {
                _logger.LogWarning(
                    "Received LTP=0 for {Key} — protobuf field may not have matched " +
                    "expected wire type. Check MarketDataFeed.proto field types.",
                    key);
                continue;
            }

            var tick = new TickData(key, ltp, 0, vol, oi, now);
            _tickChannel.Writer.TryWrite(tick);

            var count = Interlocked.Increment(ref _tickCount);
            if (count % 10 == 0)
                _logger.LogDebug("Tick #{Count} | {Key} | LTP: {Ltp}", count, key, ltp);
        }
    }

    private static bool TryExtractLtp(string key, Feed value,
        out decimal ltp, out long vol, out long oi)
    {
        ltp = 0; vol = 0; oi = 0;

        switch (value.FeedUnionCase)
        {
            case Feed.FeedUnionOneofCase.FullFeed:
                var ff = value.FullFeed;
                switch (ff.FullFeedUnionCase)
                {
                    case FullFeed.FullFeedUnionOneofCase.MarketFf:
                        var mff = ff.MarketFf;
                        if (mff.Ltpc is null) return false;
                        ltp = (decimal)mff.Ltpc.Ltp;
                        vol = mff.Vtt;
                        oi  = mff.Oi;
                        return true;

                    case FullFeed.FullFeedUnionOneofCase.IndexFf:
                        var iff = ff.IndexFf;
                        if (iff.Ltpc is null) return false;
                        ltp = (decimal)iff.Ltpc.Ltp;
                        return true;

                    default:
                        return false;
                }

            case Feed.FeedUnionOneofCase.Ltpc:
                if (value.Ltpc is null) return false;
                ltp = (decimal)value.Ltpc.Ltp;
                return true;

            default:
                return false;
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        int attempt = 0;
        const int maxRetries = 10;

        while (attempt < maxRetries && !ct.IsCancellationRequested)
        {
            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            _logger.LogInformation("Reconnect attempt {Attempt}/{Max}...", attempt, maxRetries);

            try
            {
                string[] keys;
                lock (_subscribeLock) keys = _subscribedKeys.ToArray();

                var wsUri = await GetAuthorizedUriAsync(ct);
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(wsUri, ct);
                await SendSubscriptionAsync(keys, ct);

                _logger.LogInformation(
                    "WebSocket reconnected and re-subscribed ({Count} keys)", keys.Length);
                _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Reconnect attempt {Attempt} failed", attempt);
            }
        }

        _logger.LogError(
            "WebSocket could not reconnect after {Max} attempts — live feed is down",
            maxRetries);
    }
}
