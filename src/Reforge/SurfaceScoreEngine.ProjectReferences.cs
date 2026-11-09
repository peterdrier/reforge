using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Reforge;

// Project references: what a production project's compilation needs from the rest of the solution,
// and the references its csproj declares that it does not need.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Charges <c>projectReference</c> once per other section whose assembly the project's
    /// compilation uses, unless a constructor-dependency rule already charges that section pair,
    /// and <c>unusedProjectReference</c> per <c>&lt;ProjectReference&gt;</c> in its csproj whose
    /// assembly the compilation does not use.
    /// </summary>
    /// <remarks>
    /// <para>"Uses" is the compiler's own answer, <see cref="Compilation.GetUsedAssemblyReferences"/>:
    /// the references binding needed, the set an IDE's remove-unused-references offers to keep, plus
    /// what the project's Razor views name, since views are not in the workspace's compilation. An
    /// application's references are never judged unused: a host that discovers its modules at run
    /// time references them only to ship them.</para>
    /// <para><c>projectReference</c> is a floor on what reaching another section costs, not a
    /// surcharge: an edge that already pays <c>crossSection*</c> pays nothing more, so the trade
    /// those rules are balanced against (copying the logic to drop the dependency) stays unprofitable.
    /// An edge they do not see (static helpers, DTOs, base types, enums) pays the floor, about what
    /// a copy costs: the copied method's own charges plus the orphaned export's
    /// <c>exportedUnusedExternally</c>.</para>
    /// <para>It follows use, not declaration, so it cannot be dodged by declaration: dropping a
    /// direct reference and reaching the same assembly transitively pays the same, and a redundant
    /// direct reference to an assembly in use costs nothing extra. It is per target <i>section</i>,
    /// so a project reaching both <c>X</c> and <c>X.Contracts</c> pays once, and a reference into the
    /// project's own section (<c>X</c> to <c>X.Contracts</c>) is free.</para>
    /// <para>Test projects are not charged: they reference the code under test by construction, and
    /// nothing in a test project may move the main score.</para>
    /// </remarks>
    private async Task ScoreProjectReferencesAsync(Solution solution, ScoreReport report,
        HashSet<(string From, string To)> pricedSectionPairs, CancellationToken ct)
    {
        var refWeight = _config.Weight("projectReference");
        var unusedWeight = _config.Weight("unusedProjectReference");
        if (refWeight == 0 && unusedWeight == 0) return;

        var solutionAssemblies = new HashSet<string>(solution.Projects.Select(p => p.AssemblyName), StringComparer.Ordinal);
        var unused = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (SolutionClassifier.IsTestProject(project)) continue;
            // A multi-targeted project loads once per target framework; score it once.
            if (!seen.Add(project.FilePath ?? project.AssemblyName)) continue;
            if (!report.SectionByAssembly.TryGetValue(project.AssemblyName, out var section)) continue;
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in compilation.GetUsedAssemblyReferences(ct))
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol { Name: var name }
                    && name != project.AssemblyName && solutionAssemblies.Contains(name))
                    used.Add(name);

            var csproj = project.FilePath is null ? "" : LocationHelper.NormalizePath(project.FilePath, _solutionDirectory);
            var declared = DeclaredProjectReferences(project, solution);
            // Razor views are not in the workspace's compilation, so what only a view names is
            // invisible to the compiler's answer; read the views for it.
            if (declared.Any(d => !used.Contains(d.Assembly)) && RazorNames(project) is { Count: > 0 } razorNames)
                foreach (var d in declared.Where(d => !used.Contains(d.Assembly)).ToList())
                    if (NamedIn(razorNames, compilation, d.Assembly))
                        used.Add(d.Assembly);
            // An application's references also decide what ships beside it, and a host that
            // discovers its modules at run time names none of them; the compiler cannot judge those.
            bool judgesUnused = compilation.Options.OutputKind is OutputKind.DynamicallyLinkedLibrary;

            foreach (var target in used
                         .GroupBy(SectionOf, StringComparer.OrdinalIgnoreCase)
                         .Where(g => !string.Equals(g.Key, section, StringComparison.OrdinalIgnoreCase)
                                     && !pricedSectionPairs.Contains((section, g.Key)))
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var direct = declared.FirstOrDefault(d => target.Contains(d.Assembly, StringComparer.Ordinal));
                AddEntryByName(report, section, "projectReference", refWeight, target.Key, csproj, direct.Line,
                    direct.Assembly is null ? $"{project.Name} -> {target.Key} (transitive)" : $"{project.Name} -> {target.Key}");
            }

            foreach (var d in declared.Where(d => judgesUnused && !used.Contains(d.Assembly)))
            {
                AddEntryByName(report, section, "unusedProjectReference", unusedWeight, d.Name, csproj, d.Line,
                    $"{project.Name} -> {d.Name}: nothing in {project.Name} uses it");
                if (unusedWeight != 0) unused.Add($"{csproj}:{d.Line} -> {d.Name}");
            }
        }

        if (unused.Count > 0)
            report.Diagnostics.Add(new ScoreDiagnostic("info", "unusedProjectReference",
                $"ProjectReferences nothing in the referencing project uses: {string.Join(", ", unused)}"));

        string SectionOf(string assembly) => report.SectionByAssembly.GetValueOrDefault(assembly) ?? assembly;
    }

    /// <summary>
    /// The <c>&lt;ProjectReference&gt;</c> items written in the project's own csproj that the
    /// workspace also loaded (so an inactive conditional reference is not judged) and that reference
    /// an assembly (so an analyzer or build-order-only reference is not). References brought in by a
    /// <c>Directory.Build.props</c> are not the project's own declaration and are left out.
    /// </summary>
    internal static List<DeclaredProjectReference> DeclaredProjectReferences(Project project, Solution solution)
    {
        var result = new List<DeclaredProjectReference>();
        if (project.FilePath is not { } path || !File.Exists(path)) return result;

        XDocument doc;
        try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
        catch (System.Xml.XmlException) { return result; }

        var loaded = new Dictionary<string, Project>(PathComparer);
        foreach (var reference in project.ProjectReferences)
            if (solution.GetProject(reference.ProjectId) is { FilePath: { } targetPath } target)
                loaded.TryAdd(Path.GetFullPath(targetPath), target);
        var dir = Path.GetDirectoryName(path)!;

        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            if (Setting(element, "ReferenceOutputAssembly") is { } roa && roa.Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
            if (Setting(element, "OutputItemType") is { } oit && oit.Contains("Analyzer", StringComparison.OrdinalIgnoreCase)) continue;
            var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            foreach (var include in (element.Attribute("Include")?.Value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (include.Contains("$(", StringComparison.Ordinal)) continue;
                var full = Path.GetFullPath(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
                if (loaded.TryGetValue(full, out var target))
                    result.Add(new DeclaredProjectReference(target.Name, target.AssemblyName, line));
            }
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex DottedName =
        new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Every identifier, dotted name and dotted-name prefix in the project's <c>.cshtml</c> and
    /// <c>.razor</c> files (bin, obj and nested projects skipped). Empty for a project without views.
    /// </summary>
    private static HashSet<string> RazorNames(Project project)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (Path.GetDirectoryName(project.FilePath) is not { } root || !Directory.Exists(root)) return names;

        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var leaf = Path.GetFileName(sub);
                if (leaf is "bin" or "obj" || leaf.StartsWith('.') || Directory.EnumerateFiles(sub, "*.csproj").Any()) continue;
                pending.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (System.Text.RegularExpressions.Match m in DottedName.Matches(File.ReadAllText(file)))
                {
                    var parts = m.Value.Split('.');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        names.Add(parts[i]);
                        names.Add(string.Join('.', parts, 0, i + 1));
                    }
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Whether a view names <paramref name="assembly"/>: its name (<c>@addTagHelper *, X</c>), a
    /// namespace it declares types in, or a non-private type it declares that the referencing
    /// project does not declare a type of the same name itself.
    /// </summary>
    private static bool NamedIn(HashSet<string> razorNames, Compilation compilation, string assembly)
    {
        if (razorNames.Contains(assembly)) return true;
        var target = compilation.References
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .FirstOrDefault(a => a.Name == assembly);
        if (target is null) return false;

        var own = new HashSet<string>(TypesOf(compilation.Assembly.GlobalNamespace).Select(t => t.Name), StringComparer.Ordinal);
        foreach (var type in TypesOf(target.GlobalNamespace))
        {
            if (type.DeclaredAccessibility == Accessibility.Private) continue;
            if (razorNames.Contains(type.ContainingNamespace.ToDisplayString())) return true;
            if (!own.Contains(type.Name) && razorNames.Contains(type.Name)) return true;
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> TypesOf(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
            yield return type;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in TypesOf(child))
                yield return type;
    }

    private static string? Setting(XElement element, string name) =>
        element.Attribute(name)?.Value ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>A <c>&lt;ProjectReference&gt;</c> in a csproj: the referenced project, its assembly, and the line.</summary>
internal readonly record struct DeclaredProjectReference(string Name, string Assembly, int Line);
