namespace ScoringCorpus.InjectInterfaceNotConcrete.After;

public sealed class InvoiceService
{
    private readonly ICampBillingService _billing;

    public InvoiceService(ICampBillingService billing) => _billing = billing;

    public int TotalDue(int campId) => _billing.BalanceFor(campId);
}
