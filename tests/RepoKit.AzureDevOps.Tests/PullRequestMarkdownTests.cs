namespace RepoKit.AzureDevOps.Tests;

public class PullRequestMarkdownTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a *bold* `code` [link](x) <b> | # !", "a \\*bold\\* \\`code\\` \\[link\\](x) \\<b\\> \\| \\# \\!")]
    [InlineData("two\nlines", "two lines")]
    public void Escape_EscapesMarkdownAndFlattensLines(string text, string expected) =>
        Assert.Equal(expected, PullRequestMarkdown.Escape(text));

    [Fact]
    public void Escape_DefusesMentions() =>
        Assert.Equal("ask @​team", PullRequestMarkdown.Escape("ask @team"));
}
