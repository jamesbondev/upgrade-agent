using System.Text.RegularExpressions;

namespace UpgradeAgent.Bumping;

/// <param name="ValueStart">Offset of the version value in the file text; null when the entry has no version.</param>
public sealed record VersionEntry(
    string Element,
    string Id,
    string? Version,
    bool HasVersionOverride,
    int? ValueStart,
    int? ValueLength);

/// <summary>
/// Finds <c>PackageVersion</c>, <c>PackageReference</c> and <c>GlobalPackageReference</c> entries by scanning
/// the raw text rather than round-tripping XML, so an edit changes exactly one value and nothing else.
/// Entries inside XML comments are ignored.
/// </summary>
public static partial class VersionEntryScanner
{
    public static IReadOnlyList<VersionEntry> Scan(string text)
    {
        var comments = Comment().Matches(text).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        bool InComment(int index) => comments.Any(c => index >= c.Start && index < c.End);

        var entries = new List<VersionEntry>();
        foreach (Match tag in OpeningTag().Matches(text))
        {
            if (InComment(tag.Index))
            {
                continue;
            }

            var element = tag.Groups["name"].Value;
            var attributes = tag.Groups["attributes"];
            var idMatch = IdAttribute().Match(attributes.Value);
            if (!idMatch.Success)
            {
                continue;
            }

            var hasOverride = VersionOverrideAttribute().IsMatch(attributes.Value);
            string? version = null;
            int? start = null;
            int? length = null;

            var versionAttribute = VersionAttribute().Match(attributes.Value);
            if (versionAttribute.Success)
            {
                var value = versionAttribute.Groups["value"];
                (version, start, length) = (value.Value, attributes.Index + value.Index, value.Length);
            }
            else if (tag.Groups["selfClose"].Value.Length == 0)
            {
                var closing = text.IndexOf($"</{element}>", tag.Index + tag.Length, StringComparison.Ordinal);
                if (closing > 0)
                {
                    var body = text[(tag.Index + tag.Length)..closing];
                    hasOverride |= body.Contains("<VersionOverride>", StringComparison.Ordinal);
                    var child = VersionElement().Match(body);
                    if (child.Success)
                    {
                        var value = child.Groups["value"];
                        (version, start, length) = (value.Value.Trim(), tag.Index + tag.Length + value.Index, value.Length);
                    }
                }
            }

            entries.Add(new VersionEntry(element, idMatch.Groups["id"].Value.Trim(), version, hasOverride, start, length));
        }

        return entries;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<(?<name>PackageVersion|PackageReference|GlobalPackageReference)\b(?<attributes>[^>]*?)(?<selfClose>/?)>", RegexOptions.Singleline)]
    private static partial Regex OpeningTag();

    [GeneratedRegex(@"\b(?:Include|Update)\s*=\s*(?<q>[""'])(?<id>[^""']*)\k<q>")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"\bVersion\s*=\s*(?<q>[""'])(?<value>[^""']*)\k<q>")]
    private static partial Regex VersionAttribute();

    [GeneratedRegex(@"\bVersionOverride\s*=")]
    private static partial Regex VersionOverrideAttribute();

    [GeneratedRegex(@"<Version>(?<value>[^<]*)</Version>")]
    private static partial Regex VersionElement();
}
