using System.CommandLine;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Preflight;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Cli;

internal static class PlanCommand
{
    private static readonly Option<FileInfo?> PlanOut = new("--plan-out") { Description = "Where to write the plan JSON. Default: <Output:Directory>/plan.json." };

    public static Command Create()
    {
        var command = new Command("plan", "Detect outdated packages and print the upgrade plan. Changes nothing.") { CommonOptions.Only, PlanOut };
        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<PlanCommandHandler>(parseResult, handler => handler.RunAsync(
            parseResult.GetValue(CommonOptions.Only) ?? [], parseResult.GetValue(PlanOut)?.FullName, cancellationToken)));
        return command;
    }
}

internal sealed class PlanCommandHandler(ResolvedConfig config, TargetPreflight preflight, PlanService plans, PlanRenderer renderer)
{
    public async Task<int> RunAsync(IReadOnlyCollection<string> only, string? planOut, CancellationToken cancellationToken)
    {
        var checks = await preflight.RunAsync(config, requireCleanRepo: false, cancellationToken);
        renderer.Preflight(checks);
        if (!checks.All(c => c.Passed))
        {
            return ExitCodes.PreflightFailed;
        }

        var plan = (await plans.DetectAsync(config.RepoPath, config.SolutionPath, cancellationToken))
            .Narrow(only, new PackageFamilies(config.Options.Policy.EffectiveGroups));
        renderer.Plan(plan);

        var path = planOut ?? Path.Combine(config.OutputDirectory, "plan.json");
        await PlanStore.SaveAsync(plan, path, cancellationToken);
        renderer.Note($"Plan written to {path}");
        return ExitCodes.Success;
    }
}
