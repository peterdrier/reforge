namespace ScoringCorpus.MergeTinyUnreused.Before;

public sealed class Greeter
{
    public string Greet(string name, int hour)
    {
        var who = Clean(name);
        var part = PartOfDay(hour);
        return Join(part, who);
    }

    private static string Clean(string name) => name.Trim();
    private static string PartOfDay(int hour) => hour < 12 ? "morning" : "day";
    private static string Join(string part, string who) => $"Good {part}, {who}";
}
