using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Settings;

namespace UpstoxTrader.Infrastructure.Auth;

public class TokenService : IHostedService
{
    private readonly TokenManager _tokenManager;
    private readonly UpstoxSettings _settings;
    private readonly ILogger<TokenService> _logger;

    public TokenService(TokenManager tokenManager, IOptions<UpstoxSettings> settings,
        ILogger<TokenService> logger)
    {
        _tokenManager = tokenManager;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_tokenManager.HasToken)
        {
            _logger.LogInformation("Token already present — skipping OAuth flow");
            return;
        }

        if (string.IsNullOrEmpty(_settings.ClientId) || _settings.ClientId == "PASTE_CLIENT_ID_HERE")
        {
            _logger.LogWarning("ClientId not configured — skipping OAuth. Set Upstox:ClientId in appsettings.json");
            return;
        }

        var authUrl = $"https://api.upstox.com/v2/login/authorization/dialog" +
                      $"?client_id={Uri.EscapeDataString(_settings.ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}" +
                      $"&response_type=code" +
                      $"&scope=orders,portfolio,feed,user";

        _logger.LogInformation("Opening browser for Upstox OAuth...");
        _logger.LogInformation("Auth URL: {Url}", authUrl);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open browser automatically — please navigate to the URL above");
        }

        var code = await WaitForCallbackAsync(cancellationToken);
        if (code is null) return;

        await ExchangeCodeForTokenAsync(code, cancellationToken);
    }

    private async Task<string?> WaitForCallbackAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5000/callback/");

        try
        {
            listener.Start();
            _logger.LogInformation("Waiting for OAuth callback on http://localhost:5000/callback/ ...");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            var contextTask = listener.GetContextAsync();
            await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (timeoutCts.IsCancellationRequested && !contextTask.IsCompleted)
            {
                _logger.LogError("OAuth callback timed out after 3 minutes");
                return null;
            }

            var context = await contextTask;
            var code = context.Request.QueryString["code"];

            var responseHtml = "<html><body><h2>Authentication successful! You can close this tab.</h2></body></html>";
            var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, ct);
            context.Response.Close();

            return code;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for OAuth callback");
            return null;
        }
        finally
        {
            if (listener.IsListening) listener.Stop();
        }
    }

    private async Task ExchangeCodeForTokenAsync(string code, CancellationToken ct)
    {
        using var http = new HttpClient();

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("client_id", _settings.ClientId),
            new KeyValuePair<string, string>("client_secret", _settings.ClientSecret),
            new KeyValuePair<string, string>("redirect_uri", _settings.RedirectUri),
            new KeyValuePair<string, string>("grant_type", "authorization_code")
        });

        try
        {
            var response = await http.PostAsync(
                "https://api.upstox.com/v2/login/authorization/token", body, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
            {
                _logger.LogError("Token exchange failed: {Response}", json);
                return;
            }

            var token = tokenEl.GetString() ?? "";
            _tokenManager.SetToken(token);
            _logger.LogInformation("Access token obtained — ready to trade");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to exchange authorization code for token");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
