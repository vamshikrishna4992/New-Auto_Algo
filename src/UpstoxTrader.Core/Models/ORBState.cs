namespace UpstoxTrader.Core.Models;

public class ORBState
{
    public decimal LastNiftyLtp { get; set; }
    public decimal PreviousNiftyLtp { get; set; }

    public OpeningCandle? Candle { get; set; }
    public OpeningCandle? PreviousCandle { get; set; }
    public bool CandleReady { get; set; }

    // Live candle-build tracking (visible to dashboard before candle locks)
    public decimal CandleHighSoFar { get; set; }
    public decimal CandleLowSoFar { get; set; }
    public DateTime? CandleEndTime { get; set; }

    public Signal? ActiveSignal { get; set; }
    public Position? ActivePosition { get; set; }
    public bool TradeTakenToday { get; set; }

    public bool ForceExitRequested { get; set; }
    public bool BotRunning { get; set; } = true;

    public List<string> EventLog { get; } = new();

    public void Log(string message)
    {
        var ist = GetIST();
        var entry = $"{ist:HH:mm:ss} — {message}";
        lock (EventLog)
        {
            EventLog.Insert(0, entry);
            if (EventLog.Count > 100) EventLog.RemoveAt(100);
        }
    }

    public void RequestForceExit() => ForceExitRequested = true;

    public void Reset()
    {
        Candle = null;
        CandleReady = false;
        CandleHighSoFar = 0;
        CandleLowSoFar = 0;
        CandleEndTime = null;
        ActiveSignal = null;
        ActivePosition = null;
        TradeTakenToday = false;
        ForceExitRequested = false;
        LastNiftyLtp = 0;
        PreviousNiftyLtp = 0;
        lock (EventLog) EventLog.Clear();
        Log("State reset — ready for new day");
    }

    public static DateTime GetIST()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }
}
