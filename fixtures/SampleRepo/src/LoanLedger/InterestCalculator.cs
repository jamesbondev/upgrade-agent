using Fixture.Lib;

namespace LoanLedger;

/// <summary>Simple-interest accrual using the platform's day-count and rounding settings.</summary>
public sealed class InterestCalculator(ConfigClient configClient)
{
    public decimal DailyInterest(decimal principal, decimal annualRatePercent)
    {
        var config = configClient.GetConfig();
        return Math.Round(DailyRate(principal, annualRatePercent, config), config.RoundingDecimals, MidpointRounding.ToEven);
    }

    public decimal AccruedInterest(decimal principal, decimal annualRatePercent, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);

        var config = configClient.GetConfig();
        return Math.Round(DailyRate(principal, annualRatePercent, config) * days, config.RoundingDecimals, MidpointRounding.ToEven);
    }

    private static decimal DailyRate(decimal principal, decimal annualRatePercent, FixtureConfig config) =>
        principal * annualRatePercent / 100m / config.DayCountBasis;
}
