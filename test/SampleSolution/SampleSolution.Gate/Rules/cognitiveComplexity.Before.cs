// gate1: cognitiveComplexity
// gate1-gameable: the helper is given a second caller — an existing public method is routed through
// it in a way that changes no behaviour. A helper with two callers is shared code, so it stops
// folding into `Reconcile`; the entry point's charge drops to its own body and the helper's own
// reading sits under the threshold. Nothing was simplified: the same branches run in the same
// order, and one call site was added.
//
// This is the hole the fold leaves, and it is narrower than the one it closes. The split an agent
// reaches for first — pull the branchy middle into a private helper and call it once — is now
// free of charge in both directions: `effCognitive` is unchanged by construction, so the split
// neither pays nor costs. Escaping the charge takes a *second real call site*, which is either
// genuine reuse (the incentive this rule is built around) or, as here, a manufactured one that a
// reader can see is manufactured.
//
// Closing it means not gating on caller count, and every alternative measured worse: charging every
// helper separately made the rule a size rule (it fires on 59-80% of all private methods on two
// corpora), and folding a shared helper into each of its callers counts it twice.
//
// The Before: one public entry point whose branching mostly lives in a private helper only it
// calls. Declared, the entry point is small — 8 lines of loop and one call. Over its call path it
// is the whole classification ladder, which is what the rule charges.

namespace SampleSolution.Gate.Rules;

public sealed class GateReconcileBeforeService
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
            if (id >= 0) n++;
        }
        return n;
    }

    public IReadOnlyList<string> Log() => _log;

    // The classification ladder. Single caller, so its complexity is Reconcile's complexity.
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
