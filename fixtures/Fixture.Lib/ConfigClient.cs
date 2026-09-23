namespace Fixture.Lib;

/// <summary>Accrual settings published by the platform team.</summary>
public sealed record FixtureConfig(string Currency, int DayCountBasis, int RoundingDecimals);

/// <summary>Reads accrual settings.</summary>
public sealed class ConfigClient
{
    private readonly FixtureConfig _config;

    /// <summary>Creates a client that serves the given settings.</summary>
    public ConfigClient(FixtureConfig config) => _config = config;

#if !FIXTURE_API_2_0
    /// <summary>Gets the current settings.</summary>
#if FIXTURE_API_1_1
    [Obsolete("Use GetConfigAsync(CancellationToken) instead. GetConfig will be removed in 2.0.")]
#endif
    public FixtureConfig GetConfig() => _config;
#endif

#if !FIXTURE_API_1_0
    /// <summary>Gets the current settings.</summary>
    public async Task<FixtureConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return _config;
    }
#endif
}
