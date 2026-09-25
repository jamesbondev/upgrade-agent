using System.Text;

namespace UpgradeAgent.Publishing;

internal static class Markdown
{
    private const string Special = "\\`*_[]<>|#";

    /// <summary>
    /// Makes untrusted text (anything the agent wrote) safe to put on one Markdown line: newlines flattened and
    /// every character that could start formatting, a table cell, HTML or a link escaped.
    /// </summary>
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
