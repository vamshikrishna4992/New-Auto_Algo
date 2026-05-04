namespace UpstoxTrader.Infrastructure.Auth;

public class TokenManager
{
    private string _token = "";
    private readonly object _lock = new();

    public string GetToken()
    {
        lock (_lock) return _token;
    }

    public void SetToken(string token)
    {
        lock (_lock) _token = token;
    }

    public bool HasToken
    {
        get { lock (_lock) return !string.IsNullOrEmpty(_token); }
    }
}
