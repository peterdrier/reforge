using SampleSolution.Camp;
using SampleSolution.Camp.Contracts;

namespace SampleSolution.Reporting;

// A section with NO repository — must not trip any missing* rule — whose consumers reach across
// the assembly boundary into Camp's write surface.

// Cross-section caller that injects the Camp full interface but only READS -> crossSectionWriteSurface.
public sealed class CampReportBuilder
{
    private readonly ICampSectionService _camp;
    public CampReportBuilder(ICampSectionService camp) => _camp = camp;
    public async Task<string> BuildAsync(Guid id)
    {
        var info = await _camp.GetByIdAsync(id);   // read-covered (exists on ICampServiceRead)
        return info.Name;
    }
}

// Cross-section caller that PASSES the injected dependency onward -> unknown usage (advisory, not penalty).
public sealed class CampDelegator
{
    private readonly ICampSectionService _camp;
    public CampDelegator(ICampSectionService camp) => _camp = camp;
    public Task HandOffAsync() => Consume(_camp);            // escapes: passed as an argument
    private static Task Consume(ICampServiceRead svc) => Task.CompletedTask;
}

// Return-type fixtures for the derived canonical read DTO set. All three return types belong to
// the Camp section, so all three cross a section boundary; only the two exported from Camp's
// contracts assembly are its published read API.
public sealed class CampFeedReader
{
    public Task<CampInfo> GetCampAsync(Guid id) => Task.FromResult(new CampInfo());          // canonical -> credit
    public Task<CampStayEntity> GetStayAsync(Guid id) => Task.FromResult(new CampStayEntity()); // canonical AND entity-classified -> credit, no leak penalty
    public Task<CampLegacyEntity> GetLegacyAsync(Guid id) => Task.FromResult(new CampLegacyEntity()); // off the contracts surface -> entity leak
}

// Orchestrator-only, no repository — must NOT trip any missing* rule.
public interface IBookingOrchestrator { Task RunAsync(CancellationToken ct = default); }
public sealed class BookingOrchestrator : IBookingOrchestrator
{
    private readonly ICampSectionService _camp;
    public BookingOrchestrator(ICampSectionService camp) => _camp = camp;
    public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;
}
