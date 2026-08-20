// The cheapest fix for crossSectionRepository: move the injection out of the constructor.

namespace SampleSolution.Gate.Rules;

public sealed class GateLedgerCheapestFixService
{
    public ICampLedgerFixRepository Ledger { get; set; } = default!;

    public Task<string> Row(int campId, CancellationToken ct) => Ledger.LoadAsync(campId, ct);
}
