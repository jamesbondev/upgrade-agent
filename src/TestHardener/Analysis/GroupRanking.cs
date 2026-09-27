namespace TestHardener.Analysis;

internal static class GroupRanking
{
    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, SurvivorGroup> group) =>
        items.OrderByDescending(i => group(i).LogicSurvivors)
            .ThenByDescending(i => group(i).Survivors.Count)
            .ThenByDescending(i => group(i).FixCommits)
            .ThenBy(i => group(i).File, StringComparer.Ordinal)
            .ThenBy(i => group(i).Member.StartLine);
}
