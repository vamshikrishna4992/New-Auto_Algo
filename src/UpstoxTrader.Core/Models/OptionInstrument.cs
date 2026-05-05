public class OptionInstrument
{
    public string InstrumentKey { get; set; } = "";
    public int LotSize { get; set; }
    public decimal Strike { get; set; }
    public DateTime Expiry { get; set; }
}