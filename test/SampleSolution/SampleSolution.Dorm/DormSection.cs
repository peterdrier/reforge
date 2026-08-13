namespace SampleSolution.Dorm;

// Repo-backed section missing a WRITE/full interface (has repo + read, no full service).
// No caching decorator either, so its cache DTO must default to the primary DormInfo.
public interface IDormRepository { Task<DormInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface IDormServiceRead { Task<DormInfo> GetByIdAsync(Guid id, CancellationToken ct = default); }
public sealed class DormInfo { public Guid Id { get; set; } public string Name { get; set; } = ""; }
