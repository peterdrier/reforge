using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// A single error-severity compiler diagnostic retained from the workspace compile, so a
/// degraded score can name WHICH errors it hit (file/line/CS-code/message), not just a count.
/// <see cref="Column"/> is captured for fidelity but not currently rendered by any format.
/// </summary>
public sealed record BuildDiagnostic(
    string Id,
    string Severity,
    string Project,
    string File,
    int Line,
    int Column,
    string Message);

/// <summary>
/// Build-health of an analyzed solution. When <see cref="Degraded"/> is true the
/// semantic model is incomplete (the solution did not compile cleanly), so any
/// score computed against it is partial. <see cref="AppearsUnbuilt"/> only flavors
/// the warning wording; it is not the authoritative degraded signal.
/// <see cref="Diagnostics"/> holds the deduped, capped list of the actual errors (empty
/// when not degraded); <see cref="DiagnosticsTruncated"/> is how many were dropped by the cap.
/// </summary>
public sealed record BuildHealth(
    bool Degraded,
    int CompilationErrorCount,
    int UnresolvedReferenceCount,
    bool AppearsUnbuilt)
{
    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; init; } = Array.Empty<BuildDiagnostic>();
    public int DiagnosticsTruncated { get; init; }
}

/// <summary>
/// Assesses whether a solution compiled cleanly. surface-score relies on a complete
/// semantic model; an unbuilt/erroring solution silently under-counts cross-project
/// rules (DI registration, cross-section service/interface, entity-return). This
/// inspector surfaces that state, and retains the actual error diagnostics so a degraded
/// score can name which errors it hit — not just how many.
/// </summary>
public static class BuildInspector
{
    // Canonical "didn't build / didn't restore" diagnostic codes:
    // CS0246 type-or-namespace not found, CS0234 name not in namespace,
    // CS0012 type defined in an unreferenced assembly.
    private static readonly HashSet<string> UnresolvedReferenceCodes =
        new(StringComparer.Ordinal) { "CS0246", "CS0234", "CS0012" };

    /// <summary>
    /// Inspects every project's compilation plus the on-disk build-artifact probe.
    /// Reuses Roslyn's per-project compilation cache, so calling this after the
    /// scoring passes adds no meaningful compilation cost. <paramref name="maxDiagnostics"/>
    /// caps the retained per-error list (0 = unlimited); the counts are never capped.
    /// </summary>
    public static async Task<BuildHealth> InspectAsync(Solution solution, int maxDiagnostics, CancellationToken ct)
    {
        var byProject = new List<(Compilation Compilation, string Project)>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null) byProject.Add((compilation, project.Name));
        }

        var (errors, unresolved) = CountErrors(byProject.Select(x => x.Compilation), ct);
        var appearsUnbuilt = AppearsUnbuilt(solution.Projects.Select(p => p.FilePath));

        var solutionDir = LocationHelper.GetSolutionDirectory(solution);
        var (diagnostics, truncated) = CollectDiagnostics(byProject, solutionDir, maxDiagnostics, ct);

        return new BuildHealth(errors > 0, errors, unresolved, appearsUnbuilt)
        {
            Diagnostics = diagnostics,
            DiagnosticsTruncated = truncated
        };
    }

    /// <summary>
    /// Retains the error-severity diagnostics (the detail the counts throw away) across the
    /// given per-project compilations, mapped to solution-relative <see cref="BuildDiagnostic"/>s,
    /// then deduped + capped via <see cref="DedupAndCap"/>.
    /// </summary>
    internal static (IReadOnlyList<BuildDiagnostic> Diagnostics, int Truncated) CollectDiagnostics(
        IEnumerable<(Compilation Compilation, string Project)> compilations,
        string solutionDirectory,
        int max,
        CancellationToken ct)
    {
        var raw = new List<BuildDiagnostic>();
        foreach (var (compilation, project) in compilations)
            foreach (var d in compilation.GetDiagnostics(ct))
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                raw.Add(ToBuildDiagnostic(d, project, solutionDirectory));
            }
        return DedupAndCap(raw, max);
    }

    private static BuildDiagnostic ToBuildDiagnostic(Diagnostic d, string project, string solutionDirectory)
    {
        string file = "";
        int line = 0, column = 0;
        if (d.Location.IsInSource)
        {
            var span = d.Location.GetLineSpan();
            file = LocationHelper.NormalizePath(span.Path, solutionDirectory);
            line = span.StartLinePosition.Line + 1;     // 0-based -> 1-based
            column = span.StartLinePosition.Character + 1;
        }
        return new BuildDiagnostic(d.Id, d.Severity.ToString(), project, file, line, column, d.GetMessage());
    }

    /// <summary>
    /// Dedups identical diagnostics by (Id + file + line + message), orders deterministically
    /// by (project, file, line), and caps the result at <paramref name="max"/> (0 or negative =
    /// unlimited). Returns the kept list plus how many were dropped by the cap.
    /// </summary>
    internal static (IReadOnlyList<BuildDiagnostic> Diagnostics, int Truncated) DedupAndCap(
        IEnumerable<BuildDiagnostic> diagnostics, int max)
    {
        // Structural tuple key dedups on exactly (Id, file, line, message) — no delimiter,
        // so no field value can forge a match across a separator boundary.
        var seen = new HashSet<(string Id, string File, int Line, string Message)>();
        var deduped = new List<BuildDiagnostic>();
        foreach (var d in diagnostics)
        {
            if (seen.Add((d.Id, d.File, d.Line, d.Message)))
                deduped.Add(d);
        }

        deduped.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Project, b.Project);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.File, b.File);
            if (c != 0) return c;
            return a.Line.CompareTo(b.Line);
        });

        if (max <= 0 || deduped.Count <= max)
            return (deduped, 0);

        return (deduped.Take(max).ToList(), deduped.Count - max);
    }

    /// <summary>Counts error-severity diagnostics across the given compilations, and the unresolved-reference subset.</summary>
    internal static (int errors, int unresolved) CountErrors(IEnumerable<Compilation> compilations, CancellationToken ct)
    {
        int errors = 0, unresolved = 0;
        foreach (var compilation in compilations)
        {
            foreach (var d in compilation.GetDiagnostics(ct))
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                errors++;
                if (UnresolvedReferenceCodes.Contains(d.Id)) unresolved++;
            }
        }
        return (errors, unresolved);
    }

    /// <summary>
    /// Best-effort: true when at least one project path is inspectable and none show
    /// build artifacts (any <c>*.cs</c> under a sibling <c>obj/</c>). Unknown/unreadable
    /// paths contribute nothing. All-unknown returns false (can't tell).
    /// </summary>
    internal static bool AppearsUnbuilt(IEnumerable<string?> projectFilePaths)
    {
        bool anyInspectable = false;
        foreach (var path in projectFilePaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var dir = Path.GetDirectoryName(path);
            if (dir is null) continue;
            var objDir = Path.Combine(dir, "obj");

            bool looksBuilt;
            try
            {
                looksBuilt = Directory.Exists(objDir)
                    && Directory.EnumerateFiles(objDir, "*.cs", SearchOption.AllDirectories).Any();
            }
            catch
            {
                continue; // unreadable -> unknown, skip
            }

            anyInspectable = true;
            if (looksBuilt) return false; // at least one project built -> not "unbuilt"
        }
        return anyInspectable; // saw projects, none built -> appears unbuilt
    }

    /// <summary>Human/agent-facing one-line description of a degraded build. Pure; no I/O.</summary>
    public static string DescribeDegraded(BuildHealth h)
    {
        var counts = $"{h.CompilationErrorCount} compile error(s), {h.UnresolvedReferenceCount} unresolved reference(s)";
        return h.AppearsUnbuilt
            ? $"Solution appears unbuilt ({counts}). Surface-score is PARTIAL: cross-section/DI/entity rules under-count. Run `dotnet build` first, then re-run."
            : $"Solution did not compile cleanly ({counts}). Surface-score is PARTIAL: cross-section/DI/entity rules may under-count.";
    }
}
