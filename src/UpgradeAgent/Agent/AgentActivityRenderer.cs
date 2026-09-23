using System.Text.Json;
using Spectre.Console;
using UpgradeAgent.Build;

namespace UpgradeAgent.Agent;

/// <summary>
/// Turns agent events into append-only console lines (and a plain-text log). Events arrive on
/// background threads, so every write takes the shared console lock.
/// </summary>
public sealed class AgentActivityRenderer(IAnsiConsole console, object consoleLock)
{
    private string _worktree = "";
    private readonly Dictionary<string, (string Tool, string Detail)> _started = [];
    private StreamWriter? _log;

    /// <summary>Starts a group: sets the worktree that paths are shown relative to, and the log file.</summary>
    public void StartLog(string path, string worktree)
    {
        _worktree = worktree;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _log = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void StopLog()
    {
        _log?.Dispose();
        _log = null;
    }

    public void ToolStarted(string callId, string tool, JsonElement? arguments, string? shellCommand)
    {
        var detail = tool switch
        {
            "bash" or "powershell" => shellCommand ?? Argument(arguments, "command") ?? "",
            "view" or "create" or "edit" => Relative(Argument(arguments, "path") ?? ""),
            "grep" => $"{Argument(arguments, "pattern")} {Relative(Argument(arguments, "path") ?? "")}".Trim(),
            "glob" => Argument(arguments, "pattern") ?? "",
            _ => "",
        };

        lock (consoleLock)
        {
            _started[callId] = (tool, detail);
        }

        var (icon, color) = tool switch
        {
            "bash" or "powershell" => ("$", "blue"),
            "edit" or "create" => ("✎", "yellow"),
            _ => ("·", "grey"),
        };

        // Reads are frequent and dull; keep them quiet on screen but in the log.
        var quiet = tool is "view" or "grep" or "glob" or "read_bash" or "list_bash" or "report_intent";
        Write(quiet ? $"    [grey]{icon} {Markup.Escape(tool)} {Markup.Escape(Truncate(detail, 110))}[/]" : $"    [{color}]{icon}[/] {Markup.Escape(Truncate(detail, 120))}",
            $"TOOL {tool} {detail}");
    }

    public void ToolCompleted(string callId, bool success, string? output, string? error)
    {
        (string Tool, string Detail) started;
        lock (consoleLock)
        {
            if (!_started.Remove(callId, out started))
            {
                return;
            }
        }

        if (!success)
        {
            Write($"      [red]✗ {Markup.Escape(Truncate(error ?? "failed", 140))}[/]", $"  FAILED {error}");
            return;
        }

        if (started.Tool is not ("bash" or "powershell") || output is null)
        {
            return;
        }

        if (started.Detail.Contains("dotnet build", StringComparison.Ordinal))
        {
            var (errors, _) = BuildOutputParser.ParseDiagnostics(output);
            var line = errors.Count == 0 && !output.Contains("Build FAILED", StringComparison.Ordinal)
                ? "      [green]→ build succeeded[/]"
                : $"      [red]→ build: {errors.Count} error(s)[/] [grey]{Markup.Escape(TopCodes(errors))}[/]";
            Write(line, $"  BUILD {errors.Count} errors");
        }
        else if (started.Detail.Contains("dotnet test", StringComparison.Ordinal) && BuildOutputParser.ParseTestCounts(output) is { } counts)
        {
            Write(counts.Failed == 0
                ? $"      [green]→ tests: {counts.Passed} passed[/]"
                : $"      [red]→ tests: {counts.Failed} failed[/], {counts.Passed} passed", $"  TESTS {counts}");
        }
    }

    public void AssistantMessage(string? content)
    {
        var line = content?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is not null)
        {
            Write($"  [grey italic]{Markup.Escape(Truncate(line, 130))}[/]", $"AGENT {content}");
        }
    }

    public void Refused(string action, string reason) =>
        Write($"    [red]⊘ refused[/] [grey]{Markup.Escape(Truncate(action, 80))} — {Markup.Escape(Truncate(reason, 90))}[/]", $"REFUSED {action}: {reason}");

    public void Note(string text) => Write($"  [grey]{Markup.Escape(text)}[/]", text);

    public void Final(string? text) => Log($"FINAL {text}");

    public void Log(string text)
    {
        lock (consoleLock)
        {
            _log?.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} {text}");
        }
    }

    private void Write(string markup, string logLine)
    {
        lock (consoleLock)
        {
            console.MarkupLine(markup);
            _log?.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} {logLine}");
        }
    }

    private string Relative(string path) =>
        path.Length > 0 && Path.IsPathRooted(path) ? Path.GetRelativePath(_worktree, path).Replace('\\', '/') : path;

    private static string? Argument(JsonElement? arguments, string name) =>
        arguments is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string TopCodes(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join(", ", diagnostics.GroupBy(d => d.Code).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key}×{g.Count()}"));

    private static string Truncate(string text, int length)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..(length - 1)] + "…";
    }
}
