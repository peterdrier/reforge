namespace ScoringCorpus.InjectInterfaceNotConcrete.Before;

public sealed class InvoiceService
{
    private readonly CampBillingService _billing;

    public InvoiceService(CampBillingService billing) => _billing = billing;

    public int TotalDue(int campId) => _billing.BalanceFor(campId);
}
