using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Build-health of an analyzed solution. When <see cref="Degraded"/> is true the
/// semantic model is incomplete (the solution did not compile cleanly), so any
/// score computed against it is partial. <see cref="AppearsUnbuilt"/> only flavors
/// the warning wording; it is not the authoritative degraded signal.
/// </summary>
public sealed record BuildHealth(
    bool Degraded,
    int CompilationErrorCount,
    int UnresolvedReferenceCount,
    bool AppearsUnbuilt);

/// <summary>
/// Assesses whether a solution compiled cleanly. surface-score relies on a complete
/// semantic model; an unbuilt/erroring solution silently under-counts cross-project
/// rules (DI registration, cross-section service/interface, entity-return). This
/// inspector surfaces that state. Counts only - no diagnostic messages are retained.
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
    /// scoring passes adds no meaningful compilation cost.
    /// </summary>
    public static async Task<BuildHealth> InspectAsync(Solution solution, CancellationToken ct)
    {
        var compilations = new List<Compilation>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null) compilations.Add(compilation);
        }

        var (errors, unresolved) = CountErrors(compilations, ct);
        var appearsUnbuilt = AppearsUnbuilt(solution.Projects.Select(p => p.FilePath));
        return new BuildHealth(errors > 0, errors, unresolved, appearsUnbuilt);
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
