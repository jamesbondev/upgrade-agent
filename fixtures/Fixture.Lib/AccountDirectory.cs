namespace Fixture.Lib;

/// <summary>An account known to the directory.</summary>
public sealed record AccountInfo(string Id, string Holder, decimal Balance, decimal AnnualRatePercent);

#if !FIXTURE_API_1_0
/// <summary>Strongly typed account identifier.</summary>
public sealed record AccountId
{
    /// <summary>Creates an identifier; the value must not be empty.</summary>
    public AccountId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>The raw identifier.</summary>
    public string Value { get; }
}
#endif

/// <summary>In-memory account lookup.</summary>
public sealed class AccountDirectory
{
    private readonly Dictionary<string, AccountInfo> _accounts;

    /// <summary>Creates a directory over the given accounts.</summary>
    public AccountDirectory(IEnumerable<AccountInfo> accounts) =>
        _accounts = accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);

#if !FIXTURE_API_2_0
    /// <summary>Gets an account by its raw identifier.</summary>
    /// <exception cref="KeyNotFoundException">The account does not exist.</exception>
#if FIXTURE_API_1_1
    [Obsolete("Use GetAccount(AccountId) instead. The string overload will be removed in 2.0.")]
#endif
    public AccountInfo GetAccount(string accountId) => Lookup(accountId);
#endif

#if !FIXTURE_API_1_0
    /// <summary>Gets an account by its identifier.</summary>
    /// <exception cref="KeyNotFoundException">The account does not exist.</exception>
    public AccountInfo GetAccount(AccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        return Lookup(accountId.Value);
    }
#endif

    private AccountInfo Lookup(string accountId) =>
        _accounts.TryGetValue(accountId, out var account)
            ? account
            : throw new KeyNotFoundException($"Account '{accountId}' was not found.");
}
