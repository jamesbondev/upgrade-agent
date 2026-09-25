using System.Xml.Linq;

namespace UpgradeAgent.Guardrails;

internal sealed record MethodStats(int Passed, int Failed, int Skipped);

internal sealed record TestInventory(IReadOnlyDictionary<string, MethodStats> Methods)
{
    public int Passed => Methods.Values.Sum(m => m.Passed);

    public int Failed => Methods.Values.Sum(m => m.Failed);

    public int Skipped => Methods.Values.Sum(m => m.Skipped);

    public int Total => Passed + Failed + Skipped;
}

internal static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestInventory Parse(IEnumerable<string> trxFiles) => FromDocuments(trxFiles.Select(XDocument.Load));

    public static TestInventory ParseXml(string xml) => FromDocuments([XDocument.Parse(xml)]);

    private static TestInventory FromDocuments(IEnumerable<XDocument> documents)
    {
        var methods = new Dictionary<string, MethodStats>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            Accumulate(document, methods);
        }

        return new TestInventory(methods);
    }

    private static void Accumulate(XDocument document, Dictionary<string, MethodStats> methods)
    {
        var keys = document.Descendants(Ns + "UnitTest").ToDictionary(
            t => (string)t.Attribute("id")!,
            t => KeyFor(t),
            StringComparer.OrdinalIgnoreCase);

        foreach (var result in document.Descendants(Ns + "Results").Elements(Ns + "UnitTestResult"))
        {
            if (!keys.TryGetValue((string?)result.Attribute("testId") ?? "", out var key))
            {
                continue;
            }

            var inner = result.Element(Ns + "InnerResults")?.Elements(Ns + "UnitTestResult").ToList();
            var outcomes = inner is { Count: > 0 }
                ? inner.Select(r => (string?)r.Attribute("outcome"))
                : [(string?)result.Attribute("outcome")];

            foreach (var outcome in outcomes)
            {
                var stats = methods.GetValueOrDefault(key) ?? new MethodStats(0, 0, 0);
                methods[key] = outcome switch
                {
                    "Passed" => stats with { Passed = stats.Passed + 1 },
                    "NotExecuted" or "Inconclusive" => stats with { Skipped = stats.Skipped + 1 },
                    _ => stats with { Failed = stats.Failed + 1 },
                };
            }
        }
    }

    private static string KeyFor(XElement unitTest)
    {
        var method = unitTest.Element(Ns + "TestMethod");
        var className = (string?)method?.Attribute("className") ?? "";
        var name = (string?)method?.Attribute("name") ?? (string?)unitTest.Attribute("name") ?? "";

        var codeBase = ((string?)method?.Attribute("codeBase") ?? (string?)unitTest.Attribute("storage") ?? "").Replace('\\', '/');
        var segments = codeBase.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var assembly = segments.Length > 0 ? Path.GetFileNameWithoutExtension(segments[^1]) : "";
        var framework = segments.Length > 1 ? segments[^2] : "";

        var qualified = name.StartsWith(className + ".", StringComparison.Ordinal) || className.Length == 0 ? name : $"{className}.{name}";
        var parenthesis = qualified.IndexOf('(', StringComparison.Ordinal);
        if (parenthesis > 0)
        {
            qualified = qualified[..parenthesis];
        }

        return $"{assembly} [{framework}] {qualified}";
    }
}
