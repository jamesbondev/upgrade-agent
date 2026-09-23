using Fixture.Lib;
using Newtonsoft.Json;

namespace LoanLedger;

public sealed record Statement(string AccountId, string Holder, string Balance, string AccruedInterest, int Days);

/// <summary>Builds interest statements for accounts.</summary>
public sealed class StatementService(AccountDirectory directory, InterestCalculator calculator)
{
    public Statement BuildStatement(string accountId, int days)
    {
        var account = directory.GetAccount(accountId);
        var interest = calculator.AccruedInterest(account.Balance, account.AnnualRatePercent, days);

        return new Statement(
            account.Id,
            account.Holder,
            ValueFormatter.Format(account.Balance),
            ValueFormatter.Format(interest),
            days);
    }

    public string ToJson(Statement statement) => JsonConvert.SerializeObject(statement, Formatting.None);
}
