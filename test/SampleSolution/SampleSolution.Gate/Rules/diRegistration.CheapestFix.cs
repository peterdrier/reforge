// gate1: diRegistration
//
// AddScoped<IService, TImpl>() -> AddScoped(typeof(IService), typeof(TImpl)). Same container, same
// two types, same resolution at runtime.
//
// ScoreDiRegistrationsAsync matches the invocation's method name and then requires the expression
// to be a GenericNameSyntax carrying at least one type argument. The non-generic overload has no
// type argument list, so the call is skipped before the classification lookup is ever reached. The
// interface stays classified and its method keeps charging; only the registration charge vanishes.

namespace SampleSolution.Gate.Rules;

public interface IGateRegistrarCheapestFixService
{
    int Count();
}

public sealed class GateRegistrarCheapestFixService : IGateRegistrarCheapestFixService
{
    public int Count() => 0;
}

public static class GateCheapestFixServiceCollection
{
    public static void AddScoped<TService, TImplementation>() where TImplementation : TService { }
    public static void AddScoped(Type service, Type implementation) { }
}

public static class GateCheapestFixRegistrationRoot
{
    public static void Register()
    {
        GateCheapestFixServiceCollection.AddScoped(
            typeof(IGateRegistrarCheapestFixService),
            typeof(GateRegistrarCheapestFixService));
    }
}
