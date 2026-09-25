namespace RepoKit;

public sealed class CloneException(RepoSource source, string reason)
    : Exception($"Could not clone {source}: {reason}")
{
    public string RepoName { get; } = source.Name;

    public string Reason { get; } = reason;
}

public sealed class RepoWorkspace : IAsyncDisposable
{
    private static readonly string[] CloneOptions =
    [
        "--single-branch", "--no-tags",
        "-c", "core.symlinks=false",
        "-c", "core.autocrlf=false",
        "-c", "core.longpaths=true",
    ];

    private RepoWorkspace(RepoSource source, string path, GitCli git)
    {
        Source = source;
        Path = path;
        Git = git;
    }

    public RepoSource Source { get; }

    public string Path { get; }

    public GitCli Git { get; }

    public bool Keep { get; set; }

    public static string DefaultWorkRoot(string appName) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), appName);

    public static async Task<RepoWorkspace> CloneAsync(
        RepoSource source, string workRoot, GitCli git, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workRoot);
        var destination = FreeDirectory(System.IO.Path.Combine(System.IO.Path.GetFullPath(workRoot), SafeName(source.Name)));
        var sourceGit = source.AuthorizationHeader is { } header ? git.WithEnvironment(GitAuth.HeaderEnvironment(source.Location, header)) : git;

        using var limit = new CancellationTokenSource(timeout ?? Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            var result = await sourceGit.CloneAsync(source.Location, destination, CloneOptions, linked.Token);
            if (!result.Succeeded)
            {
                throw new CloneException(source, $"git clone failed with exit code {result.ExitCode}: {Tail(result.CombinedOutput)}");
            }

            if (!await sourceGit.HasCommitsAsync(destination, linked.Token))
            {
                throw new CloneException(source, "the repository is empty");
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            DeleteDirectory(destination);
            throw new CloneException(source, $"the clone took longer than {timeout}");
        }
        catch
        {
            DeleteDirectory(destination);
            throw;
        }

        return new RepoWorkspace(source, destination, sourceGit);
    }

    public ValueTask DisposeAsync()
    {
        if (!Keep)
        {
            DeleteDirectory(Path);
        }

        return ValueTask.CompletedTask;
    }

    internal static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Append('/').Append('\\').ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('.', '-');
        return safe.Length == 0 ? "repo" : safe;
    }

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FreeDirectory(string path)
    {
        var candidate = path;
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = $"{path}-{i}";
        }

        return candidate;
    }

    private static string Tail(string output) =>
        string.Join(" | ", GitCli.SplitLines(output).TakeLast(5));
}
