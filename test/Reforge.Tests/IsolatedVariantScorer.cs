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

    private static async Task<ScoreReport> ScoreUncachedAsync(string sourceFile, string assemblyName, string solutionDirectory)
    {
        var projectDirectory = Path.GetDirectoryName(sourceFile)!;
        var projectId = ProjectId.CreateNewId(assemblyName);

        var documents = new List<DocumentInfo>
        {
            Document(projectId, sourceFile, await File.ReadAllTextAsync(sourceFile)),
            // Synthetic, and given a path inside the project so that if it ever did declare a type
            // the type would classify by the same path rules as any other.
            Document(projectId, Path.Combine(projectDirectory, "ImplicitUsings.g.cs"), ImplicitUsings),
        };

        var project = ProjectInfo.Create(
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
            metadataReferences: RuntimeReferences.Value);

        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: Path.Combine(solutionDirectory, "IsolatedVariant.slnx"),
            projects: new[] { project });

        using var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(solutionInfo);

        var engine = new SurfaceScoreEngine(SurfaceScoreConfig.Default(), solutionDirectory);
        return await engine.ScoreAsync(solution, CancellationToken.None);
    }

    private static DocumentInfo Document(ProjectId projectId, string path, string text)
        => DocumentInfo.Create(
            DocumentId.CreateNewId(projectId),
            name: Path.GetFileName(path),
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(text, System.Text.Encoding.UTF8), VersionStamp.Create(), path)),
            filePath: path);
}
