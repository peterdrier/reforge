namespace ScoringCorpus.NarrowToReadInterface.After;

public interface ICampBillingReadService
{
    int BalanceFor(int campId);
}

public interface ICampBillingService : ICampBillingReadService
{
    void Charge(int campId, int amount);
}

public sealed class CampBillingService : ICampBillingService
{
    private readonly Dictionary<int, int> _balances = new();

    public int BalanceFor(int campId) => _balances.TryGetValue(campId, out var b) ? b : 0;
    public void Charge(int campId, int amount) => _balances[campId] = BalanceFor(campId) + amount;
}
