namespace ScoringCorpus.SplitPartialAcrossFiles.After;

public sealed partial class Roster
{
    public int Count() => _names.Count(n => n.Length > 0);
}
