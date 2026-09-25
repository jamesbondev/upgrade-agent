using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Detection;

public class SignalFinderTests
{
    [Fact]
    public async Task NamesNoCodeFileContainsAreCandidates()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo
            .Write("README.md", "The ReviewJobHandler uses ITenantContext and IReviewEngine.\n")
            .Write("docs/design.md", "ITenantContext was removed.")
            .Write("src/ReviewJobHandler.cs", "class ReviewJobHandler(IReviewEngine engine);")
            .CommitAsync();
        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        var scan = await SignalFinder.FindAsync(facts, repo.Path, repo.Git, CancellationToken.None);

        var signal = Assert.Single(scan.Signals);
        Assert.Equal((SignalKind.MissingIdentifier, "ITenantContext", 1), (signal.Kind, signal.Text, signal.Line));
    }

    [Fact]
    public async Task ARepoWithoutCodeHasNoIdentifierSignals()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("README.md", "The ReviewJobHandler does it.\n").Write("docs/a.md", "x").CommitAsync();
        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        Assert.Empty((await SignalFinder.FindAsync(facts, repo.Path, repo.Git, CancellationToken.None)).Signals);
    }
}
