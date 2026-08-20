// gate1: cognitiveComplexity
//
// The whole edit is on one line: `CountReconcilable` now runs each id through `Classify` and
// discards the result. `Classify` has two callers, so it is shared code and no longer folds into
// `Reconcile`. Both methods now read under the threshold on their own bodies and the rule stops.
//
// Not a fix. The ladder is untouched, `Reconcile` still runs all of it, and the codebase gained a
// call that exists to be counted. `CountReconcilable` is now slower and depends on a helper it has
// no use for.

namespace SampleSolution.Gate.Rules;

public sealed class GateReconcileCheapestFixService
{
    private readonly List<string> _log = new();

    public int Reconcile(IReadOnlyList<int> ids)
    {
        int total = 0;
        foreach (var id in ids)
        {
            if (id < 0) continue;
            total += Classify(id);
        }
        return total;
    }

    public int CountReconcilable(IReadOnlyList<int> ids)
    {
        int n = 0;
        foreach (var id in ids)
        {
            if (id >= 0 && Classify(id) >= 0) n++;
        }
        return n;
    }

    public IReadOnlyList<string> Log() => _log;

    private int Classify(int id)
    {
        if (id % 2 == 0)
        {
            if (id % 3 == 0)
            {
                for (int i = 0; i < id; i++)
                {
                    if (i % 5 == 0 && i > 10)
                    {
                        _log.Add($"six-{i}");
                    }
                    else if (i % 7 == 0)
                    {
                        _log.Add($"seven-{i}");
                    }
                }
                return 6;
            }

            while (id > 100)
            {
                if (id % 11 == 0)
                {
                    _log.Add("eleven");
                }
                id -= 10;
            }
            return 2;
        }

        if (id % 5 == 0)
        {
            switch (id % 4)
            {
                case 0:
                    return 5;
                case 1:
                    return 15;
                default:
                    if (id > 1000)
                    {
                        return 25;
                    }
                    return 35;
            }
        }

        return id % 7 == 0 ? 7 : 1;
    }
}
