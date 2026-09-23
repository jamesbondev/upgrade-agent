using UpgradeAgent.Preflight;

namespace UpgradeAgent.Tests.Preflight;

public sealed class TargetPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ua-preflight-").FullName;

    [Fact]
    public void SdkStyleProjectsPass()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("src/Web/Web.csproj", """<Project><Sdk Name="Microsoft.NET.Sdk.Web" /></Project>""");

        var check = TargetPreflight.CheckProjectStyle(_root);

        Assert.True(check.Passed);
        Assert.Equal("2 projects", check.Detail);
    }

    [Fact]
    public void LegacyProjectsFail()
    {
        Write("src/Old/Old.csproj", """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003" />""");

        var check = TargetPreflight.CheckProjectStyle(_root);

        Assert.False(check.Passed);
        Assert.Contains("Old.csproj", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagesConfigFails()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("src/App/packages.config", "<packages />");

        var check = TargetPreflight.CheckProjectStyle(_root);

        Assert.False(check.Passed);
        Assert.Contains("packages.config", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildOutputIsIgnored()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("src/App/obj/App.csproj.nuget.g.props", "<Project />");
        Write("src/App/bin/Debug/Old.csproj", "<Project />");

        Assert.True(TargetPreflight.CheckProjectStyle(_root).Passed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

public sealed class UncommittedConfigTests : IDisposable
{
    private readonly UpgradeAgent.Tests.TestSupport.TempRepo _repo = new();

    [Fact]
    public async Task PassesWhenConfigFilesAreCommitted()
    {
        _repo.Write("nuget.config", "<configuration />").Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />").Commit();

        var check = await new TargetPreflight(new UpgradeAgent.Infrastructure.ProcessRunner()).CheckUncommittedConfigAsync(_repo.Path, CancellationToken.None);

        Assert.True(check.Passed, check.Detail);
    }

    [Fact]
    public async Task FailsForALocalUncommittedNuGetConfig()
    {
        _repo.Write(".gitignore", "nuget.config\n").Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />").Commit();
        _repo.Write("nuget.config", "<configuration />").Write("src/Directory.Build.props", "<Project />");

        var check = await new TargetPreflight(new UpgradeAgent.Infrastructure.ProcessRunner()).CheckUncommittedConfigAsync(_repo.Path, CancellationToken.None);

        Assert.False(check.Passed);
        Assert.StartsWith("nuget.config, src/Directory.Build.props exist(s) but aren't committed", check.Detail, StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}
