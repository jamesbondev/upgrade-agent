using Microsoft.Extensions.Options;

namespace ReadmeChecker.Config;

internal sealed class OptionsValidator : IValidateOptions<ReadmeCheckerOptions>
{
    public ValidateOptionsResult Validate(string? name, ReadmeCheckerOptions options)
    {
        List<string> failures = [];

        if (options.Repos.Count == 0)
        {
            failures.Add("Repos is empty. Add at least one repo: { \"Name\": \"my-repo\" } or { \"Name\": \"demo\", \"Path\": \"../demo\" }.");
        }

        foreach (var (repo, index) in options.Repos.Select((r, i) => (r, i)))
        {
            if (string.IsNullOrWhiteSpace(repo.Name))
            {
                failures.Add($"Repos[{index}] has no Name.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(repo.Path)
                && string.IsNullOrWhiteSpace(repo.OrganizationUrl ?? options.AzureDevOps.OrganizationUrl))
            {
                failures.Add($"Repo '{repo.Name}' has no Path and there is no AzureDevOps:OrganizationUrl.");
            }

            if (string.IsNullOrWhiteSpace(repo.Path) && string.IsNullOrWhiteSpace(repo.Project ?? options.AzureDevOps.Project))
            {
                failures.Add($"Repo '{repo.Name}' has no Path and there is no AzureDevOps:Project.");
            }
        }

        foreach (var duplicate in options.Repos.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            failures.Add($"Repo '{duplicate.Key}' is listed {duplicate.Count()} times.");
        }

        RequirePositive(options.Readme.RecentCommits, "Readme:RecentCommits");
        RequirePositive(options.Readme.MaxCandidates, "Readme:MaxCandidates");
        RequirePositive(options.Agent.MaxMinutes, "Agent:MaxMinutes");
        RequirePositive(options.Agent.MaxToolCalls, "Agent:MaxToolCalls");
        RequirePositive(options.Agent.MaxRefusals, "Agent:MaxRefusals");
        RequirePositive(options.Output.CloneTimeoutMinutes, "Output:CloneTimeoutMinutes");
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
}
