using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Tests.TestSupport;

internal sealed class TempRepo : IDisposable
{
    private readonly TempDirectory _directory = new("ua-repo-");

    private TempRepo()
    {
    }

    public string Path => _directory.Path;

    public GitCli Git { get; } = new(new ProcessRunner());

    public static async Task<TempRepo> CreateAsync()
    {
        var repo = new TempRepo();
        await repo.RunAsync("init", "-q", "-b", "main");
        await repo.RunAsync("config", "user.name", "Test");
        await repo.RunAsync("config", "user.email", "test@example.invalid");
        await repo.RunAsync("config", "commit.gpgsign", "false");

        Directory.CreateDirectory(System.IO.Path.Combine(repo.Path, ".git", "no-hooks"));
        await repo.RunAsync("config", "core.hooksPath", ".git/no-hooks");
        return repo;
    }

    public TempRepo Write(string relativePath, string content)
    {
        _directory.Write(relativePath, content);
        return this;
    }

    public string Read(string relativePath) => _directory.Read(relativePath);

    public void Delete(string relativePath) => _directory.Delete(relativePath);

    public async Task<string> CommitAsync(string message = "commit")
    {
        await RunAsync("add", "-A");
        await RunAsync("commit", "-q", "-m", message);
        return await Git.HeadAsync(Path);
    }

    public Task<string> RunAsync(params string[] arguments) => Git.RunAsync(Path, arguments);

    public void Dispose() => _directory.Dispose();
}
