using System.Text;

namespace UpgradeAgent.Publishing;

internal static class Markdown
{
    private const string Special = "\\`*_[]<>|#";

    public static string EscapeInline(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if (Special.Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
