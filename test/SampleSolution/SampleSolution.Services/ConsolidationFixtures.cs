namespace SampleSolution.Services;

// Fixtures for the internal-complexity / Pareto-gate scoring. Each class is a deliberate
// "before/after consolidation" shape used by SurfaceScoreTests.

public enum SignupAction { Approve, Refuse, Bail }

public enum GreetingReadShape { Basic, WithHistory, WithStats }

[Flags]
public enum UpdateFlags { None = 0, Name = 1, Email = 2, Status = 4 }

/// <summary>
/// BAD consolidation: three explicit commands (Approve/Refuse/Bail) collapsed behind one
/// generic ApplyAsync that dispatches on an enum and routes each arm to a distinct member.
/// The method mutates (command shape, returns Task), so the behavioral gate does NOT exempt
/// it. Expected: actionDispatcher fires.
/// </summary>
public class SignupWorkflowService
{
    public Task ApplyAsync(Guid id, SignupAction action, string? reason = null) => action switch
    {
        SignupAction.Approve => ApproveAsync(id),
        SignupAction.Refuse => RefuseAsync(id, reason),
        SignupAction.Bail => BailAsync(id),
        _ => Task.CompletedTask
    };

    private Task ApproveAsync(Guid id) => Task.CompletedTask;
    private Task RefuseAsync(Guid id, string? reason) => Task.CompletedTask;
    private Task BailAsync(Guid id) => Task.CompletedTask;
}

/// <summary>
/// GOOD consolidation: several include-shape read methods collapsed into one GetGreetingsAsync
/// taking a read-shape enum. The arms share a base query and differ only by projection/order,
/// and the method returns data (a read), so the behavioral gate exempts it.
/// Expected: actionDispatcher does NOT fire even though it switches on a parameter.
/// </summary>
public class GreetingQueryService
{
    private readonly List<(int UserId, string Text, DateTime At)> _all = new();

    public IReadOnlyList<string> GetGreetingsAsync(int userId, GreetingReadShape shape)
    {
        var query = _all.Where(g => g.UserId == userId);
        return shape switch
        {
            GreetingReadShape.Basic => query.Select(g => g.Text).ToList(),
            GreetingReadShape.WithHistory => query.OrderBy(g => g.At).Select(g => g.Text).ToList(),
            GreetingReadShape.WithStats => query.Select(g => $"{g.Text} ({g.At:o})").ToList(),
            _ => new List<string>()
        };
    }
}

/// <summary>
/// BAD god method: deeply nested branching well over the cognitive-complexity threshold and
/// over the long-method LOC threshold. Expected: longMethod AND cognitiveComplexity fire.
/// </summary>
public class ReportBuilder
{
    public string BuildEverything(IReadOnlyList<int> ids, bool verbose, int mode)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var id in ids)
        {
            if (id < 0)
            {
                continue;
            }

            if (id % 2 == 0 && verbose)
            {
                for (int i = 0; i < id; i++)
                {
                    if (i % 3 == 0)
                    {
                        while (i > 0)
                        {
                            if (i > 100)
                            {
                                sb.Append('x');
                            }
                            else if (i > 50)
                            {
                                sb.Append('y');
                            }
                            else
                            {
                                sb.Append('z');
                            }

                            i--;
                        }
                    }
                    else if (i % 5 == 0)
                    {
                        sb.Append('q');
                    }
                }
            }
            else
            {
                switch (mode)
                {
                    case 0:
                        sb.Append('a');
                        break;
                    case 1:
                        sb.Append('b');
                        break;
                    case 2 when verbose:
                        sb.Append('c');
                        break;
                    default:
                        sb.Append('d');
                        break;
                }
            }

            if (sb.Length > 1000 || (verbose && sb.Length > 500))
            {
                sb.Clear();
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// A mutation method whose control flow is driven by a [Flags] enum. The branches don't
/// delegate to distinct members (so it isn't an action dispatcher), but it tests flags to
/// decide what to mutate. Expected: flagsControlFlow fires; actionDispatcher does NOT.
/// </summary>
public class FlagUpdateService
{
    public Task UpdateAsync(Guid id, UpdateFlags flags)
    {
        var changes = new List<string>();
        if (flags.HasFlag(UpdateFlags.Name)) changes.Add("name");
        if (flags.HasFlag(UpdateFlags.Email)) changes.Add("email");
        if (flags.HasFlag(UpdateFlags.Status)) changes.Add("status");
        return Task.CompletedTask;
    }
}
