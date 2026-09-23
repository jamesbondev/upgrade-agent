namespace LoanLedger.Tests;

public class StatementServiceTests
{
    [Fact]
    public void BuildStatement_FormatsBalanceAndInterest()
    {
        var statement = TestData.Statements().BuildStatement("ACC-2", 30);

        Assert.Equal("ACC-2", statement.AccountId);
        Assert.Equal("Grace Hopper", statement.Holder);
        Assert.Equal("250,000.00", statement.Balance);
        // 250,000 * 5% / 365 * 30 = 1,027.397... -> 1,027.40
        Assert.Equal("1,027.40", statement.AccruedInterest);
        Assert.Equal(30, statement.Days);
    }

    [Theory]
    [InlineData("ACC-1", "Ada Lovelace")]
    [InlineData("ACC-2", "Grace Hopper")]
    [InlineData("ACC-3", "Alan Turing")]
    public void BuildStatement_ResolvesHolder(string accountId, string expectedHolder)
    {
        Assert.Equal(expectedHolder, TestData.Statements().BuildStatement(accountId, 1).Holder);
    }

    [Fact]
    public void BuildStatement_ThrowsForUnknownAccount()
    {
        Assert.Throws<KeyNotFoundException>(() => TestData.Statements().BuildStatement("ACC-404", 1));
    }

    [Fact]
    public void ToJson_ContainsAllFields()
    {
        var service = TestData.Statements();
        var json = service.ToJson(service.BuildStatement("ACC-1", 1));

        Assert.Equal(
            """{"AccountId":"ACC-1","Holder":"Ada Lovelace","Balance":"10,000.00","AccruedInterest":"1.00","Days":1}""",
            json);
    }
}
