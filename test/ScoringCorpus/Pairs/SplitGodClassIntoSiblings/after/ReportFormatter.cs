namespace ScoringCorpus.SplitGodClassIntoSiblings.After;

public static class ReportFormatter
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
}
