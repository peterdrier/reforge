namespace SampleSolution.Tent;

// Repo-backed section missing a primary Info DTO (has repo + read + full, no TentInfo).
public interface ITentRepository { Task FindAsync(Guid id, CancellationToken ct = default); }
public interface ITentServiceRead { Task<bool> ExistsAsync(Guid id, CancellationToken ct = default); }
public interface ITentService { Task PitchAsync(Guid id, CancellationToken ct = default); }
public sealed class TentService : ITentService { public Task PitchAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask; }
