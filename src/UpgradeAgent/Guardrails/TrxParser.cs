using System.Xml.Linq;

namespace UpgradeAgent.Guardrails;

public sealed record MethodStats(int Passed, int Failed, int Skipped);

/// <summary>
/// Test results keyed by <c>assembly [framework] Class.Method</c>. Theory rows are counted under
/// their method: display names embed arguments, which legitimately change when a parameter type does.
/// </summary>
public sealed record TestInventory(IReadOnlyDictionary<string, MethodStats> Methods)
{
    public int Passed => Methods.Values.Sum(m => m.Passed);

    public int Failed => Methods.Values.Sum(m => m.Failed);

    public int Skipped => Methods.Values.Sum(m => m.Skipped);

    public int Total => Passed + Failed + Skipped;
}

public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestInventory Parse(IEnumerable<string> trxFiles)
    {
        var methods = new Dictionary<string, (int Passed, int Failed, int Skipped)>(StringComparer.Ordinal);
        foreach (var file in trxFiles)
        {
            Accumulate(XDocument.Load(file), methods);
        }

        return new TestInventory(methods.ToDictionary(kv => kv.Key, kv => new MethodStats(kv.Value.Passed, kv.Value.Failed, kv.Value.Skipped), StringComparer.Ordinal));
    }

    public static TestInventory ParseXml(string xml)
    {
        var methods = new Dictionary<string, (int Passed, int Failed, int Skipped)>(StringComparer.Ordinal);
        Accumulate(XDocument.Parse(xml), methods);
        return new TestInventory(methods.ToDictionary(kv => kv.Key, kv => new MethodStats(kv.Value.Passed, kv.Value.Failed, kv.Value.Skipped), StringComparer.Ordinal));
    }

    private static void Accumulate(XDocument document, Dictionary<string, (int Passed, int Failed, int Skipped)> methods)
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

            // MSTest data rows arrive as one parent result with InnerResults; count the rows.
            var inner = result.Element(Ns + "InnerResults")?.Elements(Ns + "UnitTestResult").ToList();
            var outcomes = inner is { Count: > 0 }
                ? inner.Select(r => (string?)r.Attribute("outcome"))
                : [(string?)result.Attribute("outcome")];

            foreach (var outcome in outcomes)
            {
                methods.TryGetValue(key, out var stats);
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

        // codeBase is .../bin/<Configuration>/<tfm>/<Assembly>.dll, with either separator.
        var codeBase = ((string?)method?.Attribute("codeBase") ?? (string?)unitTest.Attribute("storage") ?? "").Replace('\\', '/');
        var segments = codeBase.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var assembly = segments.Length > 0 ? Path.GetFileNameWithoutExtension(segments[^1]) : "";
        var framework = segments.Length > 1 ? segments[^2] : "";

        // Some adapters put the fully qualified name in 'name'; don't repeat the class.
        var qualified = name.StartsWith(className + ".", StringComparison.Ordinal) || className.Length == 0 ? name : $"{className}.{name}";
        var parenthesis = qualified.IndexOf('(', StringComparison.Ordinal);
        if (parenthesis > 0)
        {
            qualified = qualified[..parenthesis];
        }

        return $"{assembly} [{framework}] {qualified}";
    }
}
