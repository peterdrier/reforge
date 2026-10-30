namespace ScoringCorpus.SplitSectionIntoSiblingFiles.Before;

public sealed class PriceTable
{
    private readonly Dictionary<string, decimal> _unitPrices = new(StringComparer.Ordinal);

    public void SetUnitPrice(string sku, decimal unitPrice)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("A price needs a SKU.", nameof(sku));
        if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice), "A price cannot be negative.");
        _unitPrices[sku] = unitPrice;
    }

    public decimal UnitPriceOf(string sku)
        => _unitPrices.TryGetValue(sku, out var unitPrice) ? unitPrice : throw new KeyNotFoundException($"No price for {sku}.");
}

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

public sealed class InvoiceTotals
{
    public decimal LineTotal(decimal unitPrice, int quantity, decimal discountPercent)
    {
        var gross = unitPrice * quantity;
        var discount = Math.Round(gross * discountPercent / 100m, 2, MidpointRounding.AwayFromZero);
        return gross - discount;
    }

    public decimal InvoiceTotal(IReadOnlyList<decimal> lineTotals, decimal taxPercent)
    {
        var net = lineTotals.Sum();
        return net + Math.Round(net * taxPercent / 100m, 2, MidpointRounding.AwayFromZero);
    }
}
