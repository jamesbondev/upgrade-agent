using Microsoft.Extensions.Options;

namespace TestHardener.Config;

internal sealed class OptionsValidator : IValidateOptions<TestHardenerOptions>
{
    private static readonly string[] MutationLevels = ["Basic", "Standard", "Advanced", "Complete"];

    public ValidateOptionsResult Validate(string? name, TestHardenerOptions options)
    {
        List<string> failures = [];

        if (options.Repos.Count == 0)
        {
            failures.Add("Repos is empty. Add at least one repo with a Solution and Targets.");
        }

        foreach (var (repo, index) in options.Repos.Select((r, i) => (r, i)))
        {
            if (string.IsNullOrWhiteSpace(repo.Name))
            {
                failures.Add($"Repos[{index}] has no Name.");
                continue;
            }

            ValidateRepo(repo, options.AzureDevOps, failures);
        }

        foreach (var duplicate in options.Repos.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            failures.Add($"Repo '{duplicate.Key}' is listed {duplicate.Count()} times.");
        }

        if (!MutationLevels.Contains(options.Stryker.MutationLevel, StringComparer.Ordinal))
        {
            failures.Add($"Stryker:MutationLevel must be one of {string.Join(", ", MutationLevels)}.");
        }

        if (string.IsNullOrWhiteSpace(options.Stryker.Version))
        {
            failures.Add("Stryker:Version is empty. Pin a dotnet-stryker version, like 5.0.0.");
        }

        if (string.IsNullOrWhiteSpace(options.Stryker.ToolPath))
        {
            failures.Add("Stryker:ToolPath is empty.");
        }

        if (options.Stryker.Concurrency is <= 0)
        {
            failures.Add("Stryker:Concurrency must be more than 0, or left out for Stryker's default.");
        }

        RequirePositive(options.Stryker.TimeoutMinutesPerTarget, "Stryker:TimeoutMinutesPerTarget");
        RequirePositive(options.Hardening.MaxGroupsPerRun, "Hardening:MaxGroupsPerRun");
        RequirePositive(options.Hardening.MaxSurvivorsPerGroup, "Hardening:MaxSurvivorsPerGroup");
        RequirePositive(options.Hardening.FixHistoryDays, "Hardening:FixHistoryDays");
        RequirePositive(options.Output.CloneTimeoutMinutes, "Output:CloneTimeoutMinutes");
        RequirePositive(options.Hardening.MaxRounds, "Hardening:MaxRounds");
        RequirePositive(options.Hardening.OriginalRuns, "Hardening:OriginalRuns");
        RequirePositive(options.Agent.MaxMinutes, "Agent:MaxMinutes");
        RequirePositive(options.Agent.MaxToolCalls, "Agent:MaxToolCalls");
        RequirePositive(options.Agent.MaxRefusals, "Agent:MaxRefusals");
        if (options.Agent.MaxAiCreditsPerRun < 0)
        {
            failures.Add("Agent:MaxAiCreditsPerRun must be 0 (no cap) or more.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void RequirePositive(int value, string setting)
        {
            if (value <= 0)
            {
                failures.Add($"{setting} must be more than 0.");
            }
        }
    }

    private static void ValidateRepo(RepoOptions repo, AzureDevOpsSettings defaults, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(repo.Path) && string.IsNullOrWhiteSpace(repo.OrganizationUrl ?? defaults.OrganizationUrl))
        {
            failures.Add($"Repo '{repo.Name}' has no Path and there is no AzureDevOps:OrganizationUrl.");
        }

        if (string.IsNullOrWhiteSpace(repo.Path) && string.IsNullOrWhiteSpace(repo.Project ?? defaults.Project))
        {
            failures.Add($"Repo '{repo.Name}' has no Path and there is no AzureDevOps:Project.");
        }

        if (string.IsNullOrWhiteSpace(repo.Solution))
        {
            failures.Add($"Repo '{repo.Name}' has no Solution, the .sln or .slnx to restore.");
        }

        if (repo.Targets.Count == 0)
        {
            failures.Add($"Repo '{repo.Name}' has no Targets. Add one per source project: {{ \"Project\": \"src/App/App.csproj\", \"TestProjects\": [ ... ] }}.");
        }

        foreach (var (target, index) in repo.Targets.Select((t, i) => (t, i)))
        {
            var label = $"Repo '{repo.Name}' Targets[{index}]";
            if (!IsRelativeProject(target.Project))
            {
                failures.Add($"{label}: Project must be a repo-relative path to a .csproj.");
            }

            if (target.TestProjects.Count == 0 || !target.TestProjects.All(IsRelativeProject))
            {
                failures.Add($"{label}: TestProjects must list at least one repo-relative .csproj.");
            }
        }

        if (!repo.VerifyTestProjects.All(IsRelativeProject))
        {
            failures.Add($"Repo '{repo.Name}': VerifyTestProjects must be repo-relative .csproj paths.");
        }

        if (repo.TestNamePattern is { Length: > 0 } pattern && !IsRegex(pattern))
        {
            failures.Add($"Repo '{repo.Name}': TestNamePattern is not a valid regular expression.");
        }

        foreach (var duplicate in repo.Targets.GroupBy(t => ConfigLoader.TargetName(t.Project), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            failures.Add($"Repo '{repo.Name}' lists the target {duplicate.Key} {duplicate.Count()} times.");
        }
    }

    private static bool IsRegex(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsRelativeProject(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        && !Path.IsPathRooted(path)
        && !ConfigLoader.RepoPath(path).Split('/').Contains("..", StringComparer.Ordinal);
}
