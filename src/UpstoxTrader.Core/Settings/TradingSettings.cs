namespace UpstoxTrader.Core.Settings;

public class TradingSettings
{
    public int CandleMinutes { get; set; } = 15;
    public int LotSize { get; set; } = 75;
   public string ExitMode { get; set; } = "Percent";

public decimal TakeProfitPct { get; set; } = 10.0m;
public decimal StopLossPct { get; set; } = 5.0m;

public decimal TakeProfitPoints { get; set; } = 10;
public decimal StopLossPoints { get; set; } = 5;
    public string HardExitTime { get; set; } = "15:20";
    public bool PaperTrade { get; set; } = true;

    // "FirstOnly"  — breakout checked only against the 9:15 opening candle (classic ORB)
    // "AllCandles" — after each candle closes its high/low becomes the new breakout reference
    public string CandleMode { get; set; } = "FirstOnly";

    // Only used when CandleMode = "AllCandles"
    // No new signal is generated from candles that close at or after this time (IST, HH:mm)
    public string SignalCutoffTime { get; set; } = "15:00";


}
