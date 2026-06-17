namespace UpstoxTrader.Core.Models;

public class VolumeBreakoutState
{
    public Position? ActivePosition { get; set; }
    public bool ForceExitRequested { get; set; }
    public DateTime LastProcessedCandleTime { get; set; }
    public string? FuturesInstrumentKey { get; set; }

    public List<string> EventLog { get; } = new();

    public void Log(string message)
    {
        var ist = ORBState.GetIST();
        var entry = $"{ist:HH:mm:ss} [VOL] {message}";
        lock (EventLog)
        {
            EventLog.Insert(0, entry);
            if (EventLog.Count > 100) EventLog.RemoveAt(100);
        }
    }

    public void Reset()
    {
        ActivePosition = null;
        ForceExitRequested = false;
        LastProcessedCandleTime = default;
        lock (EventLog) EventLog.Clear();
        Log("VolumeStrategy reset — ready for new day");
    }
}
