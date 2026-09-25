using System.Text.Json;

namespace UpgradeAgent.Build;

internal enum TestRunnerMode
{
    VSTest,

    TestingPlatform,
}

internal static class TestRunnerDetector
{
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
