namespace ScoringCorpus.SplitSectionIntoSiblingFiles.After;

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
