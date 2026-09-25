using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Publishing;

/// <summary>
/// After a run: the PR description always; the push only through push_branch, with the operator's approval;
/// then a draft PR when the branch really was pushed to Azure DevOps.
/// </summary>
internal sealed class RunPublisher(ResolvedConfig config, GitCli git, PublishRenderer renderer)
{
    public const string DescriptionFileName = "pr-description.md";

    /// <param name="azureDevOps">The real destination with --ado; null means a dry run.</param>
    public async Task PublishAsync(RunReport report, IPushPublisher publisher, AzureDevOpsPublisher? azureDevOps, CancellationToken cancellationToken)
    {
        var description = PrDescription.Create(report);
        var latest = Path.Combine(config.OutputDirectory, DescriptionFileName);
        await File.WriteAllTextAsync(Path.Combine(report.OutputDirectory, DescriptionFileName), description, CancellationToken.None);
        await File.WriteAllTextAsync(latest, description, CancellationToken.None);

        PushResult? push = null;
        if (report.Ledger.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            renderer.PublishHeader(publisher.How);
            IPushDestination destination = azureDevOps is null ? new DryRunDestination(git, report.WorktreePath) : azureDevOps;
            push = await publisher.PublishAsync(new PushBranchTool(git, report, destination), cancellationToken);
        }

        renderer.Published(latest, push);
        if (azureDevOps is not null && push is { Pushed: true })
        {
            var url = await azureDevOps.CreatePullRequestAsync(report, PrDescription.Title(report), description, CancellationToken.None);
            renderer.PullRequestCreated(config.Options.AzureDevOps.Label, url);
        }
    }
}
