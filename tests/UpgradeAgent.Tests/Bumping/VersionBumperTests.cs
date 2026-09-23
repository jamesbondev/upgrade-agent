using System.Text;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Tests.Bumping;

public sealed class VersionBumperTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ua-bump-").FullName;

    [Fact]
    public void EditsCentralVersionChangingNothingElse()
    {
        const string props = """
            <Project>
              <ItemGroup>
                <!-- keep this comment -->
                <PackageVersion Include="Foo"   Version="1.0.0" />
                <PackageVersion Include="Bar" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """;
        Write("Directory.Packages.props", props);
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root, [Update("Foo", "1.0.0", "1.1.0")]);

        Assert.Equal(props.Replace("\"1.0.0\"", "\"1.1.0\"", StringComparison.Ordinal), Read("Directory.Packages.props"));
        Assert.Equal([new VersionEdit("Directory.Packages.props", "Foo", "1.0.0", "1.1.0")], result.Edits);
        Assert.Empty(result.Manual);
    }

    [Fact]
    public void EditsPerProjectVersions()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root, [Update("Foo", "1.0.0", "1.0.5")]);

        Assert.Contains("Version=\"1.0.5\"", Read("src/App/App.csproj"), StringComparison.Ordinal);
        Assert.Equal("src/App/App.csproj", Assert.Single(result.Edits).File);
    }

    [Fact]
    public void UsesTheNearestDirectoryPackagesProps()
    {
        Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        VersionBumper.Apply(_root, [Update("Foo", "1.0.0", "1.1.0")]);

        Assert.Contains("1.1.0", Read("src/Directory.Packages.props"), StringComparison.Ordinal);
        Assert.Contains("1.0.0", Read("Directory.Packages.props"), StringComparison.Ordinal);
    }

    [Fact]
    public void BumpsFromAnOlderVersionWhenAnEarlierStepWasRejected()
    {
        // Plan said 1.1.0 -> 2.0.0, but the 1.0.0 -> 1.1.0 step was reverted.
        Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var result = VersionBumper.Apply(_root, [Update("Foo", "1.1.0", "2.0.0")]);

        Assert.Equal(new VersionEdit("Directory.Packages.props", "Foo", "1.0.0", "2.0.0"), Assert.Single(result.Edits));
    }

    [Fact]
    public void PropertyVersionsAndVersionOverridesAreManual()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" Version="$(FooVersion)" /><PackageReference Include="Bar" VersionOverride="1.0.0" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root, [Update("Foo", "1.0.0", "2.0.0"), Update("Bar", "1.0.0", "2.0.0")]);

        Assert.Empty(result.Edits);
        Assert.Contains("not a plain version", result.Manual.Single(m => m.Update.Id == "Foo").Reason, StringComparison.Ordinal);
        Assert.Contains("VersionOverride", result.Manual.Single(m => m.Update.Id == "Bar").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEntryIsManual()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var result = VersionBumper.Apply(_root, [Update("Foo", "1.0.0", "2.0.0")]);

        Assert.Contains("no version entry found", Assert.Single(result.Manual).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesByteOrderMark()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""", new UTF8Encoding(true));
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        VersionBumper.Apply(_root, [Update("Foo", "1.0.0", "1.1.0")]);

        var bytes = File.ReadAllBytes(Path.Combine(_root, "Directory.Packages.props"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PlannedUpdate Update(string id, string from, string to) =>
        new(id, from, to, BumpKind.Minor, [new ProjectTarget("src/App/App.csproj", "net10.0")], UpdateDecision.Planned, null, "patch-minor");

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(_root, relativePath));
}
