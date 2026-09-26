using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Detection;

public class IgnoredPathsTests
{
    [Fact]
    public async Task MissingPathsAndCommandTargetsThatGitIgnoresAreDropped()
    {
        using var repo = await IgnoringRepoAsync();
        var scan = new SignalScan(
            [
                Signal.NotInRepository(SignalKind.MissingPath, 1, "logs/app.json", "logs/app.json"),
                Signal.NotInRepository(SignalKind.MissingCommandTarget, 2, "./run.local.sh", "run.local.sh"),
                Signal.NotInRepository(SignalKind.MissingPath, 3, "src/gone.json", "src/gone.json"),
            ],
            4);

        var kept = await IgnoredPaths.DropIgnoredAsync(scan, repo.Path, repo.Git, CancellationToken.None);

        Assert.Equal(["src/gone.json"], kept.Signals.Select(s => s.Target));
        Assert.Equal(4, kept.Dropped);
    }

    [Fact]
    public async Task ABrokenLinkIsKeptEvenWhenGitIgnoresItsTarget()
    {
        using var repo = await IgnoringRepoAsync();
        var scan = new SignalScan([Signal.NotInRepository(SignalKind.BrokenLink, 1, "logs/old.md", "logs/old.md")], 0);

        var kept = await IgnoredPaths.DropIgnoredAsync(scan, repo.Path, repo.Git, CancellationToken.None);

        Assert.Equal(SignalKind.BrokenLink, Assert.Single(kept.Signals).Kind);
    }

    private static async Task<TempRepo> IgnoringRepoAsync()
    {
        var repo = await TempRepo.CreateAsync();
        await repo.Write(".gitignore", "logs/\n*.local.sh\n").Write("README.md", "x").CommitAsync();
        return repo;
    }
}
