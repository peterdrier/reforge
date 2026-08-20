// gate1: crossSectionRepository
// gate1-gameable: the constructor parameter becomes a settable property. Same dependency, same
// assembly reference, same calls, same boundary crossed — ScoreDependencyUse reads constructor
// parameters and nothing else, so the 25 vanishes while the coupling stays. Same hole
// crossSectionFullService demonstrates at 8; this is the rule it pays best on.

namespace SampleSolution.Gate.Rules;

public sealed class GateLedgerBeforeService
{
    private readonly ICampLedgerBeforeRepository _ledger;

    public GateLedgerBeforeService(ICampLedgerBeforeRepository ledger) => _ledger = ledger;

    public Task<string> Row(int campId, CancellationToken ct) => _ledger.LoadAsync(campId, ct);
}
