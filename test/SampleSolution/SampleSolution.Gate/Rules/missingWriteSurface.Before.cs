// gate1: missingWriteSurface
// gate1-gameable: an interface named to match I*Service with one unimplemented mutator satisfies the
// rule. It cannot be empty — an interface publishing nothing demotes to a read classification — so
// the fix costs one fullServiceInterfaceMethod charge and still nets a drop. No implementation, no
// caller, no behaviour: a section publishes a write surface by naming one.
//
// missingPrimaryInfoDto fires on both variants, for the reason recorded in missingReadSurface.

namespace SampleSolution.Gate.Rules;

public interface IGateWritableBeforeRepository
{
    Task<string> LoadAsync(int id, CancellationToken ct);
}

// In both variants so missingReadSurface stays quiet.
public interface IGateWritableBeforeServiceRead
{
    Task<string> GetAsync(int id, CancellationToken ct);
}
