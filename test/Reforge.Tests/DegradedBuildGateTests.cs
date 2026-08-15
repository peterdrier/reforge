namespace Reforge.Tests;

/// <summary>
/// The refusal contract for a degraded build. The gate is a pure function over
/// <see cref="BuildHealth"/> writing to an injected <see cref="TextWriter"/>, so the whole
/// contract is testable without a workspace — and, structurally, it cannot write to stdout,
/// which is the property the whole change exists to guarantee.
/// </summary>
public class DegradedBuildGateTests
{
    private static BuildHealth Degraded(int errors = 6, int unresolved = 4, bool unbuilt = false,
        IReadOnlyList<BuildDiagnostic>? diagnostics = null, int truncated = 0) =>
        new(Degraded: true, CompilationErrorCount: errors, UnresolvedReferenceCount: unresolved, AppearsUnbuilt: unbuilt)
        {
            Diagnostics = diagnostics ?? Array.Empty<BuildDiagnostic>(),
            DiagnosticsTruncated = truncated
        };

    private static BuildDiagnostic Diag(string id = "CS0246", string file = "src/Foo.cs", int line = 42,
        string message = "The type or namespace name 'Bar' could not be found", string project = "Humans.Core") =>
        new(id, "Error", project, file, line, 1, message);

    [Fact]
    public void DegradedExitCode_IsTwo_SoABrokenTreeIsDistinguishableFromABrokenTool()
    {
        Assert.Equal(2, DegradedBuildGate.DegradedExitCode);
        Assert.NotEqual(1, DegradedBuildGate.DegradedExitCode);
    }

    [Fact]
    public void Refuse_ReturnsTheDegradedExitCode()
    {
        var stderr = new StringWriter();

        var exitCode = DegradedBuildGate.Refuse(Degraded(), "surface-score", stderr);

        Assert.Equal(DegradedBuildGate.DegradedExitCode, exitCode);
    }

    [Fact]
    public void Refuse_NamesTheErrorAndUnresolvedCounts()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(errors: 3723, unresolved: 2024), "surface-score", stderr);
        var text = stderr.ToString();

        Assert.Contains("3723 compile error(s)", text, StringComparison.Ordinal);
        Assert.Contains("2024 unresolved reference(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_NamesTheCommandItIsRefusingFor()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(), "section-shape", stderr);

        Assert.Contains("section-shape", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_ListsEachDiagnosticWithCodeFileLineMessageAndProject()
    {
        var stderr = new StringWriter();
        var health = Degraded(diagnostics: new[]
        {
            Diag(id: "CS0246", file: "src/Foo.cs", line: 42, message: "missing Bar", project: "Humans.Core"),
            Diag(id: "CS0234", file: "src/Baz.cs", line: 7, message: "missing Qux", project: "Humans.Web")
        });

        DegradedBuildGate.Refuse(health, "surface-score", stderr);
        var text = stderr.ToString();

        Assert.Contains("CS0246", text, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs:42", text, StringComparison.Ordinal);
        Assert.Contains("missing Bar", text, StringComparison.Ordinal);
        Assert.Contains("(Humans.Core)", text, StringComparison.Ordinal);

        Assert.Contains("CS0234", text, StringComparison.Ordinal);
        Assert.Contains("src/Baz.cs:7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_ReportsHowManyDiagnosticsTheCapDropped()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(diagnostics: new[] { Diag() }, truncated: 17), "surface-score", stderr);

        Assert.Contains("(+17 more)", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_WithNothingTruncated_SaysNothingAboutTruncation()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(diagnostics: new[] { Diag() }), "surface-score", stderr);

        Assert.DoesNotContain("more)", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch has to be discoverable at the moment of refusal. Someone hitting this in
    /// CI should not have to go read --help to find out how to proceed.
    /// </summary>
    [Fact]
    public void Refuse_PointsAtTheOptOut()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(), "surface-score", stderr);

        Assert.Contains("--allow-degraded", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_OnAnUnbuiltSolution_SaysToBuildFirst()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(unbuilt: true), "surface-score", stderr);

        Assert.Contains("dotnet build", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Warn_SaysTheResultIsPartial_AndStillListsTheDiagnostics()
    {
        var stderr = new StringWriter();

        DegradedBuildGate.Warn(Degraded(diagnostics: new[] { Diag() }), stderr);
        var text = stderr.ToString();

        Assert.Contains("WARNING", text, StringComparison.Ordinal);
        Assert.Contains("PARTIAL", text, StringComparison.Ordinal);
        Assert.Contains("--allow-degraded", text, StringComparison.Ordinal);
        Assert.Contains("CS0246", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refuse is an error, Warn is a continuation. An agent grepping stderr should be able to tell
    /// them apart on the first token.
    /// </summary>
    [Fact]
    public void Refuse_AndWarn_UseDistinctSeverityPrefixes()
    {
        var refuseErr = new StringWriter();
        var warnErr = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(), "surface-score", refuseErr);
        DegradedBuildGate.Warn(Degraded(), warnErr);

        Assert.StartsWith("ERROR:", refuseErr.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("WARNING:", warnErr.ToString(), StringComparison.Ordinal);
    }
}
