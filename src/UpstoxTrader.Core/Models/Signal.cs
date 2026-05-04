using UpstoxTrader.Core.Enums;

namespace UpstoxTrader.Core.Models;

public class Signal
{
    public OptionType Direction { get; set; }
    public decimal NiftyLtp { get; set; }
    public int AtmStrike { get; set; }
    public string OptionSymbol { get; set; } = "";
    public DateTime DetectedAt { get; set; }
}
