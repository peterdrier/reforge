namespace ScoringCorpus.SplitGodClassIntoSiblings.Before;

public static class ReportBuilder
{
    public static string Money(decimal amount, string currency)
        => currency switch
        {
            "EUR" => $"{amount:0.00} EUR",
            "USD" => $"${amount:0.00}",
            _ => $"{amount:0.00} {currency}",
        };

    public static string Percent(decimal fraction)
        => $"{Math.Round(fraction * 100m, 1, MidpointRounding.AwayFromZero)}%";

    public static string Heading(string title, DateOnly from, DateOnly to)
        => $"{title.Trim()} ({from:yyyy-MM-dd} to {to:yyyy-MM-dd})";

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
