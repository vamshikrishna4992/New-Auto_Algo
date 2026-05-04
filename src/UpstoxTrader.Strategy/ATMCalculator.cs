namespace UpstoxTrader.Strategy;

public static class ATMCalculator
{
    public static int Calculate(decimal ltp, int strikeInterval)
        => (int)Math.Round(ltp / strikeInterval) * strikeInterval;
}
