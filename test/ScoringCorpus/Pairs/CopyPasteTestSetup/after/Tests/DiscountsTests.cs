namespace ScoringCorpus.CopyPasteTestSetup.After.Tests;

public sealed class DiscountsTests
{
    public void SmallOrdersPayFullPrice()
    {
        var discounts = new Discounts();
        var unitPrice = 10m;
        var quantity = 10;
        var expectedTotal = 100m;
        var actualTotal = discounts.Discounted(unitPrice, quantity);
        if (actualTotal != expectedTotal)
            throw new InvalidOperationException($"SmallOrdersPayFullPrice: quantity {quantity} at {unitPrice} gave {actualTotal}, expected {expectedTotal}");
    }

    public void TwentyGetFivePercent()
    {
        var discounts = new Discounts();
        var unitPrice = 10m;
        var quantity = 20;
        var expectedTotal = 190m;
        var actualTotal = discounts.Discounted(unitPrice, quantity);
        if (actualTotal != expectedTotal)
            throw new InvalidOperationException($"TwentyGetFivePercent: quantity {quantity} at {unitPrice} gave {actualTotal}, expected {expectedTotal}");
    }

    public void HundredGetTenPercent()
    {
        var discounts = new Discounts();
        var unitPrice = 10m;
        var quantity = 100;
        var expectedTotal = 900m;
        var actualTotal = discounts.Discounted(unitPrice, quantity);
        if (actualTotal != expectedTotal)
            throw new InvalidOperationException($"HundredGetTenPercent: quantity {quantity} at {unitPrice} gave {actualTotal}, expected {expectedTotal}");
    }

    public void FiveHundredGetFifteenPercent()
    {
        var discounts = new Discounts();
        var unitPrice = 10m;
        var quantity = 500;
        var expectedTotal = 4250m;
        var actualTotal = discounts.Discounted(unitPrice, quantity);
        if (actualTotal != expectedTotal)
            throw new InvalidOperationException($"FiveHundredGetFifteenPercent: quantity {quantity} at {unitPrice} gave {actualTotal}, expected {expectedTotal}");
    }
}
