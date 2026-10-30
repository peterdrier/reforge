namespace ScoringCorpus.InlineSingleUseHelper.Before;

public sealed class Invoice
{
    public decimal Total(IReadOnlyList<decimal> lines, bool member)
    {
        decimal sum = 0;
        foreach (var line in lines)
        {
            if (line <= 0) continue;
            sum += line;
        }
        return ApplyDiscount(sum, member);
    }

    private static decimal ApplyDiscount(decimal sum, bool member)
    {
        if (!member) return sum;
        if (sum > 1000) return sum * 0.8m;
        else if (sum > 100) return sum * 0.9m;
        return sum;
    }
}
