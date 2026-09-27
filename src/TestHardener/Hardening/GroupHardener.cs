using System.Globalization;
using System.Text.Json.Serialization;
using AgentHarness;
using AgentHarness.Copilot;
using AgentHarness.Policies;
using RepoKit;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Infrastructure;

namespace TestHardener.Hardening;

internal enum GroupOutcome
{
    Verified,
    Rejected,
    Failed,
    Skipped,
}

internal sealed record GroupResult
{
    public required int Number { get; init; }

    public required string Target { get; init; }

    public required SurvivorGroup Group { get; init; }

    public required GroupOutcome Outcome { get; init; }

    public string? Reason { get; init; }

    public OwnedFile? Owned { get; init; }

    public IReadOnlyList<Verification> Rounds { get; init; } = [];

    public GroupSummary? Summary { get; init; }

    public AgentStats? Stats { get; init; }

    public TimeSpan Duration { get; init; }

    [JsonIgnore]
    public Verification? Final => Rounds.Count == 0 ? null : Rounds[^1];
}

internal sealed record GroupJob(
    int Number,
    string RepoName,
    string RepoRoot,
    GitCli Git,
    TargetConfig Target,
    TestRunnerMode Runner,
    SurvivorGroup Group,
    IReadOnlyCollection<string> TrackedFiles,
    IReadOnlyDictionary<string, string> Kept,
    IReadOnlyList<string> ConventionFiles,
    string? TestNamePattern,
    string OutputDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    double? CreditBudget = null);

internal interface IGroupHardener
{
    Task<GroupResult> HardenAsync(GroupJob job, IAgentBackend backend, CancellationToken cancellationToken);
}

internal sealed class GroupHardener(IGroupVerifier verifier, DotnetCli dotnet, ResolvedConfig config, TimeProvider time) : IGroupHardener
{
    public async Task<GroupResult> HardenAsync(GroupJob job, IAgentBackend backend, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var result = new GroupResult { Number = job.Number, Target = job.Target.Name, Group = job.Group, Outcome = GroupOutcome.Failed };
        var owned = OwnedFileChooser.Choose(job.Group, job.Target, job.TrackedFiles, file => ReadTracked(job, file));
        if (owned is null)
        {
            return result with { Reason = "no test project to write the tests in", Duration = time.GetElapsedTime(started) };
        }

        result = result with { Owned = owned };
        var ownedPath = Path.Combine(job.RepoRoot, owned.Path);
        var snapshot = job.Kept.TryGetValue(owned.Path, out var kept) ? kept : File.Exists(ownedPath) ? await File.ReadAllTextAsync(ownedPath, cancellationToken) : null;
        if (snapshot is null)
        {
            snapshot = await WriteSkeletonAsync(job, owned, ownedPath, cancellationToken);
            if (snapshot is null)
            {
                return result with { Outcome = GroupOutcome.Rejected, Reason = "a new test file for this class doesn't compile", Duration = time.GetElapsedTime(started) };
            }
        }

        try
        {
            var attempt = await RunSessionAsync(job, backend, owned, snapshot, cancellationToken);
            if (attempt.Rounds.Count > 0 && attempt.Rounds[^1].Passed)
            {
                return result with { Outcome = GroupOutcome.Verified, Rounds = attempt.Rounds, Summary = attempt.Summary, Stats = attempt.Stats, Duration = time.GetElapsedTime(started) };
            }

            await RestoreAsync(ownedPath, snapshot, owned.IsNew && !job.Kept.ContainsKey(owned.Path), cancellationToken);
            return result with
            {
                Outcome = attempt.Failure is null ? GroupOutcome.Rejected : GroupOutcome.Failed,
                Reason = attempt.Failure ?? LastFeedback(attempt.Rounds),
                Rounds = attempt.Rounds,
                Summary = attempt.Summary,
                Stats = attempt.Stats,
                Duration = time.GetElapsedTime(started),
            };
        }
        catch
        {
            await RestoreAsync(ownedPath, snapshot, owned.IsNew && !job.Kept.ContainsKey(owned.Path), CancellationToken.None);
            throw;
        }
    }

    private async Task<(IReadOnlyList<Verification> Rounds, GroupSummary? Summary, AgentStats? Stats, string? Failure)> RunSessionAsync(
        GroupJob job, IAgentBackend backend, OwnedFile owned, string snapshot, CancellationToken cancellationToken)
    {
        var agent = config.Options.Agent;
        var survivors = job.Group.Survivors.Take(config.Options.Hardening.MaxSurvivorsPerGroup).ToList();
        await using var log = FileAgentLog.Open(Path.Combine(job.OutputDirectory, "agent.log"), time);
        var options = new AgentSessionOptions
        {
            Name = $"harden {job.Group.Member.Name}",
            WorkingDirectory = job.RepoRoot,
            Instructions = HardeningPrompts.System(owned),
            Policy = HardeningPolicy.Create(job.RepoRoot, owned.Path, job.Target.TestProjects, () => CheckBeforeTests(job, owned, snapshot)),
            Limits = new AgentLimits { MaxDuration = TimeSpan.FromMinutes(agent.MaxMinutes), MaxToolCalls = agent.MaxToolCalls, MaxRefusals = agent.MaxRefusals },
            Observers = [log.Observer],
            StopRules = job.CreditBudget is { } budget ? [CreditStop(budget)] : [],
        };

        var task = new HardeningTask(
            job.RepoName, job.Group, survivors, MemberSource(job), owned, snapshot,
            job.ConventionFiles.Where(f => job.TrackedFiles.Contains(f)).ToList(), job.TestNamePattern, job.Target.TestProjects);
        var rounds = new List<Verification>();
        AgentSession? session = null;
        try
        {
            session = await new AgentRunner(backend, ApprovalPrompter.DeclineAll, time).StartAsync(options, cancellationToken);
            var message = HardeningPrompts.Task(task);
            for (var round = 1; round <= config.Options.Hardening.MaxRounds; round++)
            {
                var reply = await session.SendAsync(message, cancellationToken);
                ThrowIfQuotaExceeded(reply.StopReason);
                var verification = await verifier.VerifyAsync(
                    new VerifyRequest(job, survivors, owned, snapshot, Path.Combine(job.OutputDirectory, $"round-{round}")), cancellationToken);
                rounds.Add(verification);
                log.Note($"--- round {round}: {(verification.Passed ? "passed" : "failed")} {string.Join("; ", verification.Steps.Select(s => $"{s.Name}: {(s.Passed ? "ok" : "failed")}"))}");
                if (verification.Passed || reply.Stopped)
                {
                    break;
                }

                message = HardeningPrompts.Feedback(verification);
            }

            var summary = await session.AskAsync<GroupSummary>(HardeningPrompts.SummaryQuestion, cancellationToken);
            ThrowIfQuotaExceeded(summary.Error);
            return (rounds, summary.Value, session.Stats, null);
        }
        catch (AgentUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ThrowIfQuotaExceeded(ex.GetBaseException().Message);
            return (rounds, null, session?.Stats, $"the agent failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    internal static IStopRule CreditStop(double budget)
    {
        var spent = 0.0;
        return AgentObserver.StopWhen(e =>
        {
            if (e is ModelUsage usage)
            {
                spent += usage.AiCredits;
            }

            return spent > budget
                ? string.Create(CultureInfo.InvariantCulture, $"agent stopped: the AI credit cap was reached ({spent:0.##} of {budget:0.##} credits left for this run)")
                : null;
        });
    }

    internal static string? CheckBeforeTests(GroupJob job, OwnedFile owned, string snapshot)
    {
        var path = Path.Combine(job.RepoRoot, owned.Path);
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        if (current == snapshot)
        {
            return null;
        }

        var check = TestFileChecker.Check(snapshot, current, job.TestNamePattern);
        return check.Passed ? null : string.Join(Environment.NewLine, check.Problems.Select(p => $"- {p}"));
    }

    private async Task<string?> WriteSkeletonAsync(GroupJob job, OwnedFile owned, string ownedPath, CancellationToken cancellationToken)
    {
        var skeleton = OwnedFileChooser.Skeleton(owned, ReadTracked(job, owned.TestProject), ReadTracked(job, job.Group.File), job.Group.Member.OuterType);
        Directory.CreateDirectory(Path.GetDirectoryName(ownedPath)!);
        await File.WriteAllTextAsync(ownedPath, skeleton, cancellationToken);
        var built = false;
        try
        {
            built = (await dotnet.BuildAsync(job.RepoRoot, owned.TestProject, job.Environment, cancellationToken)).Succeeded;
            return built ? skeleton : null;
        }
        finally
        {
            if (!built)
            {
                File.Delete(ownedPath);
            }
        }
    }

    private static async Task RestoreAsync(string path, string snapshot, bool delete, CancellationToken cancellationToken)
    {
        if (delete)
        {
            File.Delete(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, snapshot, cancellationToken);
        }
    }

    private static string MemberSource(GroupJob job)
    {
        var text = ReadTracked(job, job.Group.File) ?? "";
        var member = job.Group.Member;
        return member.SpanEnd <= text.Length && member.SpanStart < member.SpanEnd ? text[member.SpanStart..member.SpanEnd] : "";
    }

    private static string? ReadTracked(GroupJob job, string file)
    {
        if (!job.TrackedFiles.Contains(file))
        {
            return null;
        }

        var path = Path.Combine(job.RepoRoot, file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string LastFeedback(IReadOnlyList<Verification> rounds) =>
        rounds.Count == 0 ? "the agent made no change to check" : rounds[^1].Feedback ?? "the checks didn't pass";

    private static void ThrowIfQuotaExceeded(string? message)
    {
        if (CopilotQuota.IsExceeded(message))
        {
            throw new AgentUnavailableException($"Copilot's usage quota is used up, so the run stopped: {message}");
        }
    }
}
