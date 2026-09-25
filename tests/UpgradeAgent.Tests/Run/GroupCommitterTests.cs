using UpgradeAgent.Config;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Run;

/// <summary>Real git hooks on a temp repo: the company's hooks (e.g. ggshield) must apply to agent commits.</summary>
public sealed class GroupCommitterTests : IAsyncLifetime
{
    private TempRepo _repo = null!;
    private string _start = null!;

    public async Task InitializeAsync()
    {
        _repo = await TempRepo.CreateAsync();
        _start = await _repo.Write("src/A.cs", "class A {}\n").CommitAsync("base");
        _repo.Write("src/A.cs", "class A { int X; }\n");
    }

    [Fact]
    public async Task CommitsTheVerifiedTree()
    {
        var result = await CommitAsync(runHooks: true);

        Assert.Equal(await _repo.Git.HeadAsync(_repo.Path), Assert.IsType<CommitResult.Committed>(result).Sha);
    }

    [Fact]
    public async Task AFailingHookRefusesTheCommitWithItsOutput()
    {
        await InstallHookAsync("echo 'ggshield: 1 secret detected' >&2; exit 1");

        var result = await CommitAsync(runHooks: true);

        Assert.Contains("ggshield: 1 secret detected", Assert.IsType<CommitResult.Refused>(result).Reason, StringComparison.Ordinal);
        Assert.Equal(_start, await _repo.Git.HeadAsync(_repo.Path));
    }

    [Fact]
    public async Task AHookThatRewritesFilesIsRefused()
    {
        await InstallHookAsync("echo '// reformatted' >> src/A.cs; git add src/A.cs");

        var result = await CommitAsync(runHooks: true);

        Assert.Contains("changed the committed files after verification", Assert.IsType<CommitResult.Refused>(result).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HooksCanBeSkippedExplicitly()
    {
        await InstallHookAsync("exit 1");

        Assert.IsType<CommitResult.Committed>(await CommitAsync(runHooks: false));
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    private Task<CommitResult> CommitAsync(bool runHooks) =>
        new GroupCommitter(_repo.Git, new TargetOptions { RunGitHooks = runHooks }).CommitAsync(_repo.Path, "chore(deps): bump", CancellationToken.None);

    private async Task InstallHookAsync(string body)
    {
        var hooks = Path.Combine(_repo.Path, ".git", "test-hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-commit");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await _repo.RunAsync("config", "core.hooksPath", ".git/test-hooks");
    }
}
