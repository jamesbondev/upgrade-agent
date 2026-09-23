using UpgradeAgent.Guardrails;

namespace UpgradeAgent.Tests.Guardrails;

public class TrxParserTests
{
    [Fact]
    public void GroupsTheoryRowsUnderTheirMethod()
    {
        var inventory = TrxParser.ParseXml(Trx(
            ("1", "Tests.Calc", "Adds", "/src/bin/Debug/net10.0/Tests.dll", "Passed"),
            ("2", "Tests.Calc", "Adds", "/src/bin/Debug/net10.0/Tests.dll", "Passed"),
            ("3", "Tests.Calc", "Divides", "/src/bin/Debug/net10.0/Tests.dll", "Failed"),
            ("4", "Tests.Calc", "Later", "/src/bin/Debug/net10.0/Tests.dll", "NotExecuted")));

        Assert.Equal(new MethodStats(2, 0, 0), inventory.Methods["Tests [net10.0] Tests.Calc.Adds"]);
        Assert.Equal(new MethodStats(0, 1, 0), inventory.Methods["Tests [net10.0] Tests.Calc.Divides"]);
        Assert.Equal(new MethodStats(0, 0, 1), inventory.Methods["Tests [net10.0] Tests.Calc.Later"]);
        Assert.Equal((2, 1, 1), (inventory.Passed, inventory.Failed, inventory.Skipped));
    }

    [Fact]
    public void KeysByFrameworkAndHandlesWindowsPaths()
    {
        var inventory = TrxParser.ParseXml(Trx(
            ("1", "Tests.Calc", "Adds", @"C:\src\bin\Debug\net8.0\Tests.dll", "Passed"),
            ("2", "Tests.Calc", "Adds", @"C:\src\bin\Debug\net10.0\Tests.dll", "Passed")));

        Assert.Equal(["Tests [net10.0] Tests.Calc.Adds", "Tests [net8.0] Tests.Calc.Adds"], inventory.Methods.Keys.Order());
    }

    [Fact]
    public void StripsArgumentsWhenAdaptersPutThemInTheName()
    {
        var inventory = TrxParser.ParseXml(Trx(
            ("1", "Tests.Accounts", "Tests.Accounts.Lookup(id: \"ACC-1\")", "/b/net10.0/Tests.dll", "Passed"),
            ("2", "Tests.Accounts", "Tests.Accounts.Lookup(id: AccountId { Value = ACC-1 })", "/b/net10.0/Tests.dll", "Passed")));

        Assert.Equal(new MethodStats(2, 0, 0), Assert.Single(inventory.Methods).Value);
    }

    [Fact]
    public void CountsMsTestDataRowsFromInnerResults()
    {
        const string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" outcome="Failed">
                  <InnerResults>
                    <UnitTestResult testId="1" outcome="Passed" />
                    <UnitTestResult testId="1" outcome="Passed" />
                    <UnitTestResult testId="1" outcome="Failed" />
                  </InnerResults>
                </UnitTestResult>
              </Results>
              <TestDefinitions>
                <UnitTest id="1" name="Rows"><TestMethod codeBase="/b/net10.0/Tests.dll" className="Tests.Data" name="Rows" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        Assert.Equal(new MethodStats(2, 1, 0), TrxParser.ParseXml(xml).Methods["Tests [net10.0] Tests.Data.Rows"]);
    }

    private static string Trx(params (string Id, string ClassName, string Name, string CodeBase, string Outcome)[] tests) => $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            {string.Concat(tests.Select(t => $"<UnitTestResult testId=\"{t.Id}\" outcome=\"{t.Outcome}\" />"))}
          </Results>
          <TestDefinitions>
            {string.Concat(tests.Select(t => $"<UnitTest id=\"{t.Id}\" name=\"x\"><TestMethod codeBase=\"{t.CodeBase}\" className=\"{t.ClassName}\" name=\"{System.Security.SecurityElement.Escape(t.Name)}\" /></UnitTest>"))}
          </TestDefinitions>
        </TestRun>
        """;
}
