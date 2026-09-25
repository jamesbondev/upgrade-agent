using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Tests.TestSupport;

/// <summary>A throwaway git repo in the temp folder.</summary>
internal sealed class TempRepo : IDisposable
{
    public TempRepo()
    {
        Path = Directory.CreateTempSubdirectory("ua-test-").FullName;
        Git = new GitCli(new ProcessRunner());
        Run("init", "-q", "-b", "main");
        Run("config", "user.name", "Test");
        Run("config", "user.email", "test@example.invalid");
        Run("config", "commit.gpgsign", "false");

        // Keep a developer's global hooks (e.g. a ggshield core.hooksPath) out of throwaway test repos.
        Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git", "no-hooks"));
        Run("config", "core.hooksPath", ".git/no-hooks");
    }

    public string Path { get; }

    public GitCli Git { get; }

    public TempRepo Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public string Read(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    public void Delete(string relativePath) => File.Delete(System.IO.Path.Combine(Path, relativePath));

    public string Commit(string message = "commit")
    {
        Run("add", "-A");
        Run("commit", "-q", "-m", message);
        return Git.HeadAsync(Path).GetAwaiter().GetResult();
    }

    public string Run(params string[] arguments) => Git.RunAsync(Path, arguments).GetAwaiter().GetResult();

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Path, recursive: true);
    }
}
