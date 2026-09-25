using ReadmeChecker.Reporting;

namespace ReadmeChecker.Run;

internal sealed class CheckOrchestrator(RepoInspector inspector, ICheckProgress progress, TimeProvider time)
{
    public async Task<CheckReport> RunAsync(CheckArguments arguments, CancellationToken cancellationToken)
    {
        var run = await inspector.PrepareAsync(arguments.Only, cancellationToken);
        var reports = new List<RepoReport>();
        var credits = 0.0;
        try
        {
            for (var i = 0; i < run.Targets.Count; i++)
            {
                progress.RepoStarted(run.Targets[i], i + 1, run.Targets.Count);
                await using var inspection = await inspector.InspectAsync(run.Targets[i], run, inspector.AgentNote(arguments.Provider, credits), cancellationToken);
                var report = inspection.Report;
                credits += report.Stats?.AiCredits ?? 0;
                reports.Add(report);
                await ReportWriter.WriteRepoAsync(report, run.OutputDirectory, cancellationToken);
                progress.RepoFinished(report);
            }
        }
        finally
        {
            RepoInspector.TryDeleteEmpty(run.WorkRoot);
        }

        var check = new CheckReport(run.RunId, run.StartedUtc, time.GetElapsedTime(run.StartedTimestamp), run.OutputDirectory, credits, reports);
        var reportPath = await ReportWriter.WriteAsync(check, cancellationToken);
        progress.RunFinished(check, reportPath);
        return check;
    }
}
