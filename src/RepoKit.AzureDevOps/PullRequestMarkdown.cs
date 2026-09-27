using System.Text;

namespace RepoKit.AzureDevOps;

public static class PullRequestMarkdown
{
    private const string Special = "\\`*_[]<>|#!";
    private const char ZeroWidthSpace = '​';

    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if (Special.Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
            if (c == '@')
            {
                builder.Append(ZeroWidthSpace);
            }
        }

        return builder.ToString();
    }
}
