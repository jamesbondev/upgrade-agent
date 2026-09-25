using System.Text.Json;

namespace UpgradeAgent.Build;

internal enum TestRunnerMode
{
    /// <summary>Classic VSTest: <c>--logger trx</c>.</summary>
    VSTest,

    /// <summary>Microsoft.Testing.Platform selected in global.json: <c>--solution</c> and <c>--report-trx</c>.</summary>
    TestingPlatform,
}

internal static class TestRunnerDetector
{
    /// <summary>global.json <c>"test": { "runner": "Microsoft.Testing.Platform" }</c> switches dotnet test to MTP mode.</summary>
    public static TestRunnerMode Detect(string repoRoot)
    {
        var globalJson = Path.Combine(repoRoot, "global.json");
        if (!File.Exists(globalJson))
        {
            return TestRunnerMode.VSTest;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(globalJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("test", out var test)
                && test.TryGetProperty("runner", out var runner)
                && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
                    ? TestRunnerMode.TestingPlatform
                    : TestRunnerMode.VSTest;
        }
        catch (JsonException)
        {
            return TestRunnerMode.VSTest;
        }
    }
}
