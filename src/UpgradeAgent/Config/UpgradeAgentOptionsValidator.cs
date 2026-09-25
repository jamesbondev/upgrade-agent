using Microsoft.Extensions.Options;

namespace UpgradeAgent.Config;

/// <summary>Rejects settings that would make a run misbehave, before any work starts.</summary>
internal sealed class UpgradeAgentOptionsValidator : IValidateOptions<UpgradeAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, UpgradeAgentOptions options)
    {
        var failures = new List<string>();

        void RequirePositive(int value, string setting)
        {
            if (value <= 0)
            {
                failures.Add($"{setting} must be greater than 0 (it is {value}).");
            }
        }

        void RequireNotNegative(int value, string setting)
        {
            if (value < 0)
            {
                failures.Add($"{setting} must be 0 (off) or more (it is {value}).");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Target.RepoPath))
        {
            failures.Add("Target:RepoPath is not set. Pass --config <file> or set UPGRADEAGENT_Target__RepoPath.");
        }

        RequirePositive(options.Agent.MaxMinutesPerGroup, "Agent:MaxMinutesPerGroup");
        RequirePositive(options.Agent.MaxToolCallsPerGroup, "Agent:MaxToolCallsPerGroup");
        RequireNotNegative(options.Agent.MaxRefusalsPerGroup, "Agent:MaxRefusalsPerGroup");
        RequireNotNegative(options.Agent.MaxBuildsWithoutProgress, "Agent:MaxBuildsWithoutProgress");
        RequireNotNegative(options.Agent.MaxErrorsForAgent, "Agent:MaxErrorsForAgent");
        RequireNotNegative(options.Policy.MaxMajorJump, "Policy:MaxMajorJump");

        if (options.Policy.EffectiveGroups.ContainsKey(Detection.UpgradePlan.PatchMinorGroupName))
        {
            failures.Add($"Policy:Groups can't use the reserved name '{Detection.UpgradePlan.PatchMinorGroupName}'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
