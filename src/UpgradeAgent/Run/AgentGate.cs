using UpgradeAgent.Build;

namespace UpgradeAgent.Run;

internal static class AgentGate
{
    public static string? TooLargeForAgent(BuildResult build, int limit) =>
        !build.Succeeded && limit > 0 && build.Errors.Count > limit
            ? $"{build.Errors.Count} build errors after the bump (limit {limit}); too large for the agent, upgrade manually"
            : null;
}
