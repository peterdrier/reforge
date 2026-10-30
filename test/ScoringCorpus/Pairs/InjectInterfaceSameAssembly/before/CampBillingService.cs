namespace ScoringCorpus.InjectInterfaceSameAssembly.Before;

public sealed class CampBillingService
{
    private readonly Dictionary<int, int> _balances = new();

    public int BalanceFor(int campId) => _balances.TryGetValue(campId, out var b) ? b : 0;
    public void Charge(int campId, int amount) => _balances[campId] = BalanceFor(campId) + amount;
}
