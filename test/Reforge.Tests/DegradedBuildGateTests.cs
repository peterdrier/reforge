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

    /// <summary>
    /// And names <b>only</b> that command. The diagnosis is assembled from two sources — the build
    /// description and the gate's own refusal line — and they used to disagree: running
    /// <c>section-shape</c> on a broken tree opened with "Surface-score is PARTIAL" and then said
    /// "Refusing to print a section-shape result". Two commands in three lines reads as a bug in the
    /// tool rather than a fact about the build, which is the opposite of what a refusal must do.
    ///
    /// <para>The obvious assertion — that the right command appears — passed throughout, because it
    /// did appear, on the second line. Naming a wrong one is the failure worth pinning.</para>
    /// </summary>
    [Theory]
    [InlineData("surface-score", "section-shape")]
    [InlineData("section-shape", "surface-score")]
    public void Refuse_NamesNoCommandOtherThanTheOneAskedFor(string requested, string other)
    {
        var refused = new StringWriter();
        var warned = new StringWriter();

        DegradedBuildGate.Refuse(Degraded(), requested, refused);
        DegradedBuildGate.Warn(Degraded(), requested, warned);

        foreach (var text in new[] { refused.ToString(), warned.ToString() })
        {
            Assert.Contains(requested, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(other, text, StringComparison.OrdinalIgnoreCase);
        }
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

        DegradedBuildGate.Warn(Degraded(diagnostics: new[] { Diag() }), "surface-score", stderr);
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
        DegradedBuildGate.Warn(Degraded(), "surface-score", warnErr);

        Assert.StartsWith("ERROR:", refuseErr.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("WARNING:", warnErr.ToString(), StringComparison.Ordinal);
    }
}
