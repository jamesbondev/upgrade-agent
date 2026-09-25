using System.Globalization;

namespace RepoKit;

public sealed class GitException(string message, string output) : Exception($"{message}{Environment.NewLine}{output}".TrimEnd());

public sealed record GitCommit(string Sha, DateTimeOffset Date);

public sealed class GitCli
{
    private static readonly Dictionary<string, string?> NonInteractiveEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_LFS_SKIP_SMUDGE"] = "1",
    };

    private readonly IProcessRunner _processRunner;
    private readonly Dictionary<string, string?> _environment;

    public GitCli(IProcessRunner processRunner)
        : this(processRunner, NonInteractiveEnvironment)
    {
    }

    private GitCli(IProcessRunner processRunner, IReadOnlyDictionary<string, string?> environment)
    {
        _processRunner = processRunner;
        _environment = new Dictionary<string, string?>(environment);
    }

    public IReadOnlyDictionary<string, string?> Environment => _environment;

    public GitCli WithEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var merged = new Dictionary<string, string?>(_environment);
        foreach (var (name, value) in environment)
        {
            merged[name] = value;
        }

        return new GitCli(_processRunner, merged);
    }

    public async Task<string> RunAsync(string repoPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var result = await TryRunAsync(repoPath, arguments, cancellationToken);
        return result.Succeeded
            ? result.StandardOutput
            : throw new GitException($"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.", result.CombinedOutput);
    }

    public Task<ProcessResult> TryRunAsync(string repoPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        _processRunner.RunAsync("git", ["-c", "core.quotepath=false", .. arguments], repoPath, _environment, cancellationToken);

    public Task<ProcessResult> CloneAsync(string url, string destination, IReadOnlyList<string> options, CancellationToken cancellationToken = default) =>
        TryRunAsync(Path.GetDirectoryName(Path.GetFullPath(destination))!, ["clone", .. options, "--", url, destination], cancellationToken);

    public async Task<string> HeadAsync(string repoPath, CancellationToken cancellationToken = default) =>
        (await RunAsync(repoPath, ["rev-parse", "HEAD"], cancellationToken)).Trim();

    public async Task<bool> HasCommitsAsync(string repoPath, CancellationToken cancellationToken = default) =>
        (await TryRunAsync(repoPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken)).Succeeded;

    public async Task<IReadOnlyList<string>> StatusAsync(string repoPath, bool includeIgnored, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["status", "--porcelain=v1", "--untracked-files=all"];
        if (includeIgnored)
        {
            arguments.Add("--ignored");
        }

        return SplitLines(await RunAsync(repoPath, arguments, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, CancellationToken cancellationToken = default) =>
        SplitNul(await RunAsync(repoPath, ["ls-files", "-z"], cancellationToken));

    public async Task<string?> FileModeAsync(string repoPath, string relativePath, CancellationToken cancellationToken = default)
    {
        var entries = SplitNul(await RunAsync(repoPath, ["ls-files", "-s", "-z", "--", Literal(relativePath)], cancellationToken));
        return entries.Count == 0 ? null : entries[0].Split(' ', 2)[0];
    }

    public async Task<string?> ShowFileAsync(string repoPath, string commit, string relativePath, CancellationToken cancellationToken = default)
    {
        var result = await TryRunAsync(repoPath, ["show", $"{commit}:{relativePath.Replace('\\', '/')}"], cancellationToken);
        return result.Succeeded ? result.StandardOutput : null;
    }

    public async Task<GitCommit?> LastCommitTouchingAsync(string repoPath, string relativePath, CancellationToken cancellationToken = default)
    {
        var line = (await RunAsync(repoPath, ["log", "-1", "--format=%H%x09%cI", "--", Literal(relativePath)], cancellationToken)).Trim();
        if (line.Length == 0)
        {
            return null;
        }

        var parts = line.Split('\t');
        return new GitCommit(parts[0], DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    public async Task<int> CommitCountSinceAsync(string repoPath, string sinceCommit, IReadOnlyList<string> excludedPaths, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            repoPath,
            ["rev-list", "--count", $"{sinceCommit}..HEAD", "--", ".", .. excludedPaths.Select(p => $":(exclude,literal){p}")],
            cancellationToken);
        return int.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<string>> PathsChangedSinceAsync(string repoPath, string sinceCommit, string? diffFilter = null, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["diff", "--name-only", "-z", "--no-renames"];
        if (diffFilter is not null)
        {
            arguments.Add($"--diff-filter={diffFilter}");
        }

        arguments.AddRange([sinceCommit, "HEAD"]);
        return SplitNul(await RunAsync(repoPath, arguments, cancellationToken));
    }

    internal static IReadOnlyList<string> SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<string> SplitNul(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string Literal(string relativePath) => $":(literal){relativePath.Replace('\\', '/')}";
}
