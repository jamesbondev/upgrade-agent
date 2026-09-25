using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;
using RepoKit;

namespace ReadmeChecker.Tests.Detection;

public class RepoFactsTests
{
    [Fact]
    public async Task TargetFrameworksComeFromProjectsAndDirectoryBuildFiles()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo
            .Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
            .Write("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")
            .Write("src/B/B.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0; netstandard2.0</TargetFrameworks></PropertyGroup></Project>")
            .Write("src/C/C.csproj", "<Project><PropertyGroup><TargetFramework>$(DefaultTfm)</TargetFramework></PropertyGroup></Project>")
            .Write("src/D/D.csproj", "<Project><not xml")
            .Write("README.md", "# Demo")
            .CommitAsync();

        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        Assert.Equal(["net10.0", "net8.0", "netstandard2.0"], facts.TargetFrameworks.Order(StringComparer.Ordinal));
        Assert.Equal(["src/A/A.csproj", "src/B/B.csproj", "src/C/C.csproj", "src/D/D.csproj"], facts.Projects);
    }

    [Fact]
    public async Task TheSdkComesFromGlobalJsonEvenWithComments()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("global.json", "{ // pinned\n \"sdk\": { \"version\": \"10.0.100\", }, }").Write("README.md", "# Demo").CommitAsync();

        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        Assert.Equal("10.0.100", facts.SdkVersion);
    }

    [Fact]
    public async Task TheReadmeIsFoundWhateverItsCase()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("readme.md", "# Demo").CommitAsync();

        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        Assert.Equal(("readme.md", "# Demo", false), (facts.Readme?.Path, facts.Readme?.Text, facts.Readme?.IsSymlink));
        Assert.NotNull(facts.Age);
    }

    [Fact]
    public async Task AConfiguredReadmePathIsUsed()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("README.md", "root").Write("docs/README.md", "docs").CommitAsync();

        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, "docs/readme.md", CancellationToken.None);

        Assert.Equal(("docs/README.md", "docs"), (facts.Readme?.Path, facts.Readme?.Text));
        Assert.Equal("docs", facts.Readme?.Directory);
    }

    [Fact]
    public async Task NoReadmeMeansNoReadmeFacts()
    {
        using var repo = await TempRepo.CreateAsync();
        await repo.Write("src/a.cs", "class A;").CommitAsync();

        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        Assert.Null(facts.Readme);
        Assert.Null(facts.Age);
    }

    [Fact]
    public async Task TheAgeCountsWorkSinceTheReadmeLastChanged()
    {
        using var repo = await TempRepo.CreateAsync();
        var readmeCommit = await repo.Write("README.md", "# Demo").Write("src/Old/Old.csproj", "<Project />").CommitAsync();
        await repo.Write("src/New/New.csproj", "<Project />").Write("src/New/a.cs", "class A;").CommitAsync();
        await repo.Write("docs/guide.md", "guide").CommitAsync();

        var facts = await RepoFacts.CollectAsync(repo.Path, repo.Git, null, CancellationToken.None);

        Assert.Equal(readmeCommit, facts.Age?.LastChange.Sha);
        Assert.Equal(2, facts.Age?.CommitsSince);
        Assert.Equal(["src/New/New.csproj"], facts.Age?.AddedProjects);
        Assert.Equal([new FolderChurn("src", 2), new FolderChurn("docs", 1)], facts.Age?.BusiestFolders);
    }

    [Fact]
    public async Task ASymlinkedReadmeIsFlaggedAndHasNoAge()
    {
        using var source = await TempRepo.CreateAsync();
        await source.Write("docs/guide.md", "guide").CommitAsync();
        await source.CommitSymlinkAsync("README.md", "docs/guide.md");
        using var workRoot = new TempDirectory();
        await using var clone = await RepoWorkspace.CloneAsync(RepoSource.Local("demo", source.Path), workRoot.Path, source.Git);

        var facts = await RepoFacts.CollectAsync(clone.Path, clone.Git, null, CancellationToken.None);

        Assert.True(facts.Readme?.IsSymlink);
        Assert.Null(facts.Age);
    }

    [Fact]
    public void ExistenceIsCaseSensitiveLikeTheRepository()
    {
        var facts = FactsBuilder.Readme("x", ["Docs/Guide.md"]);

        Assert.True(facts.IsFile("Docs/Guide.md"));
        Assert.False(facts.IsFile("docs/guide.md"));
        Assert.True(facts.IsFolder("Docs"));
    }
}
