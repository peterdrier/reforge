using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Reforge.Tests;

/// <summary>
/// Scores one Gate 1 fixture variant as a solution of its own.
///
/// <para>The Gate 1 harness used to score the sample solution once and reconstruct each variant's
/// total by filtering the report to that variant's file. That reconstruction is exact only for
/// rules that charge a declaration for what the declaration says, and silently wrong for every
/// rule that charges a <i>section</i> for its shape — the score stopped being a property of the
/// thing being scored. Three separate ways it went wrong are recorded on issue #26; all three are
/// the same mistake, which is why the fix is to stop reconstructing and start measuring.</para>
///
/// <para>A variant compiled alone is its own section, so a section rule fires against the variant
/// that caused it and nothing else, and the report's own total <i>is</i> the variant's score. No
/// filter, nothing to attribute, no way for one fixture to move another's number.</para>
///
/// <para>Uses <see cref="AdhocWorkspace"/> rather than MSBuild: there is no project file to build
/// here, and a variant is a handful of source files against the running runtime's reference set.
/// The section name still resolves the same way — a single assembly named
/// <c>SampleSolution.Gate</c> folds to section <c>Gate</c>, exactly as it does inside the full
/// solution — so config policy applies identically and the isolation changes only what the harness
/// can see, not what the engine decides.</para>
///
/// <para><b>A variant may span sections.</b> Sections are assembly-derived, so a one-project variant
/// can never fire a cross-section rule — the caller and the dependency are always in the same
/// section by construction. That would have made <c>crossSectionRepository</c> and friends
/// permanently unfixturable and the Gate 1 backlog impossible to finish, which is a defect in the
/// harness rather than a property of those rules. So a variant's satellite files
/// (<c>&lt;stem&gt;.&lt;Section&gt;.cs</c> beside it) each become a project of their own,
/// <c>SampleSolution.&lt;Section&gt;</c>, referenced by the primary one.</para>
/// </summary>
internal static class IsolatedVariantScorer
{
    /// <summary>
    /// Every assembly the test host itself is running against. A fixture is BCL-only by the rule in
    /// the fixtures' README, so the running runtime's reference set is exactly the right one — and
    /// deriving it from the host means it cannot drift from the sample solution's target framework
    /// the way a hand-listed set would.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(() =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    /// <summary>
    /// What <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c> generates for a library — the
    /// sample projects all enable it, so a variant compiled without these would fail on code that
    /// builds fine in the solution, and the harness would report a fixture bug that isn't one.
    /// </summary>
    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    private static readonly Dictionary<string, Lazy<Task<ScoreReport>>> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    /// <summary>
    /// Scores <paramref name="sourceFile"/> compiled by itself. Cached per file: every Gate 1 test
    /// asks about the same immutable variants, and compiling each once instead of once per test is
    /// the difference between four compilations and sixteen.
    /// </summary>
    public static Task<ScoreReport> ScoreAsync(string sourceFile, string assemblyName, string solutionDirectory)
    {
        Lazy<Task<ScoreReport>> entry;
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(sourceFile, out entry!))
            {
                var path = sourceFile;
                entry = new Lazy<Task<ScoreReport>>(() => ScoreUncachedAsync(path, assemblyName, solutionDirectory));
                Cache[sourceFile] = entry;
            }
        }
        return entry.Value;
    }

    /// <summary>
    /// The satellite files of <paramref name="primaryFile"/>, keyed by the section each one declares:
    /// <c>crossSectionRepository.Before.Camp.cs</c> beside <c>crossSectionRepository.Before.cs</c>
    /// declares section <c>Camp</c>. Only a single bare identifier counts as a section, so
    /// <c>.GoodFix.cs</c> and other variants of the same label are not mistaken for satellites of
    /// each other.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SatellitesOf(string primaryFile)
    {
        var directory = Path.GetDirectoryName(primaryFile)!;
        var stem = Path.GetFileNameWithoutExtension(primaryFile); // "<label>.<variant>"
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var candidate in Directory.EnumerateFiles(directory, stem + ".*.cs"))
        {
            var name = Path.GetFileName(candidate);
            // The primary file itself can come back from the pattern on some platforms; it has
            // nothing between the stem and the extension, so the length check drops it.
            if (name.Length <= stem.Length + 1 + ".cs".Length) continue;
            var middle = name[(stem.Length + 1)..^".cs".Length];
            if (middle.Contains('.')) continue;
            if (!middle.All(ch => char.IsLetterOrDigit(ch) || ch == '_')) continue;
            found[middle] = candidate;
        }
        return found;
    }

    private static async Task<ScoreReport> ScoreUncachedAsync(string sourceFile, string assemblyName, string solutionDirectory)
    {
        var projectDirectory = Path.GetDirectoryName(sourceFile)!;
        // "SampleSolution.Gate" -> "SampleSolution.", so a satellite named Camp becomes
        // SampleSolution.Camp and AssemblySections strips the same shared prefix it strips in the
        // real solution. The sections a variant reports are the ones its file names ask for.
        var prefix = assemblyName[..(assemblyName.LastIndexOf('.') + 1)];

        var satellites = new List<ProjectInfo>();
        foreach (var (section, file) in SatellitesOf(sourceFile))
            satellites.Add(await ProjectAsync(prefix + section, projectDirectory, file, Array.Empty<ProjectReference>()));

        // The primary references every satellite, never the reverse: a cross-section rule charges
        // the consumer, and making the direction one-way is what keeps which side is the consumer
        // unambiguous in a fixture.
        var primary = await ProjectAsync(assemblyName, projectDirectory, sourceFile,
            satellites.Select(s => new ProjectReference(s.Id)).ToList());

        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: Path.Combine(solutionDirectory, "IsolatedVariant.slnx"),
            projects: satellites.Append(primary).ToList());

        using var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(solutionInfo);

        var engine = new SurfaceScoreEngine(SurfaceScoreConfig.Default(), solutionDirectory);
        return await engine.ScoreAsync(solution, CancellationToken.None);
    }

    private static async Task<ProjectInfo> ProjectAsync(
        string assemblyName, string projectDirectory, string sourceFile, IReadOnlyList<ProjectReference> references)
    {
        var projectId = ProjectId.CreateNewId(assemblyName);
        var documents = new List<DocumentInfo>
        {
            Document(projectId, sourceFile, await File.ReadAllTextAsync(sourceFile)),
            // Synthetic, and given a path inside the project so that if it ever did declare a type
            // the type would classify by the same path rules as any other. Named per assembly
            // because two projects sharing a directory would otherwise share the path, and a
            // classification keyed on file path cannot tell the copies apart.
            Document(projectId, Path.Combine(projectDirectory, assemblyName + ".ImplicitUsings.g.cs"), ImplicitUsings),
        };

        return ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: assemblyName,
            assemblyName: assemblyName,
            language: LanguageNames.CSharp,
            filePath: Path.Combine(projectDirectory, assemblyName + ".csproj"),
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            documents: documents,
            projectReferences: references,
            metadataReferences: RuntimeReferences.Value);
    }

    private static DocumentInfo Document(ProjectId projectId, string path, string text)
        => DocumentInfo.Create(
            DocumentId.CreateNewId(projectId),
            name: Path.GetFileName(path),
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(text, System.Text.Encoding.UTF8), VersionStamp.Create(), path)),
            filePath: path);
}
