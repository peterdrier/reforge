using SampleSolution.Lodge.Contracts;

namespace SampleSolution.Lodge;

// Repo-backed section missing a READ interface (has repo + full write service, no *ServiceRead).
// Its published read DTO lives under Contracts/ — see LodgeContracts.cs.
public interface ILodgeRepository { Task<LodgeStayInfo?> FindAsync(Guid id, CancellationToken ct = default); }
public interface ILodgeService { Task RenameAsync(Guid id, string name, CancellationToken ct = default); }
public sealed class LodgeService : ILodgeService { public Task RenameAsync(Guid id, string name, CancellationToken ct = default) => Task.CompletedTask; }
