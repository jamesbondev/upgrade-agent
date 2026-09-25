using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Preflight;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Preflight;

public sealed class TargetPreflightTests : IAsyncLifetime
{
    private TempRepo _repo = null!;

    public async Task InitializeAsync() => _repo = await TempRepo.CreateAsync();

    [Fact]
    public void SdkStyleProjectsPass()
    {
        _repo.Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Web/Web.csproj", """<Project><Sdk Name="Microsoft.NET.Sdk.Web" /></Project>""");

        var check = TargetPreflight.CheckProjectStyle(_repo.Path);

        Assert.True(check.Passed);
        Assert.Equal("2 projects", check.Detail);
    }

    [Fact]
    public void LegacyProjectsFail()
    {
        _repo.Write("src/Old/Old.csproj", """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003" />""");

        var check = TargetPreflight.CheckProjectStyle(_repo.Path);

        Assert.False(check.Passed);
        Assert.Contains("Old.csproj", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagesConfigFails()
    {
        _repo.Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/App/packages.config", "<packages />");

        var check = TargetPreflight.CheckProjectStyle(_repo.Path);

        Assert.False(check.Passed);
        Assert.Contains("packages.config", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOutputIsIgnored()
    {
        _repo.Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/App/obj/App.csproj.nuget.g.props", "<Project />");
        _repo.Write("src/App/bin/Debug/Old.csproj", "<Project />");

        Assert.True(TargetPreflight.CheckProjectStyle(_repo.Path).Passed);
    }

    [Fact]
    public async Task CommittedBuildConfigPasses()
    {
        await _repo.Write("nuget.config", "<configuration />").Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />").CommitAsync();

        var check = await Preflight().CheckUncommittedConfigAsync(_repo.Path, CancellationToken.None);

        Assert.True(check.Passed, check.Detail);
    }

    [Fact]
    public async Task ALocalUncommittedNuGetConfigFails()
    {
        await _repo.Write(".gitignore", "nuget.config\n").Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />").CommitAsync();
        _repo.Write("nuget.config", "<configuration />").Write("src/Directory.Build.props", "<Project />");

        var check = await Preflight().CheckUncommittedConfigAsync(_repo.Path, CancellationToken.None);

        Assert.False(check.Passed);
        Assert.StartsWith("nuget.config, src/Directory.Build.props exist(s) but aren't committed", check.Detail, StringComparison.Ordinal);
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    private TargetPreflight Preflight() => new(_repo.Git, new DotnetCli(new ProcessRunner(), TimeProvider.System));
}
