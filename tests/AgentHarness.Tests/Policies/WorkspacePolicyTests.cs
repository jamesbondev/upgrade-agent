using AgentHarness.Policies;

namespace AgentHarness.Tests.Policies;

/// <summary>
/// <see cref="WorkspacePolicy"/> never touches the disk, so these tests use made-up folders under the temp
/// directory. "Workspace" is the agent's folder; "Packages" is a read-only root (e.g. a package cache with docs).
/// </summary>
public class WorkspacePolicyTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "agent-harness-policy", "ws");
    private static readonly string Packages = Path.Combine(Path.GetTempPath(), "agent-harness-policy", "packages");

    private readonly WorkspacePolicy _policy = new(Workspace, o => o.ReadOnlyRoots.Add(Packages));

    // ---- Shell: what runs on its own ----

    [Theory]
    [InlineData("ls -la")]
    [InlineData("cd src && ls -la")]
    [InlineData("grep -rn \"GetConfig(\" src")]
    [InlineData("find . -name \"*.cs\" | head -10")]
    [InlineData("cat README.md 2>&1 | head -50")]
    [InlineData("git diff --stat")]
    [InlineData("git log --oneline -5 && git status")]
    [InlineData("sed -n '1,40p' src/App/Service.cs")]
    [InlineData("Get-ChildItem -Recurse -Filter *.cs")]
    public void ReadOnlyCommandsInsideTheWorkspaceAreApproved(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void AbsolutePathsInsideTheWorkspaceAreApproved()
    {
        var decision = _policy.EvaluateShell($"find {Workspace}/tests -name \"*.cs\"");

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void AbsolutePathsInsideAReadOnlyRootAreApprovedForReadOnlyCommands()
    {
        var decision = _policy.EvaluateShell($"cat {Packages}/some.lib/2.0.0/MIGRATION.md");

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void CdIsTrackedWhenResolvingRelativePaths()
    {
        // From the workspace root "../x" escapes; after "cd src" it points back inside.
        Assert.Equal(ToolVerdict.Reject, _policy.EvaluateShell("cat ../x").Verdict);
        Assert.Equal(ToolVerdict.Approve, _policy.EvaluateShell("cd src && cat ../x").Verdict);
    }

    [Theory]
    [InlineData("cd ..")]
    [InlineData("cd /etc")]
    [InlineData("cd src docs")]
    public void CdMustTakeOneFolderInsideTheWorkspace(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void CdIntoAReadOnlyRootIsRefused()
    {
        var decision = _policy.EvaluateShell($"cd {Packages}");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    // ---- Shell: what is refused, and the feedback the model gets ----

    [Theory]
    [InlineData("git commit -am wip", "read-only git")]
    [InlineData("git stash", "read-only git")]
    [InlineData("git", "read-only git")]
    [InlineData("find . -name '*.cs' -delete", "delete")]
    [InlineData("find . -name '*.tmp' -exec cat", "run commands")]
    [InlineData("sed -i 's/a/b/' src/a.cs", "edit tool")]
    [InlineData("echo hi > src/a.cs", "redirection")]
    [InlineData("cat $(which dotnet)", "substitution")]
    [InlineData("cat `which dotnet`", "backticks")]
    [InlineData("ls &", "background")]
    [InlineData("cat ../../secrets.txt", "outside the working copy")]
    [InlineData("cat ~/.azure/accessTokens.json", "outside the working copy")]
    [InlineData("cd /etc", "outside the working copy")]
    [InlineData("Get-ChildItem | ForEach-Object { Remove-Item $_ }", "script blocks")]
    public void RefusalsSayWhatWasWrong(string command, string expectedReason)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("curl https://example.com")]
    [InlineData("wget https://example.com/pkg.nupkg")]
    [InlineData("Invoke-WebRequest https://example.com")]
    [InlineData("ssh build-box")]
    public void NetworkCommandsAreRefused(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains("No network access", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rm src/Old.cs")]
    [InlineData("mv src/a.cs src/b.cs")]
    [InlineData("cp src/a.cs src/b.cs")]
    [InlineData("Remove-Item src/Old.cs")]
    public void FileMovesAndDeletesAreRefusedInFavourOfTheEditTool(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains("edit tool", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("python3 fix.py", "'python3' is not available")]
    [InlineData("dotnet build", "'dotnet' is not available")]
    public void ProgramsWithoutARuleAreRefusedSoUnattendedRunsNeverWaitOnAPrompt(string command, string expectedReason)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sed --in-place=.bak 's/a/b/' src/a.cs")]
    [InlineData("git diff --output=patch.txt")]
    public void FlagsThatWriteFilesAreRefused(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void ARedirectionReportedByTheRuntimeIsRefused()
    {
        var decision = _policy.EvaluateShell("ls", hasWriteFileRedirection: true);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains("redirection", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PossiblePathsOutsideTheWorkspaceAreRefused()
    {
        var decision = _policy.EvaluateShell("cat notes", possiblePaths: ["/etc/passwd"]);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void PossiblePathsInsideAReadOnlyRootAreAllowed()
    {
        var decision = _policy.EvaluateShell("cat notes", possiblePaths: [Path.Combine(Packages, "notes")]);

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ; ")]
    public void EmptyCommandsAreRefused(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void OutsidePathsInsideFlagValuesAreRefused()
    {
        var decision = _policy.EvaluateShell("diff --from-file=/etc/passwd README.md");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    // ---- Shell: the app's own command rules ----

    [Theory]
    [InlineData("dotnet build App.slnx", ToolVerdict.Approve)]
    [InlineData("dotnet test App.slnx --no-build", ToolVerdict.Approve)]
    [InlineData("dotnet BUILD", ToolVerdict.Approve)]
    [InlineData("dotnet publish", ToolVerdict.Reject)]
    [InlineData("dotnet", ToolVerdict.Reject)]
    public void ApproveVerbsAllowsOnlyTheListedSubCommands(string command, ToolVerdict expected)
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test"));

        var decision = policy.EvaluateShell(command);

        Assert.Equal(expected, decision.Verdict);
    }

    [Fact]
    public void ApproveVerbsRefusalListsTheAvailableSubCommands()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test"));

        var decision = policy.EvaluateShell("dotnet publish");

        Assert.Contains("build, test", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomCommandRuleReceivesTheArgumentsAfterTheProgramName()
    {
        IReadOnlyList<string>? seen = null;
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["make"] = arguments =>
        {
            seen = arguments;
            return ToolDecision.Approve();
        });

        policy.EvaluateShell("make test -j4");

        Assert.Equal(["test", "-j4"], seen);
    }

    [Theory]
    [InlineData("dotnet build --no-restore", ToolVerdict.Approve)]
    [InlineData("dotnet build", ToolVerdict.Reject)]
    public void ACustomCommandRuleDecidesForItsProgram(string command, ToolVerdict expected)
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["dotnet"] = arguments =>
            arguments.Contains("--no-restore") ? ToolDecision.Approve() : ToolDecision.Reject("Packages are restored already: add --no-restore."));

        var decision = policy.EvaluateShell(command);

        Assert.Equal(expected, decision.Verdict);
    }

    [Fact]
    public void ACommandRuleThatAsksMakesTheWholeCommandLineAsk()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["npm"] = CommandRules.Ask("npm can run scripts"));

        var decision = policy.EvaluateShell("ls && npm test");

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
        Assert.Equal("npm can run scripts", decision.Reason);
    }

    [Fact]
    public void ARejectInAnySegmentWinsOverAnAsk()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["npm"] = CommandRules.Ask("npm can run scripts"));

        var decision = policy.EvaluateShell("npm test && curl https://example.com");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void CommandRulesOverrideTheBuiltInRules()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["git"] = CommandRules.Approve("this app lets the agent commit"));

        var decision = policy.EvaluateShell("git commit -am wip");

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void CommandRulesCannotLetAProgramReadOutsideTheWorkspace()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["cat"] = CommandRules.Approve());

        var decision = policy.EvaluateShell("cat ../../etc/passwd");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void CommandRulesCanRejectAReadOnlyCommand()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["tree"] = CommandRules.Reject("tree output is too long; use ls."));

        var decision = policy.EvaluateShell("tree");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Equal("tree output is too long; use ls.", decision.Reason);
    }

    // ---- Configurable refusal messages ----

    [Fact]
    public void NetworkRefusalIsConfigurable()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.NetworkRefusal = "Offline: use the vendored docs.");

        var decision = policy.EvaluateShell("curl https://example.com");

        Assert.Equal("Offline: use the vendored docs.", decision.Reason);
    }

    [Fact]
    public void GitRefusalIsConfigurable()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.GitRefusal = "The app commits for you.");

        var decision = policy.EvaluateShell("git push");

        Assert.Equal("The app commits for you.", decision.Reason);
    }

    [Fact]
    public void UnknownCommandRefusalIsConfigurableAndGetsTheProgramName()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.UnknownCommandRefusal = command => $"{command}: use make instead.");

        var decision = policy.EvaluateShell("python3 fix.py");

        Assert.Equal("python3: use make instead.", decision.Reason);
    }

    [Fact]
    public void SensitiveRefusalIsConfigurableAndGetsTheFileName()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.SensitiveRefusal = name => $"{name} is off limits.");

        Assert.Equal(".env is off limits.", policy.EvaluateRead(Path.Combine(Workspace, "src", ".env")).Reason);
        Assert.Equal(".env is off limits.", policy.EvaluateWrite(Path.Combine(Workspace, "src", ".env")).Reason);
        Assert.Equal(".env is off limits.", policy.EvaluateShell("cat src/.env").Reason);
    }

    // ---- Writes ----

    [Theory]
    [InlineData("src/App/Service.cs", ToolVerdict.Approve)]
    [InlineData("src/App/SERVICE.CS", ToolVerdict.Approve)]
    [InlineData("src/App/App.csproj", ToolVerdict.Ask)]
    [InlineData("Directory.Packages.props", ToolVerdict.Ask)]
    [InlineData(".editorconfig", ToolVerdict.Ask)]
    [InlineData(".git/config", ToolVerdict.Reject)]
    [InlineData(".git", ToolVerdict.Reject)]
    [InlineData("../other-repo/a.cs", ToolVerdict.Reject)]
    public void AutoApprovedEditExtensionsRunOnTheirOwnAndOtherWorkspaceEditsAsk(string relativePath, ToolVerdict expected)
    {
        var policy = new WorkspacePolicy(Workspace, o => o.AutoApprovedEditExtensions.Add(".cs"));

        var decision = policy.EvaluateWrite(Path.Combine(Workspace, relativePath));

        Assert.Equal(expected, decision.Verdict);
    }

    [Fact]
    public void WithoutAutoApprovedExtensionsEveryWorkspaceEditAsks()
    {
        var decision = _policy.EvaluateWrite(Path.Combine(Workspace, "src", "a.cs"));

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
    }

    [Fact]
    public void RelativeWritePathsResolveAgainstTheWorkspace()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.AutoApprovedEditExtensions.Add(".cs"));

        var decision = policy.EvaluateWrite("src/a.cs");

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void ReadOnlyRootsCannotBeWritten()
    {
        var decision = _policy.EvaluateWrite(Path.Combine(Packages, "some.lib", "README.md"));

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    // ---- Reads ----

    [Fact]
    public void ReadsInsideTheWorkspaceAreApproved()
    {
        var decision = _policy.EvaluateRead(Path.Combine(Workspace, "src", "a.cs"));

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void ReadsInsideAReadOnlyRootAreApproved()
    {
        var decision = _policy.EvaluateRead(Path.Combine(Packages, "some.lib", "2.0.0", "MIGRATION.md"));

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public void ReadsInASiblingFolderWithTheSamePrefixAreRefused()
    {
        // "ws-sibling" starts with "ws" but is not inside it.
        var decision = _policy.EvaluateRead(Path.Combine(Path.GetTempPath(), "agent-harness-policy", "ws-sibling", "a.cs"));

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    // ---- Credential files ----

    [Theory]
    [InlineData("nuget.config")]
    [InlineData("NuGet.Config")]
    [InlineData("src/App/.env")]
    [InlineData("certs/signing.pfx")]
    [InlineData("keys/strong.snk")]
    [InlineData("secrets.json")]
    public void CredentialFilesCannotBeRead(string relativePath)
    {
        var decision = _policy.EvaluateRead(Path.Combine(Workspace, relativePath));

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains("credentials", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nuget.config")]
    [InlineData("src/App/.env")]
    [InlineData("certs/signing.pfx")]
    public void CredentialFilesCannotBeWrittenEvenWithAnAutoApprovedExtension(string relativePath)
    {
        var policy = new WorkspacePolicy(Workspace, o => o.AutoApprovedEditExtensions.UnionWith([".config", ".env", ".pfx"]));

        var decision = policy.EvaluateWrite(Path.Combine(Workspace, relativePath));

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Theory]
    [InlineData("cat nuget.config")]
    [InlineData("grep -n password NuGet.Config")]
    [InlineData("Get-Content ./nuget.config")]
    [InlineData("head -5 src/.env")]
    public void ShellCommandsNamingCredentialFilesAreRefused(string command)
    {
        var decision = _policy.EvaluateShell(command);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void PossiblePathsNamingCredentialFilesAreRefused()
    {
        var decision = _policy.EvaluateShell("cat x", possiblePaths: [Path.Combine(Workspace, "nuget.config")]);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void CommandRulesCannotLetAProgramReadACredentialFile()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.Commands["cat"] = CommandRules.Approve());

        var decision = policy.EvaluateShell("cat nuget.config");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public void GitShowOfACredentialFileIsRefused()
    {
        var decision = _policy.EvaluateShell("git show HEAD:nuget.config");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Theory]
    [InlineData("nuget.config", true)]
    [InlineData("src/.npmrc", true)]
    [InlineData("deploy/site.pem", true)]
    [InlineData("deploy/", false)]
    [InlineData("src/App/Program.cs", false)]
    public void IsSensitiveLooksAtTheFileNameAndExtension(string path, bool expected)
    {
        Assert.Equal(expected, _policy.IsSensitive(path));
    }

    // ---- EvaluateAsync: one entry point for every request kind ----

    [Fact]
    public async Task EvaluateAsyncRoutesShellRequestsToTheShellRules()
    {
        var decision = await _policy.EvaluateAsync(new ShellRequest("curl https://example.com"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains("No network access", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsyncPassesTheRuntimesRedirectionFlagThrough()
    {
        var decision = await _policy.EvaluateAsync(new ShellRequest("ls", WritesFile: true), CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public async Task EvaluateAsyncRoutesFileWritesToTheWriteRules()
    {
        var decision = await _policy.EvaluateAsync(new FileWriteRequest(Path.Combine(Workspace, ".git", "HEAD")), CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    [Fact]
    public async Task EvaluateAsyncRoutesFileReadsToTheReadRules()
    {
        var decision = await _policy.EvaluateAsync(new FileReadRequest(Path.Combine(Workspace, "a.cs")), CancellationToken.None);

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public async Task WebFetchesAreLeftToTheOperator()
    {
        var decision = await _policy.EvaluateAsync(new WebFetchRequest("https://example.com"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
    }

    [Fact]
    public async Task CustomToolsNeedingApprovalAreLeftToTheOperator()
    {
        var decision = await _policy.EvaluateAsync(new CustomToolRequest("deploy", "{\"env\":\"prod\"}"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
        Assert.Contains("deploy", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherToolsAreRefused()
    {
        var decision = await _policy.EvaluateAsync(new OtherToolRequest("mcp"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
    }

    // ---- Construction ----

    [Fact]
    public void RootIsNormalisedWithoutATrailingSeparator()
    {
        var policy = new WorkspacePolicy(Workspace + Path.DirectorySeparatorChar);

        Assert.Equal(Workspace, policy.Root);
    }

    // ---- "Read-only" programs with options that write files or run programs ----

    [Theory]
    [InlineData("sort -o .git/config README.md")]
    [InlineData("sort --output=.git/config README.md")]
    [InlineData("uniq README.md .git/hooks/pre-commit")]
    [InlineData("tree -o out.txt")]
    [InlineData("rg --pre ./x.sh foo")]
    [InlineData("sed -n '1e id' README.md")]
    [InlineData("sed 's/a/b/w .git/hooks/post-commit' README.md")]
    [InlineData("sed -n 'w out.txt' README.md")]
    [InlineData("sed -f script.sed README.md")]
    [InlineData("git grep -Osh foo")]
    [InlineData("git grep --open-files-in-pager=vim foo")]
    [InlineData("git diff --ext-diff")]
    [InlineData("git log -p --textconv")]
    public void ReadOnlyProgramsCannotWriteFilesOrRunPrograms(string command)
    {
        Assert.Equal(ToolVerdict.Reject, _policy.EvaluateShell(command).Verdict);
    }

    [Theory]
    [InlineData("sed -n '3,9p' src/App/Service.cs")]
    [InlineData("sed 's/Foo/Bar/g' src/App/Service.cs")]
    [InlineData("sed -n -e '1,5p' -e '10p' README.md")]
    [InlineData("sort README.md | uniq -c")]
    [InlineData("git grep -n foo")]
    [InlineData("git diff --no-ext-diff")]
    public void PrintingFormsOfThoseProgramsStillRun(string command)
    {
        Assert.Equal(ToolVerdict.Approve, _policy.EvaluateShell(command).Verdict);
    }

    [Fact]
    public void AutoApprovedExtensionsWorkWithOrWithoutTheDot()
    {
        var policy = new WorkspacePolicy(Workspace, o => o.AutoApprovedEditExtensions.Add("cs"));

        Assert.Equal(ToolVerdict.Approve, policy.EvaluateWrite(Path.Combine(Workspace, "src", "A.cs")).Verdict);
    }

    [Fact]
    public async Task McpToolsGoToTheOperator()
    {
        var decision = await _policy.EvaluateAsync(new McpToolRequest("github", "create_issue"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
    }
}
