namespace LoanLedger.Tests;

public class InterestCalculatorTests
{
    [Fact]
    public void DailyInterest_UsesDayCountBasis()
    {
        // 10,000 * 3.65% / 365 = 1.00
        Assert.Equal(1.00m, TestData.Calculator().DailyInterest(10_000m, 3.65m));
    }

    [Fact]
    public void DailyInterest_IsZeroForZeroPrincipal()
    {
        Assert.Equal(0m, TestData.Calculator().DailyInterest(0m, 5m));
    }

    [Fact]
    public void DailyInterest_RoundsToConfiguredDecimals()
    {
        // 1,000 * 5% / 365 = 0.136986... -> 0.14
        Assert.Equal(0.14m, TestData.Calculator().DailyInterest(1_000m, 5m));
    }

    [Theory]
    [InlineData(1, 1.00)]
    [InlineData(30, 30.00)]
    [InlineData(365, 365.00)]
    public void AccruedInterest_ScalesWithDays(int days, double expected)
    {
        Assert.Equal((decimal)expected, TestData.Calculator().AccruedInterest(10_000m, 3.65m, days));
    }

    [Fact]
    public void AccruedInterest_RejectsNegativeDays()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestData.Calculator().AccruedInterest(10_000m, 3.65m, -1));
    }
}
