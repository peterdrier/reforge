namespace SampleSolution.Lodge;

// Repo-backed section missing a READ interface (has repo + full write service, no *ServiceRead).
public interface ILodgeRepository { Task<LodgeInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface ILodgeService { Task RenameAsync(Guid id, string name, CancellationToken ct = default); }
public sealed class LodgeService : ILodgeService { public Task RenameAsync(Guid id, string name, CancellationToken ct = default) => Task.CompletedTask; }
public sealed class LodgeInfo { public Guid Id { get; set; } public string Name { get; set; } = ""; }
