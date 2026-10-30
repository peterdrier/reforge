namespace ScoringCorpus.MergeTinyUnreused.After;

public sealed class Greeter
{
    public string Greet(string name, int hour)
    {
        var part = hour < 12 ? "morning" : "day";
        return $"Good {part}, {name.Trim()}";
    }
}
