using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Run;

internal interface IRunProgress
{
    void Status(string message);

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
