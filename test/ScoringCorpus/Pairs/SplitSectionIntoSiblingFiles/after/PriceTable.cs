namespace ScoringCorpus.SplitSectionIntoSiblingFiles.After;

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
