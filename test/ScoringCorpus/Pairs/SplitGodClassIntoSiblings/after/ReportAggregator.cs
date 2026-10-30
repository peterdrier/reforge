namespace ScoringCorpus.SplitGodClassIntoSiblings.After;

public static class ReportAggregator
{
    public static decimal Revenue(IReadOnlyList<(string Region, decimal Amount)> sales, string region)
        => sales.Where(s => s.Region == region).Sum(s => s.Amount);

    public static decimal ShareOfTotal(IReadOnlyList<(string Region, decimal Amount)> sales, string region)
    {
        var total = sales.Sum(s => s.Amount);
        return total == 0m ? 0m : Revenue(sales, region) / total;
    }

    public static IReadOnlyList<string> RegionsByRevenue(IReadOnlyList<(string Region, decimal Amount)> sales)
        => sales.GroupBy(s => s.Region)
            .OrderByDescending(g => g.Sum(s => s.Amount))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .ToList();
}
