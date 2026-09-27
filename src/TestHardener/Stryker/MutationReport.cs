using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestHardener.Stryker;

internal enum MutantStatus
{
    Killed,
    Survived,
    NoCoverage,
    CompileError,
    RuntimeError,
    Timeout,
    Ignored,
    Pending,
}

internal sealed record Position(int Line, int Column);

internal sealed record Location(Position Start, Position End);

internal sealed record Mutant(
    string File,
    string Id,
    string Mutator,
    string? Replacement,
    Location Location,
    MutantStatus Status,
    string? StatusReason,
    bool Static,
    IReadOnlyList<string> CoveredBy,
    IReadOnlyList<string> KilledBy)
{
    public bool IsStringMutation => Mutator.Contains("String", StringComparison.OrdinalIgnoreCase);

    public MutantKey Key => new(File, Mutator, Location, Replacement);
}

internal sealed record MutantKey(string File, string Mutator, Location Location, string? Replacement);

internal sealed record MutationReport(IReadOnlyList<Mutant> Mutants, IReadOnlyDictionary<string, string> TestNames)
{
    public int TestCount => TestNames.Count;

    public IReadOnlyDictionary<MutantStatus, int> StatusCounts =>
        Mutants.GroupBy(m => m.Status).ToDictionary(g => g.Key, g => g.Count());

    public double? Score
    {
        get
        {
            var detected = Mutants.Count(m => m.Status is MutantStatus.Killed or MutantStatus.Timeout);
            var valid = detected + Mutants.Count(m => m.Status is MutantStatus.Survived or MutantStatus.NoCoverage);
            return valid == 0 ? null : 100.0 * detected / valid;
        }
    }
}

internal sealed class MutationReportException(string message) : Exception(message);

internal static class MutationReportParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static MutationReport Parse(string json, string repoRoot)
    {
        ReportDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ReportDto>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new MutationReportException($"The mutation report isn't valid JSON: {ex.Message}");
        }

        if (dto?.Files is null)
        {
            throw new MutationReportException("The mutation report has no files section.");
        }

        var mutants = dto.Files
            .SelectMany(file => (file.Value.Mutants ?? []).Select(m => ToMutant(RelativePath(file.Key, repoRoot), m)))
            .ToList();
        var tests = (dto.TestFiles ?? [])
            .SelectMany(file => file.Value.Tests ?? [])
            .Where(t => t.Id is not null && t.Name is not null)
            .GroupBy(t => t.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name!, StringComparer.Ordinal);
        return new MutationReport(mutants, tests);
    }

    public static async Task<MutationReport> ReadAsync(string path, string repoRoot, CancellationToken cancellationToken) =>
        Parse(await File.ReadAllTextAsync(path, cancellationToken), repoRoot);

    internal static string RelativePath(string reportPath, string repoRoot)
    {
        var normalized = reportPath.Replace('\\', '/');
        var root = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
        return normalized.StartsWith(root, StringComparison.Ordinal) ? normalized[root.Length..] : normalized.TrimStart('/');
    }

    private static Mutant ToMutant(string file, MutantDto dto)
    {
        if (dto.Id is null || dto.MutatorName is null || dto.Location?.Start is null || dto.Location.End is null || dto.Status is null)
        {
            throw new MutationReportException($"A mutant in {file} is missing its id, mutator, location or status.");
        }

        return new Mutant(
            file,
            dto.Id,
            dto.MutatorName,
            dto.Replacement,
            new Location(new Position(dto.Location.Start.Line, dto.Location.Start.Column), new Position(dto.Location.End.Line, dto.Location.End.Column)),
            dto.Status.Value,
            dto.StatusReason,
            dto.Static ?? false,
            dto.CoveredBy ?? [],
            dto.KilledBy ?? []);
    }

    private sealed record ReportDto(Dictionary<string, FileDto>? Files, Dictionary<string, TestFileDto>? TestFiles);

    private sealed record FileDto(List<MutantDto>? Mutants);

    private sealed record MutantDto(
        string? Id,
        string? MutatorName,
        string? Replacement,
        LocationDto? Location,
        MutantStatus? Status,
        string? StatusReason,
        bool? Static,
        List<string>? CoveredBy,
        List<string>? KilledBy);

    private sealed record LocationDto(PositionDto? Start, PositionDto? End);

    private sealed record PositionDto(int Line, int Column);

    private sealed record TestFileDto(List<TestDto>? Tests);

    private sealed record TestDto(string? Id, string? Name);
}
