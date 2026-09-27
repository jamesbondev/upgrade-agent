using TestHardener.Hardening;

namespace TestHardener.Tests.Hardening;

public class TestFileCheckerTests
{
    private const string Before = """
        using Demo;

        namespace Demo.Tests;

        public class CalculatorTests
        {
            private readonly Calculator _calculator = new(null!);

            [Fact]
            public void Add_Positive_ReturnsSum()
            {
                Assert.Equal(3, _calculator.Add(1, 2));
            }

            [Theory]
            [InlineData(1, 1)]
            [InlineData(2, 2)]
            public void Add_Zero_ReturnsSame(int value, int expected)
            {
                Assert.Equal(expected, _calculator.Add(value, 0));
            }

            private static int Helper() => 1;
        }
        """;

    [Fact]
    public void Check_AddedTest_IsANewTest()
    {
        var after = Before.Replace("    private static int Helper() => 1;", """
                private static int Helper() => 1;

                [Fact]
                public void Add_FirstBigger_ReturnsSum()
                {
                    Assert.Equal(5, _calculator.Add(3, 2));
                }
            """, StringComparison.Ordinal);

        var check = TestFileChecker.Check(Before, after, "^[A-Z][A-Za-z]*_[A-Za-z]+_[A-Za-z]+$");

        Assert.True(check.Passed, string.Join("; ", check.Problems));
        var test = Assert.Single(check.NewTests);
        Assert.Equal("Demo.Tests.CalculatorTests.Add_FirstBigger_ReturnsSum", test.FullyQualifiedName);
        Assert.False(test.IsNewRow);
    }

    [Fact]
    public void Check_AddedInlineDataRow_IsANewRow()
    {
        var after = Before.Replace("    [InlineData(2, 2)]", "    [InlineData(2, 2)]\n    [InlineData(-4, -4)]", StringComparison.Ordinal);

        var check = TestFileChecker.Check(Before, after, null);

        Assert.True(check.Passed, string.Join("; ", check.Problems));
        Assert.Equal([new NewTest("Demo.Tests.CalculatorTests.Add_Zero_ReturnsSame", "Add_Zero_ReturnsSame", true)], check.NewTests);
    }

    [Theory]
    [InlineData("    [InlineData(2, 2)]", "    [InlineData(2, 3)]", "Add_Zero_ReturnsSame")]
    [InlineData("private static int Helper() => 1;", "private static int Helper() => 2;", "Helper()")]
    [InlineData("Assert.Equal(3, _calculator.Add(1, 2));", "Assert.True(true);", "Add_Positive_ReturnsSum()")]
    [InlineData("private readonly Calculator _calculator = new(null!);", "private Calculator _calculator = new(null!);", "_calculator")]
    public void Check_ChangedExistingMember_IsRejected(string from, string to, string member)
    {
        var after = Before.Replace(from, to, StringComparison.Ordinal);

        var check = TestFileChecker.Check(Before, after, null);

        Assert.Contains(check.Problems, p => p.Contains(member, StringComparison.Ordinal) && p.Contains("Don't change", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_RemovedTest_IsRejected()
    {
        var after = Before.Replace("""
                [Fact]
                public void Add_Positive_ReturnsSum()
                {
                    Assert.Equal(3, _calculator.Add(1, 2));
                }
            """, "", StringComparison.Ordinal);

        Assert.Contains(TestFileChecker.Check(Before, after, null).Problems, p => p.Contains("Don't remove or rename", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_RemovedUsing_IsRejected() =>
        Assert.Contains(TestFileChecker.Check(Before, Before.Replace("using Demo;", "", StringComparison.Ordinal), null).Problems,
            p => p.Contains("using Demo;", StringComparison.Ordinal));

    [Fact]
    public void Check_WhitespaceOnlyChanges_AreFine()
    {
        var after = Before.Replace("    private static int Helper() => 1;", "    private static int Helper()   =>   1;\n\n    [Fact]\n    public void A_B_C() => Assert.True(Helper() == 1);", StringComparison.Ordinal);

        Assert.True(TestFileChecker.Check(Before, after, null).Passed);
    }

    [Theory]
    [InlineData("Assert.Equal(5, _calculator.Add(3, 2));", "_ = _calculator.Add(3, 2);", "has no assertion")]
    [InlineData("Assert.Equal(5, _calculator.Add(3, 2));", "// checks the sum\n        Assert.Equal(5, _calculator.Add(3, 2));", "Don't add comments")]
    [InlineData("[Fact]", "[Fact(Skip = \"later\")]", "is skipped")]
    [InlineData("Assert.Equal(5, _calculator.Add(3, 2));", "Assert.True(DateTime.UtcNow.Year > 2000);", "the real clock")]
    [InlineData("Assert.Equal(5, _calculator.Add(3, 2));", "Assert.NotNull(typeof(Calculator).GetMethod(\"Add\"));", "reflection")]
    [InlineData("Assert.Equal(5, _calculator.Add(3, 2));", "Assert.Null(Environment.GetEnvironmentVariable(\"X\"));", "environment variables")]
    [InlineData("Assert.Equal(5, _calculator.Add(3, 2));", "Thread.Sleep(10); Assert.True(true);", "sleeping")]
    public void Check_BadNewTest_IsRejected(string from, string to, string problem)
    {
        var added = """
                [Fact]
                public void Add_FirstBigger_ReturnsSum()
                {
                    Assert.Equal(5, _calculator.Add(3, 2));
                }
            """.Replace(from, to, StringComparison.Ordinal);
        var after = Before.Replace("    private static int Helper() => 1;", "    private static int Helper() => 1;\n\n" + added, StringComparison.Ordinal);

        Assert.Contains(TestFileChecker.Check(Before, after, null).Problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void Check_NothingAdded_IsRejected() =>
        Assert.Contains(TestFileChecker.Check(Before, Before, null).Problems, p => p.Contains("Add at least one", StringComparison.Ordinal));

    [Fact]
    public void Check_NameAgainstPattern()
    {
        var after = Before.Replace("    private static int Helper() => 1;", "    private static int Helper() => 1;\n\n    [Fact]\n    public void addsthings() => Assert.True(true);", StringComparison.Ordinal);

        Assert.Contains(TestFileChecker.Check(Before, after, "^[A-Z].*_.*_.*$").Problems, p => p.Contains("addsthings", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_NewFileFromSkeleton_AcceptsAddedTests()
    {
        const string skeleton = "using Demo;\n\nnamespace Demo.Tests.Sub;\n\npublic class CalculatorTests\n{\n}\n";
        const string after = "using Demo;\n\nnamespace Demo.Tests.Sub;\n\npublic class CalculatorTests\n{\n    [Fact]\n    public void Add_Two_Sums() => Assert.Equal(2, new Calculator(null!).Add(1, 1));\n}\n";

        var check = TestFileChecker.Check(skeleton, after, null);

        Assert.True(check.Passed, string.Join("; ", check.Problems));
        Assert.Equal("Demo.Tests.Sub.CalculatorTests.Add_Two_Sums", Assert.Single(check.NewTests).FullyQualifiedName);
    }

    [Fact]
    public void Check_CustomFactAttribute_CountsAsATest()
    {
        var after = Before.Replace("    private static int Helper() => 1;", "    private static int Helper() => 1;\n\n    [UnixOnlyFact]\n    public void A_B_C() => Assert.True(true);", StringComparison.Ordinal);

        Assert.Single(TestFileChecker.Check(Before, after, null).NewTests);
    }
}
