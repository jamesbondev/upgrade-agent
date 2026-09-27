namespace TestHardener.Tests.TestSupport;

internal static class Samples
{
    public const string CalculatorPath = "src/Demo/Calculator.cs";

    public const string Calculator = """
        using Microsoft.Extensions.Logging;

        namespace Demo;

        public partial class Calculator(ILogger logger)
        {
            private static readonly string[] Words = ["alpha", "beta"];

            public int Add(int a, int b)
            {
                if (a > b)
                {
                    logger.LogInformation("bigger {A}", a);
                }

                int Twice(int n) => n * 2;
                return Twice(a) + b;
            }

            public string Name => "calc";

            public int Limit
            {
                get { return 10 - 1; }
                set { _ = value; }
            }

            public Calculator(int seed)
                : this(null!)
            {
                _ = seed > 0;
            }

            public void LogResult(int value)
            {
                var next = value + 1;
                _ = next;
            }

            [LoggerMessage(Level = LogLevel.Debug, Message = "done")]
            private partial void Done();

            public class Inner
            {
                public bool Check(string s) => s.Length > 3;
            }
        }
        """;
}
