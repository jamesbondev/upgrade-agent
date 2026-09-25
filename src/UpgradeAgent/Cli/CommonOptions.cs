using System.CommandLine;

namespace UpgradeAgent.Cli;

/// <summary>Options every command accepts.</summary>
internal static class CommonOptions
{
    public static readonly Option<FileInfo> Config = new("--config")
    {
        Description = "JSON config file layered over appsettings.json.",
        Recursive = true,
    };

    public static readonly Option<bool> Verbose = new("--verbose")
    {
        Description = "Trace every external command (git, dotnet) to stderr.",
        Recursive = true,
    };

    public static readonly Option<string[]> Only = new("--only")
    {
        Description = "Limit to these package IDs (globs) or family names. Repeatable.",
        AllowMultipleArgumentsPerToken = true,
    };
}
