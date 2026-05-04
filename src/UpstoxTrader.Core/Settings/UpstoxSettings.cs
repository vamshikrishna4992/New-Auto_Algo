namespace UpstoxTrader.Core.Settings;

public class UpstoxSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "http://localhost:5000/auth/callback";
    public string BaseUrl { get; set; } = "https://api.upstox.com/v2";
    public string WebSocketUrl { get; set; } = "wss://api.upstox.com/v2/feed/market-data-feed";
}
