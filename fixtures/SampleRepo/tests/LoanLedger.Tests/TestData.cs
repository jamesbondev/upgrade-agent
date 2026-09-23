using Fixture.Lib;

namespace LoanLedger.Tests;

internal static class TestData
{
    public static readonly FixtureConfig Config = new("EUR", DayCountBasis: 365, RoundingDecimals: 2);

    public static AccountDirectory Directory() => new(
    [
        new AccountInfo("ACC-1", "Ada Lovelace", 10_000m, 3.65m),
        new AccountInfo("ACC-2", "Grace Hopper", 250_000m, 5m),
        new AccountInfo("ACC-3", "Alan Turing", 0m, 4m),
    ]);

    public static InterestCalculator Calculator() => new(new ConfigClient(Config));

    public static StatementService Statements() => new(Directory(), Calculator());
}
