namespace ScoringCorpus.CopyPasteTestSetup.Before;

public sealed class Discounts
{
    public decimal VolumeDiscountPercent(int quantity)
    {
        if (quantity >= 500) return 15m;
        if (quantity >= 100) return 10m;
        if (quantity >= 20) return 5m;
        return 0m;
    }

    public decimal Discounted(decimal unitPrice, int quantity)
    {
        var gross = unitPrice * quantity;
        return gross - Math.Round(gross * VolumeDiscountPercent(quantity) / 100m, 2, MidpointRounding.AwayFromZero);
    }
}
