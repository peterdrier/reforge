namespace Reforge;

/// <summary>
/// What reforge does when asked to analyze a solution that did not compile.
///
/// <para>Reforge has always known when the build was degraded — <see cref="BuildInspector"/>
/// counts error diagnostics and even retains them with file and line — and then scored anyway: a
/// warning to stderr, an entry in the JSON <c>diagnostics</c> array, exit 0, and a full score on
/// stdout that looks authoritative. An agent running <c>surface-score --format json &gt; out.json</c>
/// never sees stderr and has no reason to read one entry inside a twenty-key document.</para>
///
/// <para>That has produced wrong numbers in a CHANGELOG, a design spec and a PR body. What made
/// them convincing is that two runs against the same broken tree <i>agree with each other</i> —
/// during #14 an A/B on a tree carrying 3,723 compilation errors matched on <c>typesAnalyzed</c>
/// and <c>implementationShapeTotal</c> while reporting <c>canonicalReadDtoReturn</c> at -81 against
/// the clean tree's -162. Corpus agreement is not evidence of soundness; <c>build.degraded</c> is
/// the only reliable check, and nothing forced anyone to make it.</para>
///
/// <para>So the tool refuses instead of asking the operator to remember. Suppressing stdout is the
/// part that does the work: a number that is never printed cannot be pasted into a changelog.</para>
/// </summary>
public static class DegradedBuildGate
{
    /// <summary>
    /// Exit code for "the analyzed solution did not compile". Deliberately distinct from 1 so a
    /// caller can tell a broken tree from a broken tool without parsing output.
    /// </summary>
    public const int DegradedExitCode = 2;

    /// <summary>
    /// Reports the refusal on stderr and returns <see cref="DegradedExitCode"/>. The caller must
    /// write <b>nothing</b> to stdout after this — that suppression is the point of the gate.
    /// </summary>
    public static int Refuse(BuildHealth health, string command, TextWriter stderr)
    {
        stderr.WriteLine($"ERROR: {BuildInspector.DescribeDegraded(health, Subject(command))}");
        stderr.WriteLine($"Refusing to print a {command} result computed from an incomplete semantic model.");
        WriteDiagnostics(health, stderr);
        stderr.WriteLine("Fix the build and re-run, or pass --allow-degraded to analyze anyway (exit 0).");
        return DegradedExitCode;
    }

    /// <summary>
    /// The <c>--allow-degraded</c> path: say plainly that the result is partial, then let the
    /// caller print it and exit 0.
    /// </summary>
    /// <remarks>
    /// The opt-out really does exit 0. A flag that still fails is a flag people route around with
    /// <c>|| true</c>, which suppresses the genuine failures too — worse than having no flag.
    /// </remarks>
    public static void Warn(BuildHealth health, string command, TextWriter stderr)
    {
        stderr.WriteLine($"WARNING: {BuildInspector.DescribeDegraded(health, Subject(command))}");
        stderr.WriteLine("Continuing because --allow-degraded was passed; the result below is PARTIAL.");
        WriteDiagnostics(health, stderr);
    }

    /// <summary>
    /// The command name as it starts a sentence. Derived rather than passed as a second string, so
    /// the two lines of the diagnosis cannot end up naming different commands — which is the defect
    /// this exists to prevent, not a hypothetical.
    /// </summary>
    private static string Subject(string command) =>
        command.Length == 0 ? "This result" : char.ToUpperInvariant(command[0]) + command[1..];

    /// <summary>
    /// Lists the retained errors in the same shape the Compact and Markdown formats use, so the
    /// stderr diagnosis reads identically to the in-report one. The list is already deduped and
    /// capped by <c>--max-build-diagnostics</c>; the counts in the line above are never capped.
    /// </summary>
    private static void WriteDiagnostics(BuildHealth health, TextWriter stderr)
    {
        foreach (var d in health.Diagnostics)
            stderr.WriteLine($"  {d.Id}  {d.File}:{d.Line}  {d.Message}  ({d.Project})");

        if (health.DiagnosticsTruncated > 0)
            stderr.WriteLine($"  (+{health.DiagnosticsTruncated} more)");
    }
}
