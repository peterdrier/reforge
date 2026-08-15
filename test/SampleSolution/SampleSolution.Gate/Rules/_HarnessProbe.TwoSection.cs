// Not a Gate 1 fixture — the isolated-variant harness's own two-section probe, exercised by
// IsolatedVariantScorerTests. Named so it does not end in ".Before.cs" and so is invisible to
// DiscoverPairs: a probe that claimed to gate a rule would be worse than no probe.
//
// Its companion, _HarnessProbe.TwoSection.Camp.cs, is compiled as SampleSolution.Camp when this
// file is scored in isolation, which puts the two types in different sections and lets a
// cross-section rule fire. Inside the full sample solution both files are just Gate, so the probe
// must not declare anything that changes what Gate is: a *Repository would make the section
// repo-backed and turn the missing* rules on for every Gate 1 fixture's neighbourhood. Hence a
// service interface, and crossSectionFullService rather than crossSectionRepository.

namespace SampleSolution.Gate.Rules;

public sealed class GateProbeService
{
    private readonly ICampProbeService _camp;

    public GateProbeService(ICampProbeService camp) => _camp = camp;

    public int CountCampers() => _camp.Count();
}
