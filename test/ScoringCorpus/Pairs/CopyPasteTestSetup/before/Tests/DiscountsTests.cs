namespace ScoringCorpus.CopyPasteTestSetup.Before.Tests;

public sealed class DiscountsTests
{
    public void SmallOrdersPayFullPrice() => Expect(10, 100m);
    public void TwentyGetFivePercent() => Expect(20, 190m);
    public void HundredGetTenPercent() => Expect(100, 900m);
    public void FiveHundredGetFifteenPercent() => Expect(500, 4250m);

    private static void Expect(int quantity, decimal total)
    {
        var actual = new Discounts().Discounted(10m, quantity);
        if (actual != total) throw new InvalidOperationException($"{quantity}: {actual} != {total}");
    }
}
