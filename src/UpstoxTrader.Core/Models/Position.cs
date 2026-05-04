using UpstoxTrader.Core.Enums;

namespace UpstoxTrader.Core.Models;

public class Position
{
    public string OrderId { get; set; } = "";
    public string OptionSymbol { get; set; } = "";
    public string InstrumentKey { get; set; } = "";
    public decimal EntryPrice { get; set; }
    public decimal CurrentLtp { get; set; }
    public int Quantity { get; set; }
    public DateTime EntryTime { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.Open;
    public decimal ExitPrice { get; set; }
    public DateTime? ExitTime { get; set; }
    public string ExitReason { get; set; } = "";

    public decimal ProfitPct =>
        EntryPrice == 0 ? 0 : (CurrentLtp - EntryPrice) / EntryPrice * 100;

    public decimal LossPct =>
        EntryPrice == 0 ? 0 : (EntryPrice - CurrentLtp) / EntryPrice * 100;

    public decimal UnrealizedPnL =>
        (CurrentLtp - EntryPrice) * Quantity;
}
