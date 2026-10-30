namespace ScoringCorpus.InjectInterfaceNotConcrete.After;

public interface ICampBillingService
{
    int BalanceFor(int campId);
    void Charge(int campId, int amount);
}

public sealed class CampBillingService : ICampBillingService
{
    private readonly Dictionary<int, int> _balances = new();

    public int BalanceFor(int campId) => _balances.TryGetValue(campId, out var b) ? b : 0;
    public void Charge(int campId, int amount) => _balances[campId] = BalanceFor(campId) + amount;
}
