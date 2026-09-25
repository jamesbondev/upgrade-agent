using System.Text.RegularExpressions;

namespace UpgradeAgent.MsBuild;

internal sealed record VersionValue(string Text, int Start, int Length);

internal sealed record VersionEntry(string Element, string Id, VersionValue? Value, bool HasVersionOverride);

internal static partial class VersionEntryScanner
{
    public static IReadOnlyList<VersionEntry> Scan(string text)
    {
        var scannable = Comment().Replace(text, m => new string(' ', m.Length));

        var entries = new List<VersionEntry>();
        foreach (Match tag in OpeningTag().Matches(scannable))
        {
            var attributes = tag.Groups["attributes"];
            var idMatch = IdAttribute().Match(attributes.Value);
            if (!idMatch.Success)
            {
                continue;
            }

            var element = tag.Groups["name"].Value;
            var hasOverride = VersionOverrideAttribute().IsMatch(attributes.Value);
            VersionValue? value = null;

            var versionAttribute = VersionAttribute().Match(attributes.Value);
            if (versionAttribute.Success)
            {
                var group = versionAttribute.Groups["value"];
                value = new VersionValue(group.Value, attributes.Index + group.Index, group.Length);
            }
            else if (tag.Groups["selfClose"].Value.Length == 0)
            {
                var bodyStart = tag.Index + tag.Length;
                var closing = scannable.IndexOf($"</{element}>", bodyStart, StringComparison.Ordinal);
                if (closing > 0)
                {
                    var body = scannable[bodyStart..closing];
                    hasOverride |= body.Contains("<VersionOverride>", StringComparison.Ordinal);
                    if (VersionElement().Match(body) is { Success: true } child)
                    {
                        var group = child.Groups["value"];
                        value = new VersionValue(group.Value.Trim(), bodyStart + group.Index, group.Length);
                    }
                }
            }

            entries.Add(new VersionEntry(element, idMatch.Groups["id"].Value.Trim(), value, hasOverride));
        }

        return entries;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<(?<name>PackageVersion|PackageReference|GlobalPackageReference)\b(?<attributes>(?:[^>""']|""[^""]*""|'[^']*')*?)(?<selfClose>/?)>", RegexOptions.Singleline)]
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
