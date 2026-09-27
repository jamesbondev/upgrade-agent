namespace TestHardener.Infrastructure;

internal static class TextFormat
{
    public static string Flat(string text) =>
        string.Join(' ', text.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public static string FlatShort(string text, int max) => Shorten(Flat(text), max);

    public static string Tail(string output, int max = 2000) => output.Length <= max ? output.Trim() : output[^max..].Trim();

    public static string FirstLine(string text, int max = 120) => Shorten(text.ReplaceLineEndings("\n").Split('\n')[0], max);
}
