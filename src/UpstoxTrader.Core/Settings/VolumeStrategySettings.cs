namespace UpstoxTrader.Core.Settings;

public class VolumeStrategySettings
{
    public decimal VolumeMultiplier { get; set; } = 2.0m;
    public decimal TakeProfitPoints { get; set; } = 10m;
    public decimal StopLossPoints { get; set; } = 5m;
    public string HardExitTime { get; set; } = "15:20";
    public bool PaperTrade { get; set; } = false;
    public int LotSize { get; set; } = 65;

    // Optional manual override — set this if auto-discovery fails
    // e.g. "NSE_FO|47547"  — update monthly when contract rolls over
    public string FuturesInstrumentKey { get; set; } = "";
}
