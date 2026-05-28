namespace SampleSolution.Services;

// Fixtures for the boundary-input scoring rules. The "bad" shapes mirror the Humans Camps
// parameter-bag refactor that gamed methodParameterOverflow.

public interface ICampService
{
    Task CreateCampAsync(CampRegistrationInput input, CancellationToken ct = default);
}

public sealed class CampService : ICampService
{
    public Task CreateCampAsync(CampRegistrationInput input, CancellationToken ct = default) => Task.CompletedTask;

    // Inline parameter-object construction: the same argument bundle, now built at the call site.
    public Task RegisterAsync(Guid userId, string name, string email, string phone, bool isSwiss, int year)
        => CreateCampAsync(new CampRegistrationInput(userId, name, email, phone, isSwiss, year));
}

/// <summary>
/// BAD: a public boundary input that hides all its state behind internal getters and adds no
/// behavior — a long signature folded into an object. Expected: publicInputWithHiddenState
/// AND parameterBagInput; and inlineParameterObjectConstruction at the call site above.
/// </summary>
public sealed class CampRegistrationInput
{
    public CampRegistrationInput(Guid createdByUserId, string name, string email, string phone, bool isSwiss, int year)
    {
        CreatedByUserId = createdByUserId;
        Name = name;
        Email = email;
        Phone = phone;
        IsSwiss = isSwiss;
        Year = year;
    }

    internal Guid CreatedByUserId { get; }
    internal string Name { get; }
    internal string Email { get; }
    internal string Phone { get; }
    internal bool IsSwiss { get; }
    internal int Year { get; }
}

public interface ICampRequestService
{
    Task SubmitAsync(CampRegistrationRequest request, CancellationToken ct = default);
}

public sealed class CampRequestService : ICampRequestService
{
    public Task SubmitAsync(CampRegistrationRequest request, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// GOOD: public readable state and real validation behavior. Must NOT be penalized by the
/// boundary-input rules even though its name ends in "Request" and it has several members.
/// </summary>
public sealed record CampRegistrationRequest(
    Guid CreatedByUserId,
    string Name,
    string ContactEmail,
    string ContactPhone,
    int Year)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Name is required.", nameof(Name));
    }
}
