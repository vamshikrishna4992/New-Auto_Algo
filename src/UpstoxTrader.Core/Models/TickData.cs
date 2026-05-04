namespace UpstoxTrader.Core.Models;

public record TickData(
    string InstrumentKey,
    decimal Ltp,
    decimal PreviousLtp,
    long Volume,
    long OI,
    DateTime Timestamp);
