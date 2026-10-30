namespace ScoringCorpus.NarrowToReadInterface.After;

public sealed class InvoiceService
{
    private readonly ICampBillingReadService _billing;

    public InvoiceService(ICampBillingReadService billing) => _billing = billing;

    public int TotalDue(int campId) => _billing.BalanceFor(campId);
}
