namespace ScoringCorpus.NarrowToReadInterface.After;

public sealed class RefundService
{
    private readonly ICampBillingService _billing;

    public RefundService(ICampBillingService billing) => _billing = billing;

    public void Refund(int campId, int amount) => _billing.Charge(campId, -amount);
}
