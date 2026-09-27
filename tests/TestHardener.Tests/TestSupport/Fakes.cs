using TestHardener.Config;
using TestHardener.Run;
using TestHardener.Stryker;

namespace TestHardener.Tests.TestSupport;

internal sealed class FakeStryker(Func<StrykerRequest, string?> report) : IStrykerRunner
{
    public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

    public async Task<StrykerRun> RunAsync(
        StrykerRequest request, string outputDirectory, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        Environments.Add(environment);
        if (report(request) is not { } json)
        {
            return new StrykerRun(null, outputDirectory, TimeSpan.Zero, "fake failure");
        }

        var path = Path.Combine(outputDirectory, StrykerRunner.ReportRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return new StrykerRun(MutationReportParser.Parse(json, request.RepoRoot), outputDirectory, TimeSpan.Zero, null);
    }
}

internal sealed class SilentSurveyProgress : ISurveyProgress
{
    public void RepoStarted(RepoTarget target, int index, int count)
    {
    }

    public void TargetStarted(TargetConfig target, bool fromEarlierRun)
    {
    }

    public void TargetFinished(TargetSurvey target)
    {
    }

    public void RepoFinished(RepoSurvey repo)
    {
    }

    public void RunFinished(SurveyReport report, string reportPath)
    {
    }
}
