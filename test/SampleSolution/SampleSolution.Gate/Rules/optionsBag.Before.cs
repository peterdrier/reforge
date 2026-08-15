// gate1: optionsBag
// gate1-gameable: unbundling the bag back into loose parameters satisfies the rule and costs only
// methodParameterOverflow, which is a fraction of what optionsBag charges. The cheapest edit is the
// one that undoes a parameter-object refactor.

namespace SampleSolution.Gate.Rules;

public sealed class GateSyncBeforeService
{
    public void Configure(GateSyncOptions options)
    {
        _ = options;
    }
}

public sealed class GateSyncOptions
{
    public string Endpoint { get; set; } = "";
    public int RetryCount { get; set; }
    public int TimeoutSeconds { get; set; }
    public string Mode { get; set; } = "";
}
