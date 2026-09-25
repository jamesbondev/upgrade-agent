using Markdig;
using Markdig.Syntax;

namespace ReadmeChecker.Detection;

internal sealed record ReadmeChunk(int Index, int FirstLine, int LastLine, string Text, IReadOnlyList<string> Headings);

internal sealed record ChunkPlan(IReadOnlyList<ReadmeChunk> Chunks, IReadOnlyList<(int FirstLine, int LastLine)> Unchecked, IReadOnlyList<string> Outline);

internal static class ReadmeChunks
{
    public const int TargetLines = 80;
    public const int MaxChunks = 8;

    public static ChunkPlan Plan(string readme)
    {
        var lines = readme.ReplaceLineEndings("\n").Split('\n');
        var document = Markdown.Parse(readme);
        var headings = document.Descendants<HeadingBlock>().Where(h => h.Level <= 2).ToList();
        var unsplittable = document.Descendants<Block>()
            .Where(b => b is FencedCodeBlock or ListBlock)
            .Select(b => (First: b.Line + 1, Last: Math.Max(b.Line + 1, LineAt(readme, b.Span.End))))
            .ToList();

        var starts = headings.Select(h => h.Line + 1).Where(l => l > 1).Prepend(1).Distinct().Order().ToList();
        var sections = starts.Select((start, i) => (First: start, Last: i + 1 < starts.Count ? starts[i + 1] - 1 : lines.Length))
            .SelectMany(s => s.Last - s.First + 1 > TargetLines * 2 ? SplitSection(s.First, s.Last, lines, unsplittable) : [s])
            .ToList();

        var packed = new List<(int First, int Last)>();
        foreach (var section in sections)
        {
            if (packed.Count > 0 && packed[^1].Last - packed[^1].First + 1 + (section.Last - section.First + 1) <= TargetLines)
            {
                packed[^1] = (packed[^1].First, section.Last);
            }
            else
            {
                packed.Add(section);
            }
        }

        var outline = headings.Select(h => $"{new string('#', h.Level)} {HeadingText(h, lines)}").ToList();
        var chunks = packed.Take(MaxChunks)
            .Select((c, i) => new ReadmeChunk(
                i + 1,
                c.First,
                c.Last,
                string.Join('\n', lines[(c.First - 1)..c.Last]),
                headings.Where(h => h.Line + 1 >= c.First && h.Line + 1 <= c.Last).Select(h => HeadingText(h, lines)).ToList()))
            .ToList();
        var notChecked = packed.Skip(MaxChunks).ToList();
        return new ChunkPlan(chunks, notChecked, outline);
    }

    private static IEnumerable<(int First, int Last)> SplitSection(int first, int last, string[] lines, IReadOnlyList<(int First, int Last)> unsplittable)
    {
        var start = first;
        for (var line = first; line <= last; line++)
        {
            var isSafeBreak = lines[line - 1].Trim().Length == 0 && !unsplittable.Any(u => line > u.First && line < u.Last);
            if (isSafeBreak && line - start + 1 >= TargetLines)
            {
                yield return (start, line);
                start = line + 1;
            }
        }

        if (start <= last)
        {
            yield return (start, last);
        }
    }

    private static int LineAt(string text, int index) => text.AsSpan(0, Math.Clamp(index, 0, text.Length)).Count('\n') + 1;

    private static string HeadingText(HeadingBlock heading, string[] lines) =>
        lines[heading.Line].TrimStart('#', ' ').Trim();
}
