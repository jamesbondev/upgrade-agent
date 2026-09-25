using RepoKit.Tests.TestSupport;

namespace RepoKit.Tests;

public class GitCliTests
{
    [Fact]
    public async Task LastCommitTouchingFindsTheLatestCommitForThatPath()
    {
        using var repo = await TempRepo.CreateAsync();
        var readme = await repo.Write("README.md", "# Demo").CommitAsync("readme");
        await repo.Write("src/a.cs", "class A;").CommitAsync("code");

        var commit = await repo.Git.LastCommitTouchingAsync(repo.Path, "README.md");

        Assert.Equal(readme, commit?.Sha);
        Assert.True(commit?.Date > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task LastCommitTouchingIsNullForAPathWithNoHistory()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("src/a.cs", "class A;").CommitAsync();

        Assert.Null(await repo.Git.LastCommitTouchingAsync(repo.Path, "README.md"));
    }

    [Fact]
    public async Task CommitCountSinceLeavesOutCommitsThatOnlyTouchExcludedPaths()
    {
        using var repo = await TempRepo.CreateAsync();
        var start = await repo.Write("README.md", "v1").CommitAsync();
        await repo.Write("src/a.cs", "class A;").CommitAsync();
        await repo.Write("README.md", "v2").CommitAsync();
        await repo.Write("src/b.cs", "class B;").Write("README.md", "v3").CommitAsync();

        Assert.Equal(2, await repo.Git.CommitCountSinceAsync(repo.Path, start, ["README.md"]));
        Assert.Equal(3, await repo.Git.CommitCountSinceAsync(repo.Path, start, []));
    }

    [Fact]
    public async Task PathsChangedSinceCanListOnlyAddedFiles()
    {
        using var repo = await TempRepo.CreateAsync();
        var start = await repo.Write("src/a.cs", "class A;").CommitAsync();
        await repo.Write("src/a.cs", "class A { }").Write("src/New Project/New.csproj", "<Project />").CommitAsync();

        Assert.Equal(["src/New Project/New.csproj"], await repo.Git.PathsChangedSinceAsync(repo.Path, start, "A"));
        Assert.Equal(["src/New Project/New.csproj", "src/a.cs"], await repo.Git.PathsChangedSinceAsync(repo.Path, start));
    }

    [Fact]
    public async Task ListFilesKeepsSpacesAndUnicodeInNames()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("docs/Getting Started.md", "x").Write("docs/naïve.md", "y").CommitAsync();

        Assert.Equal(["docs/Getting Started.md", "docs/naïve.md"], await repo.Git.ListFilesAsync(repo.Path));
    }

    [Fact]
    public async Task FileModeShowsASymbolicLink()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("docs/guide.md", "x").CommitAsync();
        await repo.CommitSymlinkAsync("README.md", "docs/guide.md");

        Assert.Equal("120000", await repo.Git.FileModeAsync(repo.Path, "README.md"));
        Assert.Equal("100644", await repo.Git.FileModeAsync(repo.Path, "docs/guide.md"));
        Assert.Null(await repo.Git.FileModeAsync(repo.Path, "missing.md"));
    }

    [Fact]
    public void WithEnvironmentAddsToTheNonInteractiveDefaults()
    {
        var git = new GitCli(new ProcessRunner()).WithEnvironment(new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "1" });

        Assert.Equal("1", git.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("0", git.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("1", git.Environment["GIT_LFS_SKIP_SMUDGE"]);
    }

    [Fact]
    public async Task RunAsyncThrowsWithGitsOutputWhenGitFails()
    {
        using var repo = await TempRepo.CreateAsync();

        var error = await Assert.ThrowsAsync<GitException>(() => repo.Git.RunAsync(repo.Path, ["rev-parse", "no-such-ref"]));

        Assert.Contains("no-such-ref", error.Message, StringComparison.Ordinal);
    }
}
