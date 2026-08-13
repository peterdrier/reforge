using SampleSolution.Camp;

namespace SampleSolution.Reporting;

// Effective-accessibility fixtures. A section is an assembly, so its surface is what the assembly
// exports — nothing here is reachable from another section, so none of it scores as surface. The
// cross-section dependency below is the deliberate exception: coupling is a use, not a declaration.

// Internal service. Its public methods cannot be called from another assembly -> no
// applicationServiceMethod, methodParameterOverflow, or booleanParameter points. It DOES still
// inject Camp's full interface, which must keep scoring as cross-section coupling.
internal sealed class InternalReportService
{
    private readonly ICampSectionService _camp;
    public InternalReportService(ICampSectionService camp) => _camp = camp;
    public Task<string> RenderAsync(Guid id, bool verbose, string format) => Task.FromResult("");
}

// Internal DTO -> no publicDtoType / dtoScalarProperty points.
internal sealed class InternalReportInfo
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

// Public nested inside internal: effective accessibility is internal, so PayloadInfo is not
// surface either. This is the case a declared-accessibility check gets wrong.
internal static class ReportEnvelope
{
    public sealed class PayloadInfo
    {
        public Guid Id { get; set; }
        public string Body { get; set; } = "";
    }
}

// Internal interface with exactly one implementation -> no oneImplementationInterface points:
// an abstraction nobody outside the assembly can name is an implementation choice.
internal interface IInternalReportSink { Task WriteAsync(string body, CancellationToken ct = default); }

internal sealed class InternalReportSink : IInternalReportSink
{
    public Task WriteAsync(string body, CancellationToken ct = default) => Task.CompletedTask;
}

// An internal read-service interface. It scores no surface, so it must also NOT become a
// conservation anchor — otherwise deleting one of its methods later reads as capability
// evaporation against a baseline, for surface no consumer could reach.
internal interface IInternalDigestServiceRead
{
    Task<string> GetDigestAsync(Guid id, CancellationToken ct = default);
}

// C# 8 lets an EXPORTED interface declare non-public members. An input object reachable only
// through one of those is not on any boundary an external consumer can call, so it must not earn
// parameterBagInput points — the type-level gate alone would wrongly admit it.
public interface IReportExporter
{
    Task ExportAsync(Guid id, CancellationToken ct = default);

    private static string Describe(HiddenExportInput input) => input.Name;
}

public sealed class HiddenExportInput
{
    public HiddenExportInput(Guid id, string name, int page, bool draft)
    {
        Id = id;
        Name = name;
        Page = page;
        Draft = draft;
    }

    public Guid Id { get; }
    public string Name { get; }
    public int Page { get; }
    public bool Draft { get; }
}
