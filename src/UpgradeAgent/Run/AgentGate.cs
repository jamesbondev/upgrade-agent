using UpgradeAgent.Build;

namespace UpgradeAgent.Run;

/// <summary>Whether a broken group is worth giving to the agent at all.</summary>
internal static class AgentGate
{
    /// <summary>
    /// A bump that breaks this much at once won't be fixed within the agent's budget; say so up front
    /// instead of spending it. Null when the agent should try (or <paramref name="limit"/> is 0).
    /// </summary>
    public static string? TooLargeForAgent(BuildResult build, int limit) =>
        !build.Succeeded && limit > 0 && build.Errors.Count > limit
            ? $"{build.Errors.Count} build errors after the bump (limit {limit}); too large for the agent, upgrade manually"
            : null;
}
