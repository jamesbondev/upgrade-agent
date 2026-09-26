using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Detection;

public class CommandScannerTests
{
    private static readonly string[] Repo = ["src/App/App.csproj", "src/App/Program.cs", "scripts/build.ps1", "exists.sh"];

    [Theory]
    [InlineData("dotnet run --project src/Old", "1 MissingCommandTarget src/Old -> src/Old")]
    [InlineData("dotnet run -p src/App", "")]
    [InlineData("dotnet test Missing.Tests", "1 MissingCommandTarget Missing.Tests -> Missing.Tests")]
    [InlineData("dotnet build src/App/App.csproj --no-restore", "")]
    [InlineData("dotnet run --project src/Old --project src/Gone", "1 MissingCommandTarget src/Old -> src/Old|1 MissingCommandTarget src/Gone -> src/Gone")]
    [InlineData("dotnet run -c Release src/Old", "1 MissingPath src/Old -> src/Old")]
    [InlineData("dotnet run --project src/App -- scripts/x.json", "1 MissingPath scripts/x.json -> scripts/x.json")]
    [InlineData("dotnet ./tools/app.dll", "")]
    [InlineData("dotnet tool run tools/x.json", "1 MissingPath tools/x.json -> tools/x.json")]
    [InlineData("dotnet", "")]
    public void DotnetProjectArgumentsAreCommandTargets(string line, string expected) =>
        Assert.Equal(Expected(expected), Scan(line));

    [Theory]
    [InlineData("./build.sh", "1 MissingCommandTarget ./build.sh -> build.sh")]
    [InlineData(".\\build.ps1 -Verbose", "1 MissingCommandTarget .\\build.ps1 -> build.ps1")]
    [InlineData("./exists.sh", "")]
    [InlineData("pwsh ./missing.ps1", "1 MissingCommandTarget ./missing.ps1 -> missing.ps1")]
    [InlineData("pwsh -File scripts/gone.ps1 -x a/b.json", "1 MissingCommandTarget scripts/gone.ps1 -> scripts/gone.ps1|1 MissingPath a/b.json -> a/b.json")]
    [InlineData("powershell -NoProfile scripts/build.ps1", "")]
    [InlineData("bash tools/nope.sh arg", "1 MissingCommandTarget tools/nope.sh -> tools/nope.sh")]
    [InlineData("sh ./run.sh ./run.sh", "1 MissingCommandTarget ./run.sh -> run.sh")]
    [InlineData("bash exists.sh src/Missing/File.cs", "1 MissingPath src/Missing/File.cs -> src/Missing/File.cs")]
    [InlineData("pwsh -c Get-Thing", "")]
    public void ScriptsAreCommandTargets(string line, string expected) =>
        Assert.Equal(Expected(expected), Scan(line));

    [Theory]
    [InlineData("cat src/Missing", "1 MissingPath src/Missing -> src/Missing")]
    [InlineData("cat foo/bar", "")]
    [InlineData("cat foo/bar.json", "1 MissingPath foo/bar.json -> foo/bar.json")]
    [InlineData("cat settings.json", "")]
    [InlineData("cat bin/Debug/app.json src/obj/x.json", "")]
    [InlineData("curl https://example.com/a/b.json ~/x/y.json $HOME/z.json", "")]
    [InlineData("cat src/App/Program.cs", "")]
    public void OtherTokensAreCheckedWhenTheyLookLikeFiles(string line, string expected) =>
        Assert.Equal(Expected(expected), Scan(line));

    [Theory]
    [InlineData("$ ./gone.sh # runs src/x.json", "1 MissingCommandTarget ./gone.sh -> gone.sh")]
    [InlineData("PS C:\\repo> ./gone.ps1", "1 MissingCommandTarget ./gone.ps1 -> gone.ps1")]
    [InlineData("# ./gone.sh", "")]
    [InlineData("// ./gone.sh", "")]
    [InlineData("REM ./gone.cmd", "")]
    [InlineData("./exists.sh && pwsh gone/x.ps1 | tee out/log.txt", "1 MissingCommandTarget gone/x.ps1 -> gone/x.ps1")]
    [InlineData("cat 'src/Missing Folder/a.json'", "1 MissingPath src/Missing Folder/a.json -> src/Missing Folder/a.json")]
    public void PromptsCommentsAndChainsAreHandled(string line, string expected) =>
        Assert.Equal(Expected(expected), Scan(line));

    [Theory]
    [InlineData("cd src\ndotnet run --project App", "")]
    [InlineData("cd src\ndotnet run --project Gone", "2 MissingCommandTarget Gone -> src/Gone")]
    [InlineData("pushd src/App && cat Program.cs", "")]
    [InlineData("cd src/Nope\n./build.sh", "1 MissingCommandTarget src/Nope -> src/Nope")]
    [InlineData("cd ~/code\n./build.sh", "")]
    [InlineData("cd ..\n./build.sh", "")]
    [InlineData("cd", "")]
    public void CdMovesTheWorkingFolderOrStopsTheScan(string lines, string expected) =>
        Assert.Equal(Expected(expected), Scan(lines));

    [Theory]
    [InlineData("mkdir -p out2/logs\ncat out2/logs/app.json", "")]
    [InlineData("mkdir docs/new.md src/Other/x.json", "")]
    [InlineData("dotnet new console -o src/NewApp\ncd src/NewApp\n./build.sh", "")]
    [InlineData("dotnet new console -n Created src/Other/x.json\ndotnet run --project Created", "")]
    [InlineData("New-Item -ItemType Directory tools/gen\ncat tools/gen/a.json", "")]
    public void FoldersTheBlockCreatesAreNotMissing(string lines, string expected) =>
        Assert.Equal(Expected(expected), Scan(lines));

    [Fact]
    public void AGitCloneStopsTheScan() =>
        Assert.Empty(Scan("git clone https://example.com/other.git\ncd other\n./build.sh"));

    [Fact]
    public void AScannerStopsForTheRestOfItsBlockOnceLost()
    {
        var facts = FactsBuilder.Readme("", Repo);
        var scanner = new CommandScanner(facts.Readme!, facts);

        Assert.Empty(scanner.Scan("cd $TEMP && ./gone.sh", 1));
        Assert.Empty(scanner.Scan("./gone.sh", 2));
    }

    [Fact]
    public void DetailsSayWhatIsMissing() =>
        Assert.Equal(
            ["'src/Old' is not in the repository", "'a/b.json' is not in the repository", "cd into a folder that isn't in the repository"],
            Signals("dotnet run --project src/Old a/b.json\ncd src/Nope").Select(s => s.Detail));

    private static string[] Scan(string lines) =>
        Signals(lines).Select(s => $"{s.Line} {s.Kind} {s.Text} -> {s.Target}").ToArray();

    private static List<Signal> Signals(string lines)
    {
        var facts = FactsBuilder.Readme("", Repo);
        var scanner = new CommandScanner(facts.Readme!, facts);
        return lines.Split('\n').SelectMany((line, i) => scanner.Scan(line, i + 1)).ToList();
    }

    private static string[] Expected(string joined) => joined.Length == 0 ? [] : joined.Split('|');
}
