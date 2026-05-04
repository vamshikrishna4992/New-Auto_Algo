namespace UpstoxTrader.Core.Models;

public class OpeningCandle
{
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public DateTime CandleStart { get; set; }
    public DateTime CandleEnd { get; set; }
}
