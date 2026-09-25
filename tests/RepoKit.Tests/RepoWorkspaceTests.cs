using RepoKit.Tests.TestSupport;

namespace RepoKit.Tests;

public sealed class RepoWorkspaceTests : IDisposable
{
    private readonly TempDirectory _workRoot = new("rk-work-");
    private readonly GitCli _git = new(new ProcessRunner());

    public void Dispose() => _workRoot.Dispose();

    [Fact]
    public async Task ClonesALocalRepoAndDisposingDeletesTheClone()
    {
        using var source = await TempRepo.CreateAsync();
        var head = await source.Write("README.md", "# Demo").CommitAsync();

        string clonePath;
        await using (var workspace = await RepoWorkspace.CloneAsync(RepoSource.Local("demo", source.Path), _workRoot.Path, _git))
        {
            clonePath = workspace.Path;
            Assert.Equal(head, await workspace.Git.HeadAsync(workspace.Path));
            Assert.Equal("# Demo", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "README.md")));
        }

        Assert.False(Directory.Exists(clonePath));
    }

    [Fact]
    public async Task KeepLeavesTheCloneInPlace()
    {
        using var source = await TempRepo.CreateAsync();
        await source.Write("README.md", "# Demo").CommitAsync();

        var workspace = await RepoWorkspace.CloneAsync(RepoSource.Local("demo", source.Path), _workRoot.Path, _git);
        workspace.Keep = true;
        await workspace.DisposeAsync();

        Assert.True(File.Exists(Path.Combine(workspace.Path, "README.md")));
    }

    [Fact]
    public async Task ASymbolicLinkIsCheckedOutAsAPlainFile()
    {
        using var source = await TempRepo.CreateAsync();
        await source.Write("docs/guide.md", "guide").CommitAsync();
        await source.CommitSymlinkAsync("README.md", "../../outside/secret");

        await using var workspace = await RepoWorkspace.CloneAsync(RepoSource.Local("demo", source.Path), _workRoot.Path, _git);
        var readme = new FileInfo(Path.Combine(workspace.Path, "README.md"));

        Assert.Null(readme.LinkTarget);
        Assert.Equal("../../outside/secret", await File.ReadAllTextAsync(readme.FullName));
        Assert.Equal("120000", await workspace.Git.FileModeAsync(workspace.Path, "README.md"));
    }

    [Fact]
    public async Task TheAuthorizationHeaderReachesGitOnlyThroughTheEnvironment()
    {
        using var source = await TempRepo.CreateAsync();
        await source.Write("README.md", "# Demo").CommitAsync();
        var url = new Uri(source.Path + Path.DirectorySeparatorChar).AbsoluteUri;

        await using var workspace = await RepoWorkspace.CloneAsync(RepoSource.Remote("demo", url, "Basic c2VjcmV0"), _workRoot.Path, _git);
        var config = await File.ReadAllTextAsync(Path.Combine(workspace.Path, ".git", "config"));

        Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("c2VjcmV0", config, StringComparison.Ordinal);
        Assert.Contains("symlinks = false", config, StringComparison.Ordinal);
        Assert.Equal("AUTHORIZATION: Basic c2VjcmV0", workspace.Git.Environment["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public async Task AnEmptyRepositoryIsACloneErrorAndLeavesNothingBehind()
    {
        using var source = await TempRepo.CreateAsync(bare: true);

        var error = await Assert.ThrowsAsync<CloneException>(() => RepoWorkspace.CloneAsync(RepoSource.Local("empty", source.Path), _workRoot.Path, _git));

        Assert.Equal("the repository is empty", error.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workRoot.Path));
    }

    [Fact]
    public async Task AMissingRepositoryIsACloneError()
    {
        var missing = Path.Combine(_workRoot.Path, "nowhere");

        var error = await Assert.ThrowsAsync<CloneException>(() => RepoWorkspace.CloneAsync(RepoSource.Local("gone", missing), _workRoot.Path, _git));

        Assert.Equal("gone", error.RepoName);
        Assert.Contains("git clone failed", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACloneThatTakesTooLongIsACloneError()
    {
        var git = new GitCli(new HangingRunner());

        var error = await Assert.ThrowsAsync<CloneException>(() =>
            RepoWorkspace.CloneAsync(RepoSource.Local("slow", _workRoot.Path), _workRoot.Path, git, TimeSpan.FromMilliseconds(50)));

        Assert.Contains("took longer than", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsACloneError()
    {
        var git = new GitCli(new HangingRunner());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RepoWorkspace.CloneAsync(RepoSource.Local("slow", _workRoot.Path), _workRoot.Path, git, TimeSpan.FromMinutes(5), cancel.Token));
    }

    [Fact]
    public async Task TwoReposWithTheSameNameGetSeparateFolders()
    {
        using var source = await TempRepo.CreateAsync();
        await source.Write("README.md", "# Demo").CommitAsync();

        await using var first = await RepoWorkspace.CloneAsync(RepoSource.Local("team/api", source.Path), _workRoot.Path, _git);
        await using var second = await RepoWorkspace.CloneAsync(RepoSource.Local("team/api", source.Path), _workRoot.Path, _git);

        Assert.Equal("team-api", Path.GetFileName(first.Path));
        Assert.Equal("team-api-2", Path.GetFileName(second.Path));
    }

    [Theory]
    [InlineData("payments-api", "payments-api")]
    [InlineData("Team Project/My Repo", "Team-Project-My-Repo")]
    [InlineData("..", "repo")]
    public void SafeNameKeepsOneFolderLevel(string name, string expected)
    {
        Assert.Equal(expected, RepoWorkspace.SafeName(name));
    }

    private sealed class HangingRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ProcessResult(0, "", "");
        }
    }
}
