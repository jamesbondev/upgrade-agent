using UpgradeAgent.Bumping;

namespace UpgradeAgent.Tests.Bumping;

public class VersionEntryScannerTests
{
    [Fact]
    public void FindsCentralVersionsWithValueOffsets()
    {
        const string text = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Foo" Version="1.0.0" />
                <PackageVersion Include='Bar' Version='2.3.4' />
              </ItemGroup>
            </Project>
            """;

        var entries = VersionEntryScanner.Scan(text);

        Assert.Equal(["Foo", "Bar"], entries.Select(e => e.Id));
        Assert.All(entries, e => Assert.Equal(e.Version, text.Substring(e.ValueStart!.Value, e.ValueLength!.Value)));
    }

    [Fact]
    public void FindsChildVersionElementsAndMultilineTags()
    {
        const string text = """
            <ItemGroup>
              <PackageReference
                  Include="Foo"
                  PrivateAssets="all">
                <Version>1.2.3</Version>
              </PackageReference>
            </ItemGroup>
            """;

        var entry = Assert.Single(VersionEntryScanner.Scan(text));

        Assert.Equal(("Foo", "1.2.3"), (entry.Id, entry.Version));
        Assert.Equal("1.2.3", text.Substring(entry.ValueStart!.Value, entry.ValueLength!.Value));
    }

    [Fact]
    public void IgnoresEntriesInComments()
    {
        const string text = """
            <!-- <PackageVersion Include="Old" Version="0.1.0" /> -->
            <PackageVersion Include="Foo" Version="1.0.0" />
            """;

        Assert.Equal(["Foo"], VersionEntryScanner.Scan(text).Select(e => e.Id));
    }

    [Fact]
    public void ReportsVersionOverrideAndCentralReferencesWithoutVersion()
    {
        const string text = """
            <PackageReference Include="Foo" />
            <PackageReference Include="Bar" VersionOverride="3.0.0" />
            <PackageReference Update="Baz" Version="$(BazVersion)" />
            <GlobalPackageReference Include="Analyzers" Version="9.0.0" />
            """;

        var entries = VersionEntryScanner.Scan(text);

        Assert.Null(entries[0].Version);
        Assert.True(entries[1].HasVersionOverride);
        Assert.Null(entries[1].Version);
        Assert.Equal(("Baz", "$(BazVersion)"), (entries[2].Id, entries[2].Version));
        Assert.Equal(("GlobalPackageReference", "9.0.0"), (entries[3].Element, entries[3].Version));
    }
}
