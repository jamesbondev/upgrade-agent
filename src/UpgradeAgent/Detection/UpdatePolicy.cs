using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

/// <summary>A rule's verdict on a step. Reason says why it isn't planned.</summary>
internal sealed record UpdateVerdict(UpdateDecision Decision, string Reason);

/// <summary>One policy rule. Returns null when the rule has nothing to say about the step.</summary>
internal interface IUpdateRule
{
    UpdateVerdict? Evaluate(VersionStep step);
}

/// <summary>
/// Policy:* applied to a step: the first rule with a verdict decides; a step no rule objects to is planned.
/// Adding a rule means adding an <see cref="IUpdateRule"/> to the list.
/// </summary>
internal sealed class UpdatePolicy(PolicyOptions policy)
{
    private readonly IReadOnlyList<IUpdateRule> _rules =
    [
        new DenyListRule(policy),
        new AllowListRule(policy),
        new NeedsHumanRule(),
        new MaxAutoBumpRule(policy),
        new AttemptMajorsRule(policy),
        new MaxMajorJumpRule(policy),
    ];

    public UpdateVerdict? Evaluate(VersionStep step) => _rules.Select(r => r.Evaluate(step)).FirstOrDefault(v => v is not null);

    private sealed class DenyListRule(PolicyOptions policy) : IUpdateRule
    {
        public UpdateVerdict? Evaluate(VersionStep step) =>
            policy.Deny.FirstOrDefault(d => Glob.IsMatch(d.Id, step.Id)) is { } deny
                ? new(UpdateDecision.Skipped, deny.Reason is null ? "denied by policy" : $"denied: {deny.Reason}")
                : null;
    }

    private sealed class AllowListRule(PolicyOptions policy) : IUpdateRule
    {
        public UpdateVerdict? Evaluate(VersionStep step) =>
            policy.Allow.Count > 0 && !Glob.IsMatchAny(policy.Allow, step.Id) ? new(UpdateDecision.Skipped, "not in Allow list") : null;
    }

    private sealed class NeedsHumanRule : IUpdateRule
    {
        public UpdateVerdict? Evaluate(VersionStep step) => step.ManualReason is { } reason ? new(UpdateDecision.Manual, reason) : null;
    }

    private sealed class MaxAutoBumpRule(PolicyOptions policy) : IUpdateRule
    {
        public UpdateVerdict? Evaluate(VersionStep step) =>
            step.Kind > policy.MaxAutoBump
                ? new(UpdateDecision.Skipped, $"{step.Kind.ToString().ToLowerInvariant()} bump exceeds MaxAutoBump ({policy.MaxAutoBump})")
                : null;
    }

    private sealed class AttemptMajorsRule(PolicyOptions policy) : IUpdateRule
    {
        public UpdateVerdict? Evaluate(VersionStep step) =>
            step.Kind == BumpKind.Major && !policy.AttemptMajors ? new(UpdateDecision.Skipped, "major bumps disabled (AttemptMajors=false)") : null;
    }

    /// <summary>Far-behind packages (8 → 16) are for a human; 0.x packages are exempt, since their minors already count as majors.</summary>
    private sealed class MaxMajorJumpRule(PolicyOptions policy) : IUpdateRule
    {
        public UpdateVerdict? Evaluate(VersionStep step)
        {
            var jump = step.To.Major - step.From.Major;
            return step.Kind == BumpKind.Major && step.From.Major > 0 && policy.MaxMajorJump > 0 && jump > policy.MaxMajorJump
                ? new(UpdateDecision.Manual, $"{jump} major versions behind (limit {policy.MaxMajorJump}); upgrade manually or set a TargetOverride to a closer major")
                : null;
        }
    }
}
