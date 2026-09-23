using System.Globalization;

namespace Fixture.Lib;

/// <summary>Formats monetary values for statements and reports.</summary>
public static class ValueFormatter
{
#if !FIXTURE_API_2_0
    /// <summary>Formats a value with two decimals and group separators.</summary>
#if FIXTURE_API_1_1
    [Obsolete("Use ValueFormatter.FormatValue(decimal) instead. Format will be removed in 2.0.")]
#endif
    public static string Format(decimal value) => FormatCore(value);
#endif

#if !FIXTURE_API_1_0
    /// <summary>Formats a value with two decimals and group separators.</summary>
    public static string FormatValue(decimal value) => FormatCore(value);
#endif

    private static string FormatCore(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);
}
