using Microsoft.CodeAnalysis;

namespace Reforge;

/// <summary>
/// Size of one section's test corpus. Non-blank lines, files, and the number of test projects that
/// resolved to the section, plus the same LOC as a percentage of the section's production LOC.
/// </summary>
/// <remarks>
/// A size column, not a score. Issue #37 ordered a test column after a test <i>axis</i> (#36) on the
/// assumption the column would carry points; the measurement in
/// <c>docs/superpowers/specs/2026-08-20-test-axis-measurement.md</c> found nothing in the corpus for
/// those rules to charge, and the comparison the column is actually for ("Shifts carries 3x the test
/// mass of Camps") is a ratio of sizes. So this reports size and stays out of every total.
/// </remarks>
public sealed record TestMass(int Loc, int Files, int Projects, int LocVsProdPercent)
{
    public static readonly TestMass Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Measures the test corpus the scoring passes never see and attributes it to sections.
/// </summary>
/// <remarks>
/// <para>Test projects are excluded from the classified corpus by construction — everything in one
/// is public, so every surface rule would fire on code no other section can call. That exclusion
/// also means a section's test mass is invisible in a report that otherwise describes its whole
/// size, which is what this fills in.</para>
/// <para>A test project's section comes from its <b>non-test project references</b>, never from its
/// own name: <c>X.Tests</c> and <c>X.IntegrationTests</c> must land in the same place, and a test
/// project named after nothing in particular still tests something. Where the references span
/// several sections the project name breaks the tie, and where nothing breaks it the project is
/// reported unattributed rather than folded into whichever section sorted first.</para>
/// </remarks>
public static class TestMassAnalyzer
{
    /// <summary>
    /// Test mass per section, the solution rollup, and the test projects that could not be
    /// attributed (project name + the reason, for the diagnostic).
    /// </summary>
    public static async Task<(Dictionary<string, TestMass> BySection, TestMass Solution, List<string> Unattributed)>
        AnalyzeAsync(
            Solution solution,
            IReadOnlyList<ClassifiedType> classified,
            Dictionary<string, SectionMetrics> metricsBySection,
            CancellationToken ct)
    {
        // Assembly -> section, read off the classified corpus rather than re-derived: that map is
        // what every score in the report was grouped by, so an attribution computed a second way
        // could disagree with it.
        var sectionByAssembly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classified)
        {
            var assembly = c.Type.ContainingAssembly?.Name;
            if (assembly is not null) sectionByAssembly[assembly] = c.Group;
        }

        var loc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unattributed = new List<string>();
        int solutionLoc = 0, solutionFiles = 0, solutionProjects = 0;

        foreach (var project in solution.Projects)
        {
            if (ct.IsCancellationRequested) break;
            if (!SolutionClassifier.IsTestProject(project)) continue;

            var (projectLoc, projectFiles) = await MeasureAsync(project, ct);
            if (projectFiles == 0) continue;

            solutionLoc += projectLoc;
            solutionFiles += projectFiles;
            solutionProjects++;

            var section = Attribute(project, solution, sectionByAssembly);
            if (section is null)
            {
                unattributed.Add(project.Name);
                continue;
            }

            loc[section] = loc.GetValueOrDefault(section) + projectLoc;
            files[section] = files.GetValueOrDefault(section) + projectFiles;
            projects[section] = projects.GetValueOrDefault(section) + 1;
        }

        var bySection = loc.Keys.ToDictionary(
            s => s,
            s => new TestMass(loc[s], files[s], projects[s], Ratio(loc[s], metricsBySection.GetValueOrDefault(s)?.LocProd ?? 0)),
            StringComparer.OrdinalIgnoreCase);

        int prodLoc = metricsBySection.Values.Sum(m => m.LocProd);
        return (bySection, new TestMass(solutionLoc, solutionFiles, solutionProjects, Ratio(solutionLoc, prodLoc)), unattributed);
    }

    /// <summary>
    /// Test LOC as a percentage of production LOC. 0 when the section has no production LOC — the
    /// ratio is undefined there, and reporting a large number for "tests against nothing" would
    /// read as the opposite of what it is.
    /// </summary>
    private static int Ratio(int testLoc, int prodLoc) =>
        prodLoc == 0 ? 0 : (int)Math.Round(testLoc * 100.0 / prodLoc);

    /// <summary>
    /// Non-blank lines and file count for one test project, excluding generated files (the same
    /// exclusion <see cref="SectionMetricsAnalyzer"/> applies to production code).
    /// </summary>
    private static async Task<(int Loc, int Files)> MeasureAsync(Project project, CancellationToken ct)
    {
        int loc = 0, files = 0;
        foreach (var document in project.Documents)
        {
            if (ct.IsCancellationRequested) break;
            if (document.FilePath is null || GeneratedCode.IsGeneratedFile(document.FilePath)) continue;
            // Build intermediates. This pass walks a project's documents rather than its symbols,
            // so it sees what the SDK adds under obj/ — AssemblyInfo, InternalsVisibleTo shims —
            // which is not test code anyone wrote and would otherwise be counted per project.
            if (IsBuildIntermediate(document.FilePath)) continue;

            var text = await document.GetTextAsync(ct);
            files++;
            foreach (var line in text.Lines)
                if (!string.IsNullOrWhiteSpace(line.ToString())) loc++;
        }
        return (loc, files);
    }

    private static bool IsBuildIntermediate(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The section a test project tests, or null when its references don't say.
    /// </summary>
    /// <remarks>
    /// Candidates are the sections of its non-test project references. One candidate is the answer.
    /// Several — a test project referencing three sections' assemblies — are broken by which
    /// candidate the project's own name names as a run of whole dotted segments, so
    /// <c>Humans.Shifts.IntegrationTests</c> referencing Shifts and Users resolves to Shifts. That
    /// is the name used as a tie-break only, never as the attribution itself.
    /// </remarks>
    private static string? Attribute(Project project, Solution solution, Dictionary<string, string> sectionByAssembly)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in project.ProjectReferences)
        {
            var referenced = solution.GetProject(reference.ProjectId);
            if (referenced is null || SolutionClassifier.IsTestProject(referenced)) continue;
            if (sectionByAssembly.TryGetValue(referenced.AssemblyName, out var section)) candidates.Add(section);
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates.First();

        var named = candidates
            .Where(s => NamesSection(project.Name, s))
            .OrderByDescending(s => s.Length)
            .ToList();
        return named.Count == 0 ? null : named[0];
    }

    /// <summary>
    /// Whether a project name names a section as a run of whole dotted segments —
    /// <c>Humans.Shifts.Tests</c> names <c>Shifts</c>, <c>Humans.Campus.Tests</c> does not name
    /// <c>Camp</c>. A substring test would attribute the second one's entire LOC to a section it
    /// never references by name, and the tie-break has no other check to catch it.
    /// </summary>
    private static bool NamesSection(string projectName, string section) =>
        $".{projectName}.".Contains($".{section}.", StringComparison.OrdinalIgnoreCase);
}
