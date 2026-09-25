using ReadmeChecker.Detection;
using ReadmeChecker.Fixing;
using ReadmeChecker.Tests.TestSupport;
using RepoKit;

namespace ReadmeChecker.Tests.Fixing;

public sealed class ReadmeVerifierTests : IAsyncLifetime, IDisposable
{
    private const string Original = """
        # Demo

        See the [setup guide](docs/setup.md).

        Build with `dotnet build src/App/App.csproj`.

        Docs live at https://learn.microsoft.com/dotnet.

        ## Contributing

        Open a pull request.
        """;

    private readonly TempDirectory _work = new();
    private TempRepo _source = null!;
    private RepoWorkspace _workspace = null!;
    private RepoFacts _facts = null!;

    public async Task InitializeAsync()
    {
        _source = await TempRepo.CreateAsync();
        await _source
            .Write("README.md", Original)
            .Write("docs/guide.md", "guide")
            .Write("src/App/App.csproj", "<Project />")
            .Write("src/App/Links.cs", "class Links { const string Wiki = \"https://dev.azure.com/contoso/wiki\"; }")
            .CommitAsync();
        _workspace = await RepoWorkspace.CloneAsync(RepoSource.Local("demo", _source.Path), _work.Path, _source.Git);
        _facts = await RepoFacts.CollectAsync(_workspace.Path, _workspace.Git, null, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _workspace.DisposeAsync();
        _source.Dispose();
    }

    public void Dispose() => _work.Dispose();

    [Fact]
    public async Task AFixThatRepairsTheLinkPasses()
    {
        await WriteReadme(Original.Replace("docs/setup.md", "docs/guide.md", StringComparison.Ordinal));

        var verification = await Verify();

        Assert.Empty(verification.Problems);
        Assert.Contains("+See the [setup guide](docs/guide.md).", verification.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoChangeFails()
    {
        Assert.Equal(["the agent didn't change the README"], (await Verify()).Problems);
    }

    [Fact]
    public async Task ChangingAnotherFileFails()
    {
        await WriteReadme(Original.Replace("docs/setup.md", "docs/guide.md", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "docs/guide.md"), "changed");

        Assert.Contains("other files changed: docs/guide.md", (await Verify()).Problems);
    }

    [Fact]
    public async Task GuttingTheReadmeFails()
    {
        await WriteReadme("# Demo\n\nSee the [guide](docs/guide.md).\n");

        Assert.Contains((await Verify()).Problems, p => p.StartsWith("the README kept only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InventingAPathFails()
    {
        await WriteReadme(Original.Replace("docs/setup.md", "docs/guide.md", StringComparison.Ordinal) + "\nRun `dotnet test src/App.Tests/App.Tests.csproj`.\n");

        Assert.Contains((await Verify()).Problems, p => p.Contains("src/App.Tests/App.Tests.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavingACertainBrokenLinkFails()
    {
        await WriteReadme(Original + "\nMore text.\n");

        Assert.Contains("broken links are still there: docs/setup.md", (await Verify()).Problems);
    }

    [Fact]
    public async Task LinkingToANewSiteFailsButSitesTheRepoAlreadyUsesAreFine()
    {
        var fixedLink = Original.Replace("docs/setup.md", "docs/guide.md", StringComparison.Ordinal);
        await WriteReadme(fixedLink + "\nSee the [wiki](https://dev.azure.com/contoso/wiki) and [this](https://evil.example.com/x).\n");

        var problem = Assert.Single((await Verify()).Problems);

        Assert.Equal("it links to sites that aren't mentioned anywhere in the repository: evil.example.com", problem);
    }

    [Theory]
    [InlineData("a\nb\nc\nd", "a\nb\nc\nd", 1.0)]
    [InlineData("a\nb\nc\nd", "a\nb\nx", 0.5)]
    [InlineData("a\n\n\nb", "a\r\nb", 1.0)]
    [InlineData("a\na\nb", "a\nb", 2.0 / 3)]
    public void KeptRatioCountsOriginalLinesThatSurvive(string before, string after, double expected)
    {
        Assert.Equal(expected, ReadmeVerifier.KeptRatio(before, after), 3);
    }

    private Task WriteReadme(string text) => File.WriteAllTextAsync(Path.Combine(_workspace.Path, "README.md"), text);

    private Task<Verification> Verify() => ReadmeVerifier.VerifyAsync(_workspace, _facts, [], 0.5, CancellationToken.None);
}
