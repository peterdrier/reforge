// gate1: missingReadSurface
// gate1-gameable: an empty interface named to match the read pattern satisfies the rule. It is
// classified by name, counted, and charges nothing — zero methods, zero readServiceInterfaceMethod
// points — so the 10 disappears and no read API exists. Nothing checks that the interface has
// members, that anything implements it, or that a consumer can get data out of it.
//
// missingPrimaryInfoDto also fires on both variants: only one file in this folder may declare the
// GateInfo the convention looks for, and that is missingPrimaryInfoDto's own pair. Constant in both,
// so the pair's delta is still this rule's alone.

namespace SampleSolution.Gate.Rules;

// Repo-backed is what turns the missing* family on, derived from this declaration alone.
public interface IGateReadableBeforeRepository
{
    Task<string> LoadAsync(int id, CancellationToken ct);
}

// In both variants so missingWriteSurface stays quiet. Unimplemented on purpose: an I*Service with a
// mutating member and no implementation keeps its name-derived write classification.
public interface IGateReadableBeforeService
{
    void Save(int id);
}
