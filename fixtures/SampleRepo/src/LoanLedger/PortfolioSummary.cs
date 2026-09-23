using Fixture.Lib;

namespace LoanLedger;

/// <summary>One-line summaries across several accounts.</summary>
public sealed class PortfolioSummary(AccountDirectory directory)
{
    public decimal TotalBalance(IEnumerable<string> accountIds) =>
        accountIds.Sum(id => directory.GetAccount(id).Balance);

    public string Describe(IEnumerable<string> accountIds)
    {
        var ids = accountIds.ToList();
        return $"{ids.Count} accounts, total {ValueFormatter.Format(TotalBalance(ids))}";
    }
}
