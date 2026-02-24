namespace AIOMarketMaker.Core.Services;

public class PricingOptions
{
    public double IqrMultiplier { get; set; } = 1.5;
    public double ConfidenceWeightPower { get; set; } = 2.0;
    public double RecencyHalfLifeDays { get; set; } = 30.0;
    public int ConfidenceSampleTarget { get; set; } = 20;
    public double SampleSizeWeight { get; set; } = 0.3;
    public double ClassifierConfidenceWeight { get; set; } = 0.4;
    public double ConsistencyWeight { get; set; } = 0.3;
    public double FeePercent { get; set; } = 13.25;
    public int MinComps { get; set; } = 3;
}
