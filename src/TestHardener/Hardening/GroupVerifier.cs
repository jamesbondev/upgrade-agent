using System.Globalization;
using System.Text;
using RepoKit;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Infrastructure;
using TestHardener.Stryker;

namespace TestHardener.Hardening;

internal sealed record CheckStep(string Name, bool Passed, string? Detail = null);

internal sealed record SurvivorOutcome(string Id, string Mutator, int Line, string Status, IReadOnlyList<string> KilledBy);

internal sealed record Verification
{
    public required string OwnedPath { get; init; }

    public required bool Passed { get; init; }

    public IReadOnlyList<CheckStep> Steps { get; init; } = [];

    public string? Feedback { get; init; }

    public IReadOnlyList<NewTest> NewTests { get; init; } = [];

    public IReadOnlyList<SurvivorOutcome> Survivors { get; init; } = [];

    public IReadOnlyDictionary<string, int> KillsPerTest { get; init; } = new Dictionary<string, int>();

    public int Killed => Survivors.Count(s => s.Status == KilledStatus);

    public const string KilledStatus = "Killed";
}

internal sealed record VerifyRequest(
    string RepoRoot,
    GitCli Git,
    TargetConfig Target,
    TestRunnerMode Runner,
    SurvivorGroup Group,
    IReadOnlyList<Survivor> Survivors,
    OwnedFile Owned,
    string? Snapshot,
    IReadOnlyDictionary<string, string> Kept,
    string? TestNamePattern,
    string OutputDirectory,
    IReadOnlyDictionary<string, string?> Environment);

internal interface IGroupVerifier
{
    Task<Verification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken);
}

internal sealed class GroupVerifier(DotnetCli dotnet, IStrykerRunner stryker, HardeningOptions options) : IGroupVerifier
{
    public async Task<Verification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        var steps = new List<CheckStep>();
        Verification Fail(string step, string feedback, IReadOnlyList<NewTest>? tests = null) =>
            new() { OwnedPath = request.Owned.Path, Passed = false, Steps = [.. steps, new CheckStep(step, false, feedback)], Feedback = feedback, NewTests = tests ?? [] };

        DeleteTestResults(request);
        if (await GuardDiffAsync(request, cancellationToken) is { } stray)
        {
            return Fail("only the owned file changed", stray);
        }

        steps.Add(new CheckStep("only the owned file changed", true));
        var path = Path.Combine(request.RepoRoot, request.Owned.Path);
        if (!File.Exists(path))
        {
            return Fail("static checks", $"{request.Owned.Path} doesn't exist any more. Recreate it with your tests.");
        }

        var check = TestFileChecker.Check(request.Snapshot, await File.ReadAllTextAsync(path, cancellationToken), request.TestNamePattern);
        if (!check.Passed)
        {
            return Fail("static checks", string.Join(Environment.NewLine, check.Problems.Select(p => $"- {p}")), check.NewTests);
        }

        steps.Add(new CheckStep("static checks", true, $"{check.NewTests.Count} new tests or rows"));
        var build = await dotnet.BuildAsync(request.RepoRoot, request.Owned.TestProject, request.Environment, cancellationToken);
        if (!build.Succeeded)
        {
            var errors = build.Errors.Count > 0 ? string.Join(Environment.NewLine, build.Errors) : Tail(build.Output);
            return Fail("build", $"The test project doesn't build:{Environment.NewLine}{errors}", check.NewTests);
        }

        steps.Add(new CheckStep("build", true));
        var filter = Filter(check.NewTests);
        for (var run = 1; run <= options.OriginalRuns; run++)
        {
            var outcome = await dotnet.TestAsync(
                request.RepoRoot, request.Owned.TestProject, filter, Path.Combine(request.OutputDirectory, $"original-{run}"), request.Environment, cancellationToken);
            if (!outcome.Succeeded)
            {
                return Fail($"passes on the original code ({options.OriginalRuns} runs)", OriginalFailure(outcome, run), check.NewTests);
            }
        }

        steps.Add(new CheckStep($"passes on the original code ({options.OriginalRuns} runs)", true));
        return await KillCheckAsync(request, check.NewTests, filter, steps, cancellationToken);
    }

    internal static string Filter(IReadOnlyList<NewTest> tests) =>
        string.Join('|', tests.Select(t => t.FullyQualifiedName).Distinct(StringComparer.Ordinal).Select(n => $"FullyQualifiedName={n}"));

    internal static string MutateSpan(SurvivorGroup group, TargetConfig target)
    {
        var projectFolder = target.Project.Contains('/', StringComparison.Ordinal) ? target.Project[..target.Project.LastIndexOf('/')] + "/" : "";
        var relative = group.File.StartsWith(projectFolder, StringComparison.Ordinal) ? group.File[projectFolder.Length..] : group.File;
        return $"**/{relative}{{{group.Member.SpanStart}..{group.Member.SpanEnd}}}";
    }

    internal static (IReadOnlyList<SurvivorOutcome> Survivors, Dictionary<string, int> KillsPerTest) Match(
        IReadOnlyList<Survivor> targeted, string file, MutationReport report, IReadOnlyList<NewTest> newTests)
    {
        var byKey = report.Mutants.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        var newMethods = newTests.Select(t => t.FullyQualifiedName).ToHashSet(StringComparer.Ordinal);
        var kills = newTests.Select(t => t.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var outcomes = new List<SurvivorOutcome>();
        foreach (var survivor in targeted)
        {
            var key = new MutantKey(file, survivor.Mutator, survivor.Location, survivor.Replacement);
            if (!byKey.TryGetValue(key, out var mutant))
            {
                outcomes.Add(new SurvivorOutcome(survivor.Id, survivor.Mutator, survivor.Location.Start.Line, "Unmatched", []));
                continue;
            }

            var killers = mutant.KilledBy
                .Select(id => report.TestNames.GetValueOrDefault(id))
                .OfType<string>()
                .Select(StripArguments)
                .Where(newMethods.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var killed = mutant.Status == MutantStatus.Killed && killers.Count > 0;
            foreach (var killer in killed ? killers : [])
            {
                kills[killer]++;
            }

            var status = killed ? Verification.KilledStatus
                : mutant.Status == MutantStatus.NoCoverage ? "NotReached"
                : mutant.Status == MutantStatus.Killed ? "KilledByOtherTests"
                : mutant.Status.ToString();
            outcomes.Add(new SurvivorOutcome(survivor.Id, survivor.Mutator, survivor.Location.Start.Line, status, killers));
        }

        return (outcomes, kills);
    }

    private async Task<Verification> KillCheckAsync(
        VerifyRequest request, IReadOnlyList<NewTest> newTests, string filter, List<CheckStep> steps, CancellationToken cancellationToken)
    {
        var strykerRequest = new StrykerRequest(request.RepoRoot, request.Target, request.Runner, [MutateSpan(request.Group, request.Target)], filter, DisableBail: true);
        var run = await stryker.RunAsync(strykerRequest, Path.Combine(request.OutputDirectory, "stryker"), request.Environment, cancellationToken);
        if (run.Report is not { } report)
        {
            var feedback = $"Stryker couldn't check the mutants: {run.Failure}";
            return new Verification { OwnedPath = request.Owned.Path, Passed = false, Steps = [.. steps, new CheckStep("kills mutants", false, feedback)], Feedback = feedback, NewTests = newTests };
        }

        var (survivors, kills) = Match(request.Survivors, request.Group.File, report, newTests);
        var killed = survivors.Count(s => s.Status == Verification.KilledStatus);
        var idle = kills.Where(k => k.Value == 0).Select(k => k.Key).ToList();
        var passed = killed > 0 && idle.Count == 0;
        var detail = $"{killed} of {survivors.Count} targeted mutants killed";
        var result = new Verification
        {
            OwnedPath = request.Owned.Path,
            Passed = passed,
            Steps = [.. steps, new CheckStep("kills mutants", passed, detail)],
            NewTests = newTests,
            Survivors = survivors,
            KillsPerTest = kills,
        };
        return passed ? result : result with { Feedback = KillFeedback(survivors, idle, request.Survivors) };
    }

    private static string KillFeedback(IReadOnlyList<SurvivorOutcome> outcomes, IReadOnlyList<string> idle, IReadOnlyList<Survivor> survivors)
    {
        var builder = new StringBuilder();
        if (outcomes.All(o => o.Status != Verification.KilledStatus))
        {
            builder.AppendLine("Your tests pass, but none of them fails when any of the listed mutations is applied, so they don't catch any survivor.");
        }

        if (idle.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"These tests catch none of the listed mutants; change them so they do, or remove them: {string.Join(", ", idle)}.");
        }

        builder.AppendLine("Where each mutant stands now:");
        foreach (var outcome in outcomes)
        {
            var survivor = survivors.First(s => s.Id == outcome.Id);
            var meaning = outcome.Status switch
            {
                Verification.KilledStatus => "caught",
                "NotReached" => "your tests don't execute this code",
                "Survived" => "your tests execute it but still pass with the mutation",
                "KilledByOtherTests" => "caught, but not by one of your new tests",
                "Timeout" => "the mutation made your tests hang; that doesn't count",
                _ => outcome.Status,
            };
            builder.AppendLine(CultureInfo.InvariantCulture, $"- id {outcome.Id}, line {outcome.Line}, {outcome.Mutator} ({Flat(survivor.Original)} → {Flat(survivor.Replacement ?? "?")}): {meaning}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string OriginalFailure(TestOutcome outcome, int run)
    {
        if (outcome.Total == 0)
        {
            return "No new test ran. Check that the new tests are public methods with [Fact] or [Theory] in a public class, and that the file is in the test project.";
        }

        var failures = string.Join(Environment.NewLine, outcome.Failures.Take(10).Select(f => $"- {f.Test}: {f.Message}"));
        return run == 1
            ? $"These tests fail on the current code, which is correct by definition here. Fix the expectation or the setup:{Environment.NewLine}{failures}"
            : $"These tests passed at first but failed on run {run}, so they're flaky. Remove whatever depends on timing, order or shared state:{Environment.NewLine}{failures}";
    }

    private static async Task<string?> GuardDiffAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        var stray = new List<string>();
        foreach (var line in await request.Git.StatusAsync(request.RepoRoot, includeIgnored: false, cancellationToken))
        {
            if (line.Length < 4)
            {
                continue;
            }

            var path = line[3..].Trim('"');
            if (path == request.Owned.Path)
            {
                continue;
            }

            var full = Path.Combine(request.RepoRoot, path);
            if (request.Kept.TryGetValue(path, out var kept))
            {
                if (File.Exists(full) && await File.ReadAllTextAsync(full, cancellationToken) == kept)
                {
                    continue;
                }

                await File.WriteAllTextAsync(full, kept, cancellationToken);
            }
            else if (line.StartsWith("??", StringComparison.Ordinal))
            {
                File.Delete(full);
            }
            else
            {
                await request.Git.TryRunAsync(request.RepoRoot, ["checkout", "--", path], cancellationToken);
            }

            stray.Add(path);
        }

        return stray.Count == 0
            ? null
            : $"Only {request.Owned.Path} may change. These other changes were undone: {string.Join(", ", stray)}.";
    }

    private static void DeleteTestResults(VerifyRequest request)
    {
        foreach (var project in request.Target.TestProjects)
        {
            var folder = Path.Combine(request.RepoRoot, Path.GetDirectoryName(project) ?? "", "TestResults");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        var root = Path.Combine(request.RepoRoot, "TestResults");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string StripArguments(string name)
    {
        var parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        return parenthesis > 0 ? name[..parenthesis] : name;
    }

    private static string Flat(string text)
    {
        var flat = string.Join(' ', text.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 80 ? flat[..80] + "…" : flat;
    }

    private static string Tail(string output) => output.Length <= 2000 ? output.Trim() : output[^2000..].Trim();
}
