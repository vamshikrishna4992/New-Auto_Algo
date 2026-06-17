namespace UpstoxTrader.Core.Settings;

public class PremiumStopHuntSettings
{
    public decimal PremiumRisePoints { get; set; } = 5m;
    public decimal NiftyFlatPoints { get; set; } = 15m;
    public decimal StopHuntWickPoints { get; set; } = 25m;
    public int PremiumLookbackMinutes { get; set; } = 3;
    public int SignalWindowMinutes { get; set; } = 5;
    public decimal TakeProfitPoints { get; set; } = 15m;
    public decimal StopLossPoints { get; set; } = 8m;
    public string HardExitTime { get; set; } = "15:20";
    public int LotSize { get; set; } = 65;
}
