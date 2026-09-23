using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Run;

/// <summary>Real git hooks on a temp repo: the company's hooks (e.g. ggshield) must apply to agent commits.</summary>
public sealed class GroupCommitterTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _start;

    public GroupCommitterTests()
    {
        _start = _repo.Write("src/A.cs", "class A {}\n").Commit("base");
        _repo.Write("src/A.cs", "class A { int X; }\n");
    }

    [Fact]
    public async Task CommitsTheVerifiedTree()
    {
        var result = await new GroupCommitter(_repo.Git, runHooks: true).CommitAsync(_repo.Path, _start, "chore(deps): bump", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(await _repo.Git.HeadAsync(_repo.Path), result.Commit);
    }

    [Fact]
    public async Task AFailingHookRejectsTheGroupWithItsOutput()
    {
        InstallHook("echo 'ggshield: 1 secret detected' >&2; exit 1");

        var result = await new GroupCommitter(_repo.Git, runHooks: true).CommitAsync(_repo.Path, _start, "chore(deps): bump", CancellationToken.None);

        Assert.Null(result.Commit);
        Assert.Contains("ggshield: 1 secret detected", result.Error, StringComparison.Ordinal);
        Assert.Equal(_start, await _repo.Git.HeadAsync(_repo.Path));
    }

    [Fact]
    public async Task AHookThatRewritesFilesIsUndone()
    {
        InstallHook("echo '// reformatted' >> src/A.cs; git add src/A.cs");

        var result = await new GroupCommitter(_repo.Git, runHooks: true).CommitAsync(_repo.Path, _start, "chore(deps): bump", CancellationToken.None);

        Assert.Null(result.Commit);
        Assert.Contains("changed the committed files after verification", result.Error, StringComparison.Ordinal);
        Assert.Equal(_start, await _repo.Git.HeadAsync(_repo.Path));
    }

    [Fact]
    public async Task HooksCanBeSkippedExplicitly()
    {
        InstallHook("exit 1");

        var result = await new GroupCommitter(_repo.Git, runHooks: false).CommitAsync(_repo.Path, _start, "chore(deps): bump", CancellationToken.None);

        Assert.NotNull(result.Commit);
    }

    public void Dispose() => _repo.Dispose();

    private void InstallHook(string body)
    {
        var hooks = Path.Combine(_repo.Path, ".git", "test-hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _repo.Run("config", "core.hooksPath", ".git/test-hooks");
    }
}
