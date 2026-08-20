namespace SampleSolution.Services;

// Fixtures for the internal-complexity / Pareto-gate scoring. Each class is a deliberate
// "before/after consolidation" shape used by SurfaceScoreTests.

public enum SignupAction { Approve, Refuse, Bail }

public enum GreetingReadShape { Basic, WithHistory, WithStats }

[Flags]
public enum UpdateFlags { None = 0, Name = 1, Email = 2, Status = 4 }

public enum RouteMode { North, South, East }

public enum ThingCreationMode { Self, Voluntell }

public enum WorkflowAction { Submit, Approve, Cancel }

/// <summary>
/// BAD generic dispatcher: three explicit commands collapsed behind one generic-verb method
/// (Apply*) that dispatches on an enum and routes each arm to a distinct member. Declared on
/// an interface, so the smell must be attributed to BOTH the interface and the implementation.
/// Every actionDispatcher surcharge applies (typed selector + generic verb), so this must score
/// strictly higher than RouteService.RouteAsync below, which has the same arm count and neither.
/// </summary>
public interface ISignupWorkflowService
{
    Task ApplyAsync(Guid id, SignupAction action, string? reason = null);
}

public class SignupWorkflowService : ISignupWorkflowService
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
/// A structural dispatcher whose name is NOT a generic verb. Still routes arms to distinct
/// members, so actionDispatcher fires — but without the generic-verb surcharge. The cheap end
/// of the same rule that bills ISignupWorkflowService.ApplyAsync above.
/// </summary>
public class RouteService
{
    public Task RouteAsync(Guid id, RouteMode mode) => mode switch
    {
        RouteMode.North => GoNorthAsync(id),
        RouteMode.South => GoSouthAsync(id),
        RouteMode.East => GoEastAsync(id),
        _ => Task.CompletedTask
    };

    private Task GoNorthAsync(Guid id) => Task.CompletedTask;
    private Task GoSouthAsync(Guid id) => Task.CompletedTask;
    private Task GoEastAsync(Guid id) => Task.CompletedTask;
}

/// <summary>
/// A generic-verb mutation that carries a mode enum but whose body is small and does NOT
/// delegate to distinct members (inline branching). Caught by mutationModeParameter, not by
/// the structural dispatcher rules.
/// </summary>
public class ThingService
{
    private readonly List<string> _names = new();

    public Task CreateThingAsync(Guid id, ThingCreationMode mode)
    {
        var name = "thing";
        if (mode == ThingCreationMode.Self) name = $"self-{id}";
        else if (mode == ThingCreationMode.Voluntell) name = $"vt-{id}";
        _names.Add(name);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A real state-machine entry point: generic verb + action enum + a switch, BUT it validates
/// the current state and uses transition vocabulary. The asymmetric rule must exempt it from
/// ALL dispatcher/mode penalties (it may still draw ordinary complexity).
/// Expected: no actionDispatcher / mutationModeParameter.
/// </summary>
public class WorkflowService
{
    public Task ApplyWorkflowTransitionAsync(Guid id, WorkflowAction action)
    {
        var current = LoadState(id);
        if (!CanTransition(current, action))
            throw new InvalidOperationException("illegal transition");

        var next = action switch
        {
            WorkflowAction.Submit => "Submitted",
            WorkflowAction.Approve => "Approved",
            WorkflowAction.Cancel => "Cancelled",
            _ => current
        };
        ApplyTransition(id, current, next);
        return Task.CompletedTask;
    }

    private string LoadState(Guid id) => "Draft";
    private bool CanTransition(string from, WorkflowAction action) => true;
    private void ApplyTransition(Guid id, string from, string to) { }
}

/// <summary>
/// GOOD consolidation: several include-shape read methods collapsed into one GetGreetingsAsync
/// taking a read-shape enum. The arms share a base query and differ only by projection/order,
/// and the method returns data (a read), so the behavioral gate exempts it.
/// Expected: no dispatcher penalty even though it switches on a parameter.
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
/// BAD god method: deeply nested branching well over the cognitive-complexity threshold.
/// Expected: cognitiveComplexity fires.
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
/// A mutation method whose control flow is driven by a [Flags] enum. Expected: flagsControlFlow
/// fires; actionDispatcher does NOT (no delegation to distinct members),
/// and mutationModeParameter does NOT (flags are owned by flagsControlFlow).
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
