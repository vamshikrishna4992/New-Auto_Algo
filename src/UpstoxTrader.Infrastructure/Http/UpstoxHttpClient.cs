using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;

namespace UpstoxTrader.Infrastructure.Http;

public class UpstoxHttpClient
{
    private readonly HttpClient _http;
    private readonly TokenManager _tokenManager;
    private readonly ILogger<UpstoxHttpClient> _logger;

    public UpstoxHttpClient(HttpClient http, TokenManager tokenManager,
        IOptions<UpstoxSettings> settings, ILogger<UpstoxHttpClient> logger)
    {
        _http = http;
        _tokenManager = tokenManager;
        _logger = logger;
        _http.BaseAddress = new Uri(settings.Value.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(request);

        var response = await _http.SendAsync(request, ct);
        return await ReadResponse<T>(response, ct);
    }

    public async Task<T> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuth(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        var response = await _http.SendAsync(request, ct);
        return await ReadResponse<T>(response, ct);
    }

    private void AddAuth(HttpRequestMessage request)
    {
        var token = _tokenManager.GetToken();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<T> ReadResponse<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("HTTP {Code} from {Url}: {Body}",
                (int)response.StatusCode, response.RequestMessage?.RequestUri, json);
            throw new HttpRequestException(
                $"Upstox API returned {(int)response.StatusCode}: {json}");
        }

        return JsonSerializer.Deserialize<T>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Null response from API");
    }
}
