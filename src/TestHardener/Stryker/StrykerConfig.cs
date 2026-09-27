using System.Text.Json;
using System.Text.Json.Nodes;
using TestHardener.Config;
using TestHardener.Infrastructure;

namespace TestHardener.Stryker;

internal sealed record StrykerRequest(
    string RepoRoot,
    TargetConfig Target,
    TestRunnerMode Runner,
    IReadOnlyList<string> Mutate,
    string? TestFilter,
    bool DisableBail = false);

internal static class StrykerConfig
{
    public static string Build(StrykerRequest request, StrykerOptions options)
    {
        if (request.Runner == TestRunnerMode.TestingPlatform && request.TestFilter is not null)
        {
            throw new ConfigurationException(
                $"{request.Target.Name}: TestFilter needs the VSTest runner, but this repo uses Microsoft.Testing.Platform, where Stryker ignores test-case-filter.");
        }

        var config = new JsonObject
        {
            ["project"] = Path.GetFileName(request.Target.Project),
            ["test-projects"] = Array(request.Target.TestProjects.Select(p => Path.GetFullPath(Path.Combine(request.RepoRoot, p)))),
            ["test-runner"] = request.Runner == TestRunnerMode.TestingPlatform ? "mtp" : "vstest",
            ["coverage-analysis"] = "perTest",
            ["mutation-level"] = options.MutationLevel,
            ["reporters"] = Array(["json", "html"]),
        };

        if (request.DisableBail)
        {
            config["disable-bail"] = true;
        }

        if (request.TestFilter is { } filter)
        {
            config["test-case-filter"] = filter;
        }

        if (request.Mutate.Count > 0)
        {
            config["mutate"] = Array(request.Mutate);
        }

        if (options.EffectiveIgnoreMethods.Count > 0)
        {
            config["ignore-methods"] = Array(options.EffectiveIgnoreMethods);
        }

        if (options.IgnoreMutations.Count > 0)
        {
            config["ignore-mutations"] = Array(options.IgnoreMutations);
        }

        if (options.Concurrency is { } concurrency)
        {
            config["concurrency"] = concurrency;
        }

        return new JsonObject { ["stryker-config"] = config }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
}
