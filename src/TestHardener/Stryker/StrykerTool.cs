using RepoKit;

namespace TestHardener.Stryker;

internal sealed class StrykerToolException(string message) : Exception(message);

internal sealed class StrykerTool(IProcessRunner processRunner)
{
    public const string PackageId = "dotnet-stryker";

    public static string ExecutablePath(string toolPath) =>
        Path.Combine(toolPath, OperatingSystem.IsWindows() ? "dotnet-stryker.exe" : "dotnet-stryker");

    public async Task<string> EnsureAsync(string toolPath, string version, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(toolPath);
        var installed = await InstalledVersionAsync(toolPath, environment, cancellationToken);
        if (string.Equals(installed, version, StringComparison.OrdinalIgnoreCase))
        {
            return ExecutablePath(toolPath);
        }

        if (installed is not null)
        {
            await RunAsync(["tool", "uninstall", PackageId, "--tool-path", toolPath], toolPath, environment, cancellationToken);
        }

        await RunAsync(["tool", "install", PackageId, "--version", version, "--tool-path", toolPath], toolPath, environment, cancellationToken);
        var now = await InstalledVersionAsync(toolPath, environment, cancellationToken);
        return string.Equals(now, version, StringComparison.OrdinalIgnoreCase)
            ? ExecutablePath(toolPath)
            : throw new StrykerToolException($"Installed {PackageId} into {toolPath}, but it reports version {now ?? "none"} instead of {version}.");
    }

    internal static string? ParseVersion(string toolListOutput)
    {
        foreach (var line in toolListOutput.Split('\n'))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length >= 2 && words[0].Equals(PackageId, StringComparison.OrdinalIgnoreCase))
            {
                return words[1];
            }
        }

        return null;
    }

    private async Task<string?> InstalledVersionAsync(string toolPath, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync("dotnet", ["tool", "list", "--tool-path", toolPath], toolPath, environment, cancellationToken);
        return result.Succeeded ? ParseVersion(result.StandardOutput) : null;
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, string toolPath, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync("dotnet", arguments, toolPath, environment, cancellationToken);
        if (!result.Succeeded)
        {
            throw new StrykerToolException($"dotnet {string.Join(' ', arguments)} failed: {Tail(result.CombinedOutput)}");
        }
    }

    private static string Tail(string output) => output.Length <= 2000 ? output.Trim() : output[^2000..].Trim();
}
