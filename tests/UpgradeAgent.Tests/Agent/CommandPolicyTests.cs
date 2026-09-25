using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class CommandPolicyTests
{
    private static readonly string Worktree = Path.Combine(Path.GetTempPath(), "ua-policy", "wt");
    private static readonly string Packages = Path.Combine(Path.GetTempPath(), "ua-policy", "nuget");
    private readonly CommandPolicy _policy = new(Worktree, [Packages]);

    [Theory]
    [InlineData("dotnet build LoanLedger.slnx --no-restore")]
    [InlineData("dotnet build LoanLedger.slnx --no-restore 2>&1 | head -50")]
    [InlineData("dotnet test LoanLedger.slnx --no-build --nologo --verbosity minimal")]
    [InlineData("dotnet test LoanLedger.slnx --no-build --filter \"FullyQualifiedName~Statement\"")]
    [InlineData("dotnet build LoanLedger.slnx --no-restore && dotnet test LoanLedger.slnx --no-build 2>&1 | tail -20")]
    [InlineData("cd src && ls -la")]
    [InlineData("grep -rn \"GetConfig(\" src")]
    [InlineData("find . -name \"*.cs\" | head -10")]
    [InlineData("git diff --stat")]
    [InlineData("sed -n '1,40p' src/LoanLedger/StatementService.cs")]
    [InlineData("Get-ChildItem -Recurse -Filter *.cs")]
    public void AutoApprovesBuildsTestsAndReads(string command)
    {
        Assert.Equal(PolicyVerdict.Approve, _policy.EvaluateShell(command, false).Verdict);
    }

    [Fact]
    public void AutoApprovesAbsolutePathsInsideTheWorktreeAndPackageFolder()
    {
        Assert.Equal(PolicyVerdict.Approve, _policy.EvaluateShell($"find {Worktree}/tests -name \"*.cs\"", false).Verdict);
        Assert.Equal(PolicyVerdict.Approve, _policy.EvaluateShell($"cat {Packages}/fixture.lib/2.0.0/MIGRATION.md", false).Verdict);
    }

    [Theory]
    [InlineData("dotnet restore", "already restored")]
    [InlineData("dotnet build LoanLedger.slnx", "--no-restore")]
    [InlineData("dotnet test LoanLedger.slnx", "--no-build")]
    [InlineData("dotnet build LoanLedger.slnx --no-restore -p:TreatWarningsAsErrors=false", "property overrides")]
    [InlineData("dotnet add package Foo", "managed by UpgradeAgent")]
    [InlineData("git commit -am wip", "read-only git")]
    [InlineData("git stash", "read-only git")]
    [InlineData("find . -name '*.cs' -delete", "delete")]
    [InlineData("sed -i 's/a/b/' src/a.cs", "edit tool")]
    [InlineData("echo hi > src/a.cs", "redirection")]
    [InlineData("cat $(which dotnet)", "substitution")]
    [InlineData("cat `which dotnet`", "backticks")]
    [InlineData("dotnet build x.slnx --no-restore &", "background")]
    [InlineData("cat ../../secrets.txt", "outside the working copy")]
    [InlineData("cat ~/.azure/accessTokens.json", "outside the working copy")]
    [InlineData("cd /etc", "outside the working copy")]
    [InlineData("Get-ChildItem | ForEach-Object { Remove-Item $_ }", "script blocks")]
    public void RejectsWithActionableFeedback(string command, string expectedReason)
    {
        var decision = _policy.EvaluateShell(command, false);

        Assert.Equal(PolicyVerdict.Reject, decision.Verdict);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TracksCdWhenResolvingRelativePaths()
    {
        // From the worktree root "../x" escapes; after "cd src" it doesn't.
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateShell("cat ../x", false).Verdict);
        Assert.Equal(PolicyVerdict.Approve, _policy.EvaluateShell("cd src && cat ../x", false).Verdict);
    }

    [Theory]
    [InlineData("curl https://example.com", "No network access")]
    [InlineData("wget https://example.com/pkg.nupkg", "No network access")]
    [InlineData("Invoke-WebRequest https://example.com", "No network access")]
    [InlineData("rm src/Old.cs", "edit tool")]
    [InlineData("mv src/a.cs src/b.cs", "edit tool")]
    [InlineData("python3 fix.py", "not available")]
    [InlineData("dotnet format", "not available")]
    [InlineData("dotnet build x.slnx --no-restore --some-new-flag", "not allowed")]
    public void RejectsAnythingElseSoRunsNeverWaitOnAPrompt(string command, string expectedReason)
    {
        var decision = _policy.EvaluateShell(command, false);

        Assert.Equal(PolicyVerdict.Reject, decision.Verdict);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsWhenCopilotReportsFileRedirection()
    {
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateShell("dotnet build x.slnx --no-restore", hasWriteFileRedirection: true).Verdict);
    }

    [Fact]
    public void RejectsPossiblePathsOutsideTheWorktree()
    {
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateShell("cat notes", false, ["/etc/passwd"]).Verdict);
    }

    [Theory]
    [InlineData("src/LoanLedger/StatementService.cs", nameof(PolicyVerdict.Approve))]
    [InlineData("src/LoanLedger/LoanLedger.csproj", nameof(PolicyVerdict.AskOperator))]
    [InlineData("Directory.Packages.props", nameof(PolicyVerdict.AskOperator))]
    [InlineData(".editorconfig", nameof(PolicyVerdict.AskOperator))]
    [InlineData(".git/config", nameof(PolicyVerdict.Reject))]
    [InlineData("../other-repo/a.cs", nameof(PolicyVerdict.Reject))]
    public void WritesAreApprovedOnlyForSourceFilesInTheWorktree(string relativePath, string expected)
    {
        Assert.Equal(Enum.Parse<PolicyVerdict>(expected), _policy.EvaluateWrite(Path.Combine(Worktree, relativePath)).Verdict);
    }

    [Fact]
    public void ReadsAreLimitedToTheWorktreeAndPackageFolder()
    {
        Assert.Equal(PolicyVerdict.Approve, _policy.EvaluateRead(Path.Combine(Worktree, "src", "a.cs")).Verdict);
        Assert.Equal(PolicyVerdict.Approve, _policy.EvaluateRead(Path.Combine(Packages, "fixture.lib", "2.0.0", "MIGRATION.md")).Verdict);
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateRead(Path.Combine(Path.GetTempPath(), "ua-policy", "wt-sibling", "a.cs")).Verdict);
    }
}

public class CommandPolicyCredentialFileTests
{
    private static readonly string Worktree = Path.Combine(Path.GetTempPath(), "ua-policy", "wt");
    private readonly CommandPolicy _policy = new(Worktree, []);

    [Theory]
    [InlineData("nuget.config")]
    [InlineData("NuGet.Config")]
    [InlineData("src/App/.env")]
    [InlineData("certs/signing.pfx")]
    [InlineData("keys/strong.snk")]
    public void CredentialFilesCannotBeReadOrWritten(string relativePath)
    {
        var path = Path.Combine(Worktree, relativePath);

        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateRead(path).Verdict);
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateWrite(path).Verdict);
        Assert.Contains("credentials", _policy.EvaluateRead(path).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cat nuget.config")]
    [InlineData("grep -n password NuGet.Config")]
    [InlineData("Get-Content ./nuget.config")]
    [InlineData("head -5 src/.env")]
    public void ShellCommandsNamingCredentialFilesAreRefused(string command)
    {
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateShell(command, false).Verdict);
    }

    [Fact]
    public void PossiblePathsNamingCredentialFilesAreRefused()
    {
        Assert.Equal(PolicyVerdict.Reject, _policy.EvaluateShell("cat x", false, [Path.Combine(Worktree, "nuget.config")]).Verdict);
    }
}
