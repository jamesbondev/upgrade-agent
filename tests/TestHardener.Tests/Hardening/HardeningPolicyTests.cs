using AgentHarness;
using AgentHarness.Policies;
using TestHardener.Hardening;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Hardening;

public class HardeningPolicyTests
{
    private static readonly string[] TestProjects = ["tests/Demo.Tests/Demo.Tests.csproj"];

    [Theory]
    [InlineData("build tests/Demo.Tests/Demo.Tests.csproj --no-restore")]
    [InlineData("test tests/Demo.Tests/Demo.Tests.csproj --no-build --filter FullyQualifiedName~Add")]
    [InlineData("test Demo.Tests.csproj --no-restore --filter=Name=A -v q")]
    [InlineData("test tests/Demo.Tests --no-build")]
    public void EvaluateDotnet_TestProjectCommands_AreApproved(string command) =>
        Assert.Equal(ToolVerdict.Approve, HardeningPolicy.EvaluateDotnet(command.Split(' '), TestProjects).Verdict);

    [Theory]
    [InlineData("test --no-build", "Name the test project")]
    [InlineData("test Demo.slnx --no-build", "Name the test project")]
    [InlineData("test tests/Other.Tests/Other.Tests.csproj --no-build", "Name the test project")]
    [InlineData("build tests/Demo.Tests/Demo.Tests.csproj", "--no-restore")]
    [InlineData("test tests/Demo.Tests/Demo.Tests.csproj", "--no-build")]
    [InlineData("test Demo.Tests.csproj --no-build --logger trx", "--logger")]
    [InlineData("test Demo.Tests.csproj --no-build --results-directory x", "--results-directory")]
    [InlineData("build Demo.Tests.csproj --no-restore -p:TreatWarningsAsErrors=false", "property overrides")]
    [InlineData("restore", "Only dotnet build and dotnet test")]
    [InlineData("run --project src/App", "Only dotnet build and dotnet test")]
    public void EvaluateDotnet_OtherCommands_AreRefused(string command, string feedback)
    {
        var decision = HardeningPolicy.EvaluateDotnet(command.Split(' '), TestProjects);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains(feedback, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_AllowsWritingOnlyTheOwnedFile()
    {
        using var repo = new TempDirectory();
        var policy = HardeningPolicy.Create(repo.Path, "tests/Demo.Tests/CalculatorTests.cs", TestProjects);

        var owned = await policy.EvaluateAsync(new FileWriteRequest(repo.Combine("tests/Demo.Tests/CalculatorTests.cs")), CancellationToken.None);
        var other = await policy.EvaluateAsync(new FileWriteRequest(repo.Combine("src/Demo/Calculator.cs")), CancellationToken.None);
        var helper = await policy.EvaluateAsync(new FileWriteRequest(repo.Combine("tests/Demo.Tests/Builders.cs")), CancellationToken.None);

        Assert.Equal(ToolVerdict.Approve, owned.Verdict);
        Assert.Equal(ToolVerdict.Reject, other.Verdict);
        Assert.Equal(ToolVerdict.Reject, helper.Verdict);
        Assert.Contains("say so in your summary", helper.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RunsDotnetThroughTheRule()
    {
        using var repo = new TempDirectory();
        var policy = HardeningPolicy.Create(repo.Path, "tests/Demo.Tests/CalculatorTests.cs", TestProjects);

        var bare = await policy.EvaluateAsync(new ShellRequest("dotnet test --no-build", false, []), CancellationToken.None);
        var scoped = await policy.EvaluateAsync(new ShellRequest("dotnet test tests/Demo.Tests/Demo.Tests.csproj --no-build", false, []), CancellationToken.None);
        var network = await policy.EvaluateAsync(new ShellRequest("curl https://example.com", false, []), CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, bare.Verdict);
        Assert.Equal(ToolVerdict.Approve, scoped.Verdict);
        Assert.Equal(ToolVerdict.Reject, network.Verdict);
    }
}
