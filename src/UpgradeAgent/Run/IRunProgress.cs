using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Run;

/// <summary>
/// What a run reports as it goes. The orchestration only announces events; how they look (console, CI log,
/// a test's recording fake) is up to the implementation.
/// </summary>
internal interface IRunProgress
{
    void Status(string message);

    /// <summary>Wraps a slow phase that never prompts (detection), so a silent pause doesn't look like a hang.</summary>
    Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> action);

    void WorkspaceReady(RunWorkspace workspace);

    void BaselineReady(Baseline baseline, bool fromCache);

    void PlanReady(UpgradePlan plan);

    void GroupStarted(UpdateGroup group, int index, int count);

    void Bumped(BumpResult bump);

    void Built(string label, BuildResult build);

    void Tested(TestRunResult tests);

    void Fixed(FixOutcome fix);

    void GuardrailsChecked(GuardrailReport report);

    void GroupFinished(GroupResult result);

    void RunFinished(RunReport report, IReadOnlyList<string> commits);
}
