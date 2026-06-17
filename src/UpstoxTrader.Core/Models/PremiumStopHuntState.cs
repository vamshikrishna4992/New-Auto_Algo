namespace UpstoxTrader.Core.Models;

public class PremiumStopHuntState
{
    public Position? ActivePosition { get; set; }
    public bool ForceExitRequested { get; set; }
    public List<string> EventLog { get; } = new();

    public void Log(string message)
    {
        var ist = ORBState.GetIST();
        var entry = $"{ist:HH:mm:ss} [PSH] {message}";
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
        lock (EventLog) EventLog.Clear();
        Log("PremiumStopHunt reset — ready for new day");
    }
}
