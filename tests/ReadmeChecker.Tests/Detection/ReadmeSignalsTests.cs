using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Detection;

public class ReadmeSignalsTests
{
    [Fact]
    public void ABrokenRelativeLinkIsCertain()
    {
        var facts = FactsBuilder.Readme("[Setup](docs/setup.md) and [Old](docs/old.md)", ["docs/setup.md"]);

        var signal = Assert.Single(FactsBuilder.Signals(facts));

        Assert.Equal((SignalKind.BrokenLink, "docs/old.md", "docs/old.md", 1), (signal.Kind, signal.Text, signal.Target, signal.Line));
        Assert.True(signal.Definitive);
    }

    [Fact]
    public void LinksResolveFromTheReadmeFolderAndALeadingSlashMeansTheRepoRoot()
    {
        var facts = FactsBuilder.Readme(
            "[g](guide.md) [a](/src/a.cs) [up](../src/a.cs) [x](missing.md)",
            ["docs/guide.md", "src/a.cs"],
            readmePath: "docs/README.md");

        Assert.Equal(["docs/missing.md"], FactsBuilder.Signals(facts).Select(s => s.Target));
    }

    [Fact]
    public void AnchorsQueriesEncodedSpacesAndOtherSchemesAreHandled()
    {
        var facts = FactsBuilder.Readme(
            "[a](#intro) [b](docs/Getting%20Started.md#install) [c](docs/Getting%20Started.md?plain=1) [d](https://example.com/x.md) [e](mailto:a@example.com)",
            ["docs/Getting Started.md"]);

        Assert.Empty(FactsBuilder.Signals(facts));
    }

    [Fact]
    public void AFolderLinkCountsWhenTheFolderHasFiles()
    {
        var facts = FactsBuilder.Readme("[src](src/) [gone](gone/)", ["src/a.cs"]);

        Assert.Equal(["gone"], FactsBuilder.Signals(facts).Select(s => s.Target));
    }

    [Fact]
    public void HtmlImageSourcesAreChecked()
    {
        var facts = FactsBuilder.Readme("""<p><img src="docs/logo.png" alt="logo"></p>""", ["docs/other.png"]);

        Assert.Equal(("docs/logo.png", SignalKind.BrokenLink), FactsBuilder.Signals(facts).Select(s => (s.Target, s.Kind)).Single());
    }

    [Fact]
    public void AStaleDotnetRunProjectIsACommandTarget()
    {
        var facts = FactsBuilder.Readme("""
            ```bash
            dotnet run --project src/OldApp -- --verbose
            ```
            """, ["src/NewApp/NewApp.csproj"]);

        var signal = Assert.Single(FactsBuilder.Signals(facts));

        Assert.Equal((SignalKind.MissingCommandTarget, "src/OldApp", 2), (signal.Kind, signal.Text, signal.Line));
    }

    [Fact]
    public void CdChangesTheFolderLaterCommandsRunIn()
    {
        var inSrc = FactsBuilder.Readme("```sh\ncd src\n./build.sh\n```", ["src/build.sh"]);
        var atRoot = FactsBuilder.Readme("```sh\n./build.sh\n```", ["src/build.sh"]);

        Assert.Empty(FactsBuilder.Signals(inSrc));
        Assert.Equal(SignalKind.MissingCommandTarget, Assert.Single(FactsBuilder.Signals(atRoot)).Kind);
    }

    [Fact]
    public void ACdIntoAMissingFolderIsReportedAndTheRestOfTheBlockIsSkipped()
    {
        var facts = FactsBuilder.Readme("```sh\ncd services/api\ndotnet run --project Nowhere.csproj\n```", ["src/a.cs"]);

        Assert.Equal(["cd into a folder that isn't in the repository"], FactsBuilder.Signals(facts).Select(s => s.Detail));
    }

    [Fact]
    public void FoldersCreatedEarlierInTheBlockAreNotMissing()
    {
        var facts = FactsBuilder.Readme("```sh\ndotnet new console -o MyApp\ncd MyApp\ndotnet run --project MyApp/MyApp.csproj\n```", ["src/a.cs"]);

        Assert.Empty(FactsBuilder.Signals(facts));
    }

    [Theory]
    [InlineData("`<your-repo>/src/x.cs`")]
    [InlineData("`$HOME/x.json`")]
    [InlineData("`~/.nuget/packages`")]
    [InlineData("`/usr/local/bin/tool.sh`")]
    [InlineData("`https://example.com/z.md`")]
    [InlineData("`C:\\tools\\x.json`")]
    [InlineData("`{name}.csproj`")]
    [InlineData("`src/*.cs`")]
    [InlineData("`\".cs\"`")]
    [InlineData("`Target:RepoPath`")]
    [InlineData("`myorg/image`")]
    [InlineData("`bin/Debug/app.dll`")]
    public void TokensThatArentRepoPathsAreIgnored(string markdown)
    {
        Assert.Empty(FactsBuilder.Signals(FactsBuilder.Readme(markdown, ["src/a.cs"])));
    }

    [Fact]
    public void AFenceLanguageIsItsFirstWordEvenWhenMarkdigDecodesASpaceIntoIt()
    {
        var facts = FactsBuilder.Readme("```bash&#32;title\n./gone.sh\n```\n\n```text&#32;x\nIGoneService\n```\n");

        Assert.Equal([(SignalKind.MissingCommandTarget, "./gone.sh")], FactsBuilder.Signals(facts).Select(s => (s.Kind, s.Text)));
        Assert.Contains(ReadmeSignals.IdentifierMentions(facts.Readme!.Text), m => m.Identifier == "IGoneService");
    }

    [Fact]
    public void CodeBlocksInOtherLanguagesAreSkipped()
    {
        var facts = FactsBuilder.Readme("""
            ```json
            { "path": "src/missing.json" }
            ```

            ```csharp
            var x = File.ReadAllText("src/missing.cs");
            ```
            """, ["src/a.cs"]);

        Assert.Empty(FactsBuilder.Signals(facts));
    }

    [Fact]
    public void AMissingPathUnderAKnownFolderIsACandidate()
    {
        var facts = FactsBuilder.Readme("Edit `src/Config/settings.json` first.", ["src/a.cs"]);

        var signal = Assert.Single(FactsBuilder.Signals(facts));

        Assert.Equal((SignalKind.MissingPath, "src/Config/settings.json"), (signal.Kind, signal.Target));
        Assert.False(signal.Definitive);
    }

    [Fact]
    public void BareFileNamesInProseAreNotCheckedButBareCommandsAre()
    {
        var facts = FactsBuilder.Readme("Set `appsettings.json`, keep `secrets.json` private, then run `./build.ps1`.", ["src/App/appsettings.json"]);

        Assert.Equal([(SignalKind.MissingCommandTarget, "build.ps1")], FactsBuilder.Signals(facts).Select(s => (s.Kind, s.Target)));
    }

    [Fact]
    public void FilesMissingFromAListOfAFoldersFilesAreCandidates()
    {
        var facts = FactsBuilder.Readme(
            "- [One](docs/features/01-one.md)\n- [Two](docs/features/02-two.md)\n- [Three](docs/features/03-three.md)\n",
            ["docs/features/01-one.md", "docs/features/02-two.md", "docs/features/03-three.md", "docs/features/04-four.md", "docs/features/index.md", "docs/features/00-template.md", "docs/other/x.md"]);

        var signal = Assert.Single(FactsBuilder.Signals(facts));

        Assert.Equal((SignalKind.UnlistedFile, "docs/features/04-four.md"), (signal.Kind, signal.Target));
        Assert.Equal("the README lists 3 of the 4 files in docs/features/, but not this one", signal.Detail);
    }

    [Fact]
    public void AFolderTheReadmeOnlySamplesIsNotAList()
    {
        var facts = FactsBuilder.Readme(
            "[a](docs/a.md) [b](docs/b.md) [c](docs/c.md)",
            ["docs/a.md", "docs/b.md", "docs/c.md", "docs/d.md", "docs/e.md", "docs/f.md", "docs/g.md"]);

        Assert.Empty(FactsBuilder.Signals(facts));
    }

    [Fact]
    public void ProjectsMissingFromAnEnumeratedGroupAreCandidates()
    {
        var facts = FactsBuilder.Readme(
            "src/Api, src/Worker and src/Domain; tests: Api.Tests, Worker.Tests",
            ["src/Api/Api.csproj", "src/Worker/Worker.csproj", "src/Domain/Domain.csproj",
             "tests/Api.Tests/Api.Tests.csproj", "tests/Worker.Tests/Worker.Tests.csproj", "tests/Replay/Replay.csproj",
             "samples/One/One.csproj", "samples/Two/Two.csproj", "samples/Three/Three.csproj", "samples/Three.Tests/Three.Tests.csproj"]);

        Assert.Equal(["tests/Replay/Replay.csproj"], FactsBuilder.Signals(facts).Where(s => s.Kind == SignalKind.UnmentionedProject).Select(s => s.Target));
    }

    [Fact]
    public void ANestedReadmeOnlyCoversProjectsUnderItsFolder()
    {
        var facts = FactsBuilder.Readme(
            "Covers Harness.",
            ["src/Harness/Harness.csproj", "src/App/App.csproj", "src/Other/Other.csproj"],
            readmePath: "src/Harness/README.md",
            addedProjects: ["src/App/App.csproj"]);

        Assert.DoesNotContain(FactsBuilder.Signals(facts), s => s.Kind == SignalKind.UnmentionedProject);
    }

    [Fact]
    public void IdentifiersInCodeExamplesAreLeftOutButTreesAndProseAreIn()
    {
        var mentions = ReadmeSignals.IdentifierMentions("""
            The ReviewJobHandler and IReviewEngine run it.

            ```
            src/Application/   # Ports (ITenantContext)
            ```

            ```csharp
            builder.AddConsoleExporter(new MyProviderBackend());
            ```
            """);

        Assert.Equal(["ReviewJobHandler", "IReviewEngine", "ITenantContext"], mentions.Select(m => m.Identifier));
    }

    [Fact]
    public void ShellPromptsAndTrailingCommentsAreIgnored()
    {
        var facts = FactsBuilder.Readme("```console\n$ ./build.sh   # writes out/report.json\n$ dotnet run --project src/Gone\n```", ["build.sh"]);

        Assert.Equal(["src/Gone"], FactsBuilder.Signals(facts).Select(s => s.Target));
    }

    [Fact]
    public void DotnetAndTfmMentionsAreComparedWithTheProjects()
    {
        var facts = FactsBuilder.Readme("Requires .NET 6 and targets `net6.0`.", frameworks: ["net10.0"]);

        Assert.Equal([".NET 6", "net6.0"], FactsBuilder.Signals(facts).Where(s => s.Kind == SignalKind.VersionMismatch).Select(s => s.Text).Order());
    }

    [Theory]
    [InlineData("Requires .NET 8 or later.")]
    [InlineData("Requires .NET 8+.")]
    [InlineData("Works on net8.0 and .NET 10.")]
    public void VersionsTheProjectsTargetOrAllowAreFine(string text)
    {
        Assert.Empty(FactsBuilder.Signals(FactsBuilder.Readme(text, frameworks: ["net8.0", "net10.0"])));
    }

    [Fact]
    public void WithoutTargetFrameworksVersionsAreNotChecked()
    {
        Assert.Empty(FactsBuilder.Signals(FactsBuilder.Readme("Requires .NET 6.")));
    }

    [Fact]
    public void AnSdkVersionIsComparedWithGlobalJson()
    {
        var facts = FactsBuilder.Readme("Install the .NET SDK 8.0.400 first.", sdk: "10.0.100");

        var signal = Assert.Single(FactsBuilder.Signals(facts));

        Assert.Equal((SignalKind.VersionMismatch, "global.json pins SDK 10.0.100"), (signal.Kind, signal.Detail));
    }

    [Fact]
    public void NewProjectsTheReadmeDoesntMentionAreCandidatesButTestsAreNot()
    {
        var facts = FactsBuilder.Readme(
            "The Payments API lives in src/Payments.",
            addedProjects: ["src/Billing/Billing.csproj", "tests/Billing.Tests/Billing.Tests.csproj", "src/Payments/Payments.csproj"]);

        var signal = Assert.Single(FactsBuilder.Signals(facts));

        Assert.Equal((SignalKind.UnmentionedProject, "src/Billing/Billing.csproj", 0), (signal.Kind, signal.Text, signal.Line));
    }

    [Fact]
    public void TheCapKeepsTheMostCertainSignalsFirst()
    {
        var facts = FactsBuilder.Readme(
            "`src/one.json` `src/two.json` [x](gone.md)",
            ["src/a.cs"],
            addedProjects: ["src/New/New.csproj"]);

        var scan = ReadmeSignals.Find(facts).Capped(2);

        Assert.Equal([SignalKind.BrokenLink, SignalKind.MissingPath], scan.Signals.Select(s => s.Kind));
        Assert.Equal(2, scan.Dropped);
    }

    [Fact]
    public void ASymlinkedReadmeHasNoSignals()
    {
        var facts = FactsBuilder.Readme("[x](gone.md)") with { Readme = new ReadmeFile("README.md", "../secret", IsSymlink: true) };

        Assert.Empty(ReadmeSignals.Find(facts).Signals);
    }
}
