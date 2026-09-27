using System.Text.Json;
using System.Text.Json.Nodes;
using TestHardener.Stryker;

namespace TestHardener.Tests.TestSupport;

internal sealed class ReportBuilder(string repoRoot)
{
    private readonly Dictionary<string, JsonArray> _files = new(StringComparer.Ordinal);
    private readonly JsonArray _tests = [];
    private int _nextId = 1;

    public ReportBuilder Test(string id, string name)
    {
        _tests.Add(new JsonObject { ["id"] = id, ["name"] = name });
        return this;
    }

    public ReportBuilder Mutant(
        string file,
        string source,
        string snippet,
        MutantStatus status,
        string mutator = "Equality mutation",
        string replacement = "x",
        string[]? coveredBy = null,
        bool isStatic = false,
        int occurrence = 0,
        string[]? killedBy = null)
    {
        var location = SourceLocation.Of(source, snippet, occurrence);
        var mutant = new JsonObject
        {
            ["id"] = (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["mutatorName"] = mutator,
            ["replacement"] = replacement,
            ["location"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = location.Start.Line, ["column"] = location.Start.Column },
                ["end"] = new JsonObject { ["line"] = location.End.Line, ["column"] = location.End.Column },
            },
            ["status"] = status.ToString(),
            ["static"] = isStatic,
            ["coveredBy"] = new JsonArray([.. (coveredBy ?? ["t1"]).Select(t => (JsonNode)JsonValue.Create(t))]),
            ["killedBy"] = new JsonArray([.. (killedBy ?? []).Select(t => (JsonNode)JsonValue.Create(t))]),
        };
        var key = $"{repoRoot.TrimEnd('/')}/{file}";
        if (!_files.TryGetValue(key, out var mutants))
        {
            mutants = [];
            _files[key] = mutants;
        }

        mutants.Add(mutant);
        return this;
    }

    public string Json()
    {
        var files = new JsonObject();
        foreach (var (path, mutants) in _files)
        {
            files[path] = new JsonObject { ["language"] = "cs", ["source"] = "", ["mutants"] = mutants.DeepClone() };
        }

        var report = new JsonObject
        {
            ["schemaVersion"] = "2",
            ["thresholds"] = new JsonObject { ["high"] = 80, ["low"] = 60 },
            ["files"] = files,
            ["testFiles"] = new JsonObject { ["tests.cs"] = new JsonObject { ["tests"] = _tests.DeepClone() } },
        };
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public MutationReport Build() => MutationReportParser.Parse(Json(), repoRoot);
}

internal static class SourceLocation
{
    public static Location Of(string source, string snippet, int occurrence = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            index = source.IndexOf(snippet, index + 1, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new ArgumentException($"'{snippet}' is not in the source {occurrence + 1} times");
            }
        }

        return new Location(PositionAt(source, index), PositionAt(source, index + snippet.Length));
    }

    private static Position PositionAt(string source, int index)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return new Position(line, index - lineStart + 1);
    }
}
