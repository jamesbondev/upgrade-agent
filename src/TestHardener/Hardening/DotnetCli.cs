using System.Xml;
using System.Xml.Linq;
using RepoKit;
using TestHardener.Infrastructure;

namespace TestHardener.Hardening;

internal sealed record BuildOutcome(bool Succeeded, IReadOnlyList<string> Errors, string Output);

internal sealed record TestFailure(string Test, string Message);

internal sealed record TestOutcome(
    bool Succeeded, int Passed, int Failed, int Skipped, IReadOnlyList<string> PassedTests, IReadOnlyList<TestFailure> Failures, string Output, bool TimedOut = false)
{
    public int Total => Passed + Failed + Skipped;
}

internal sealed class DotnetCli(IProcessRunner processRunner)
{
    private const int MaxErrors = 30;

    public async Task<BuildOutcome> BuildAsync(string repoRoot, string project, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "dotnet", ["build", project, "--no-restore", "-tl:off", "-nologo", "-clp:NoSummary"], repoRoot, environment, cancellationToken);
        var errors = result.CombinedOutput.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(l => l.Replace(repoRoot.TrimEnd('/') + "/", "", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxErrors)
            .ToList();
        return new BuildOutcome(result.Succeeded, errors, result.CombinedOutput);
    }

    public async Task<TestOutcome> TestAsync(
        string repoRoot,
        string project,
        string? filter,
        string resultsDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(resultsDirectory))
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }

        List<string> arguments =
            ["test", project, "--no-build", "-tl:off", "-nologo", "--logger", "trx;LogFileName=results.trx", "--results-directory", resultsDirectory];
        if (filter is not null)
        {
            arguments.AddRange(["--filter", filter]);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync("dotnet", arguments, repoRoot, environment, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TestOutcome(false, 0, 0, 0, [], [new TestFailure("the test run", $"didn't finish within {timeout.TotalMinutes:0} minutes, so a test hangs")], "", TimedOut: true);
        }

        var trx = Directory.Exists(resultsDirectory) ? Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories) : [];
        var outcome = trx.Length == 0 ? new TestOutcome(false, 0, 0, 0, [], [], "") : ParseTrx(trx.Select(File.ReadAllText));
        return outcome with { Succeeded = result.Succeeded && outcome.Failed == 0 && outcome.Passed > 0, Output = result.CombinedOutput };
    }

    internal static TestOutcome ParseTrx(IEnumerable<string> documents)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var passed = new List<string>();
        var skipped = 0;
        var failures = new List<TestFailure>();
        foreach (var text in documents)
        {
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (XmlException)
            {
                continue;
            }

            foreach (var result in document.Descendants(ns + "UnitTestResult"))
            {
                var name = (string?)result.Attribute("testName") ?? "?";
                switch ((string?)result.Attribute("outcome"))
                {
                    case "Passed":
                        passed.Add(name);
                        break;
                    case "NotExecuted" or "Inconclusive":
                        skipped++;
                        break;
                    case var outcome:
                        var message = result.Descendants(ns + "Message").FirstOrDefault()?.Value.Trim() ?? outcome ?? "failed";
                        failures.Add(new TestFailure(name, TextFormat.Shorten(message, 600)));
                        break;
                }
            }
        }

        return new TestOutcome(failures.Count == 0 && passed.Count > 0, passed.Count, failures.Count, skipped, passed, failures, "");
    }
}
