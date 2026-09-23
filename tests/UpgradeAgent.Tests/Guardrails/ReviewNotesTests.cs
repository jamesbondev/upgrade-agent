using UpgradeAgent.Guardrails;

namespace UpgradeAgent.Tests.Guardrails;

public class ReviewNotesTests
{
    [Fact]
    public void FlagsRemovedPublicSignaturesInProductionCode()
    {
        FileDiff[] diffs =
        [
            new("src/App/InterestCalculator.cs",
                ["    public async Task<decimal> DailyInterestAsync(decimal principal, CancellationToken cancellationToken = default)"],
                ["    public decimal DailyInterest(decimal principal)"], false, false),
            new("src/App/Statement.cs", ["    private int X() => 1;"], ["    private int X() => 2;"], false, false),
            new("tests/App.Tests/CalcTests.cs", ["    public async Task Adds()"], ["    public void Adds()"], false, false),
        ];

        var notes = GuardrailRunner.PublicApiChanges(diffs, ["tests/App.Tests/CalcTests.cs"]).ToList();

        Assert.Equal(["public API changed in src/App/InterestCalculator.cs: `public decimal DailyInterest(decimal principal)`"], notes);
    }

    [Theory]
    [InlineData("public sealed class StatementService(AccountDirectory directory)")]
    [InlineData("public record Statement(string Id);")]
    [InlineData("    public static string Format(decimal value) => value.ToString();")]
    public void RecognisesTypeAndMemberSignatures(string line)
    {
        Assert.Single(GuardrailRunner.PublicApiChanges([new FileDiff("src/A.cs", [], [line], false, false)], []));
    }

    [Theory]
    [InlineData("public int Count { get; set; }")]
    [InlineData("    public const int Limit = 3;")]
    public void IgnoresPropertiesAndConstants(string line)
    {
        Assert.Empty(GuardrailRunner.PublicApiChanges([new FileDiff("src/A.cs", [], [line], false, false)], []));
    }

    [Fact]
    public void IgnoresReindentedSignatures()
    {
        var diff = new FileDiff("src/A.cs", ["        public void Run()"], ["    public void Run()"], false, false);

        Assert.Empty(GuardrailRunner.PublicApiChanges([diff], []));
    }

    [Fact]
    public void ReportsDisagreementBetweenTheSummaryAndTheDiff()
    {
        FileDiff[] diffs =
        [
            new("Directory.Packages.props", ["x"], ["y"], false, false),
            new("src/A.cs", ["x"], ["y"], false, false),
            new("src/B.cs", ["x"], ["y"], false, false),
            new(".editorconfig", ["x"], ["y"], false, false),
        ];
        var review = new ReviewContext(["./src/A.cs", "src/C.cs"], ["Directory.Packages.props"]);

        var notes = GuardrailRunner.ClaimMismatches(diffs, review).ToList();

        Assert.Equal(
        [
            "agent summary lists a fix in src/C.cs, but the file is unchanged",
            "changed but not in the agent's summary: .editorconfig",
            "changed but not in the agent's summary: src/B.cs",
        ], notes);
    }

    [Fact]
    public void NoSummaryMeansNoClaimNotes()
    {
        Assert.Empty(GuardrailRunner.ClaimMismatches([new FileDiff("src/A.cs", ["x"], [], false, false)], new ReviewContext(null, [])));
    }
}
