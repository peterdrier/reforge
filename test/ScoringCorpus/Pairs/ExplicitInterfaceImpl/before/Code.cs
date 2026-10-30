namespace ScoringCorpus.ExplicitInterfaceImpl.Before;

internal interface IClassifier
{
    int Classify(IReadOnlyList<int> values);
}

internal sealed class Classifier : IClassifier
{
    public int Classify(IReadOnlyList<int> values)
    {
        int total = 0;
        foreach (var v in values)
        {
            if (v < 0) continue;
            if (v % 2 == 0)
            {
                if (v % 3 == 0) total += 6;
                else total += 2;
            }
            else if (v % 7 == 0) total += 7;
        }
        return total;
    }
}

public sealed class Reconciler
{
    private readonly IClassifier _classifier = new Classifier();

    public int Run(IReadOnlyList<int> values) => _classifier.Classify(values) + 1;
}
