// gate1: diRegistration
// gate1-gameable: the generic registration becomes the non-generic overload. The container is
// handed the same two types, resolves the same implementation for the same service, and runs
// identically — but the detector reads the *syntax* of the call, requiring a GenericNameSyntax with
// at least one type argument, so typeof() arguments are invisible to it. The charge goes to zero
// and nothing else in the file moves.
//
// This pair exists because the rule was dead. The lookup at SurfaceScoreEngine.CrossCutting.cs:142
// keyed on the bare display string while the dictionary is keyed on
// SolutionClassifier.TypeKey ("{assembly}|{name}"), so it never matched: diRegistration scored 0 on
// Humans against 452 generic registrations, and 0 on Reforge, and nothing noticed for as long as
// the rule has existed. The reason it went unnoticed is that `diRegistration` sat in
// NotYetCovered — EveryDeclaredRule_ActuallyFiresInItsBeforeFixture is exactly the assertion that
// catches "shipped rule, fires nowhere", and a rule with no fixture is a rule nobody asked to fire.
//
// AddScoped is declared locally rather than imported: the sample solution declares no
// PackageReference by invariant, and the rule only needs the method's *name* plus a resolvable
// first type argument, so a BCL-only stand-in exercises the identical path.

namespace SampleSolution.Gate.Rules;

public interface IGateRegistrarService
{
    int Count();
}

public sealed class GateRegistrarService : IGateRegistrarService
{
    public int Count() => 0;
}

public static class GateServiceCollection
{
    public static void AddScoped<TService, TImplementation>() where TImplementation : TService { }
    public static void AddScoped(Type service, Type implementation) { }
}

public static class GateRegistrationRoot
{
    public static void Register()
    {
        GateServiceCollection.AddScoped<IGateRegistrarService, GateRegistrarService>();
    }
}
