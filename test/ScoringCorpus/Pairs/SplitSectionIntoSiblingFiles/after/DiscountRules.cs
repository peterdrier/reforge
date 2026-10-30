namespace ScoringCorpus.SplitSectionIntoSiblingFiles.After;

public sealed class DiscountRules
{
    public decimal VolumeDiscountPercent(int quantity)
    {
        if (quantity >= 500) return 15m;
        if (quantity >= 100) return 10m;
        if (quantity >= 20) return 5m;
        return 0m;
    }

    public decimal LoyaltyDiscountPercent(int yearsAsCustomer)
        => yearsAsCustomer >= 5 ? 3m : yearsAsCustomer >= 2 ? 1m : 0m;
}
