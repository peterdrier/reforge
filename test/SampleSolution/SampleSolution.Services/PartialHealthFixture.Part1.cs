namespace SampleSolution.Services;

public partial class PartialHealthFixture
{
    private readonly string _first = "first";
    private readonly string _second = "second";

    public string First() => _first;

    public string Both() => _first + _second;

    public string Second() => _second;
}
