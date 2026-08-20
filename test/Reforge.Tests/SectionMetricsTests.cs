using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// Per-section size/complexity metrics reported beside the score (#44). The load-bearing property
/// is that they are <b>informational</b>: adding them may not move a single point, in any format.
/// </summary>
[Collection("SampleSolution")]
public class SectionMetricsTests
{
    private readonly SampleSolutionFixture _fixture;

    public SectionMetricsTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<ScoreReport> ScoreDefaultAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        return await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);
    }

    private static string Capture(Action action)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    // ---------------- Engine ----------------

    [Fact]
    public async Task ScoreAsync_EverySectionGetsMetrics()
    {
        var report = await ScoreDefaultAsync();

        // Every section of the solution is measured, not only the ones that scored.
        Assert.All(report.ConfiguredSections, s => Assert.True(report.MetricsBySection.ContainsKey(s),
            $"section '{s}' has no metrics"));
        // And every scored group carries its section's metrics inline.
        foreach (var g in report.Groups.Values)
            Assert.Same(report.MetricsBySection[g.Name], g.Metrics);
    }

    [Fact]
    public async Task ScoreAsync_SectionMetricsAreNonTrivial()
    {
        var report = await ScoreDefaultAsync();

        var services = report.MetricsBySection["Services"];
        Assert.True(services.LocProd > 0);
        Assert.True(services.Files > 0);
        Assert.True(services.Classes > 0);
        Assert.True(services.Methods > 0);
        Assert.True(services.MaxClassLoc > 0);
        Assert.NotEqual("", services.MaxClassLocName);
    }

    [Fact]
    public async Task ScoreAsync_SectionLocSumsToSolutionLoc()
    {
        var report = await ScoreDefaultAsync();

        // A file belongs to exactly one assembly, so the sections partition the corpus. If this
        // drifts, a file is being counted twice (or dropped) — which would make every per-section
        // LOC comparison wrong in a way no single-section assertion can see.
        Assert.Equal(report.Metrics.LocProd, report.MetricsBySection.Values.Sum(m => m.LocProd));
        Assert.Equal(report.Metrics.Files, report.MetricsBySection.Values.Sum(m => m.Files));
        Assert.Equal(report.Metrics.Classes, report.MetricsBySection.Values.Sum(m => m.Classes));
        Assert.Equal(report.Metrics.Interfaces, report.MetricsBySection.Values.Sum(m => m.Interfaces));
        Assert.Equal(report.Metrics.Methods, report.MetricsBySection.Values.Sum(m => m.Methods));
    }

    [Fact]
    public async Task ScoreAsync_SolutionMaximaAreTheMaximaOfTheSections()
    {
        var report = await ScoreDefaultAsync();

        Assert.Equal(report.MetricsBySection.Values.Max(m => m.Cognitive.Max), report.Metrics.Cognitive.Max);
        Assert.Equal(report.MetricsBySection.Values.Max(m => m.Cyclomatic.Max), report.Metrics.Cyclomatic.Max);
        Assert.Equal(report.MetricsBySection.Values.Max(m => m.MaxClassLoc), report.Metrics.MaxClassLoc);
    }

    [Fact]
    public async Task ScoreAsync_MetricsDoNotAffectScore()
    {
        var report = await ScoreDefaultAsync();

        // The engine's own invariant: the metrics pass adds no entries, so the axes still add up
        // exactly as they did before it existed.
        Assert.Equal(report.SurfaceTotal + report.InternalComplexityTotal, report.Total);
        Assert.Equal(report.Total, report.Groups.Values.Sum(g => g.Total));
        Assert.Equal(report.SurfaceTotal, report.Groups.Values.Sum(g => g.SurfaceTotal));
        Assert.Equal(report.InternalComplexityTotal, report.Groups.Values.Sum(g => g.InternalComplexityTotal));
    }

    [Fact]
    public async Task ScoreAsync_CognitiveMaxMatchesTheMethodItNames()
    {
        var report = await ScoreDefaultAsync();
        var max = report.Metrics.Cognitive;
        Assert.NotEqual("", max.MaxMethod);

        // Re-derive the named method's score straight from syntax: the metric must be the same
        // number the internal-complexity axis charges on, not a parallel approximation of it.
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, SurfaceScoreConfig.Default(),
            LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        var parts = max.MaxMethod.Split('.');
        var recomputed = classified
            .Where(c => c.Type.Name == parts[0])
            .SelectMany(c => c.Type.GetMembers(parts[1]).OfType<Microsoft.CodeAnalysis.IMethodSymbol>())
            .SelectMany(m => m.DeclaringSyntaxReferences)
            .Select(r => r.GetSyntax(CancellationToken.None))
            .OfType<BaseMethodDeclarationSyntax>()
            .Select(ImplementationComplexity.Cognitive)
            .DefaultIfEmpty(0)
            .Max();

        Assert.Equal(max.Max, recomputed);
    }

    // ---------------- Cyclomatic complexity (shared with snapshot) ----------------

    [Fact]
    public async Task ScoreAsync_MaxClassLocNamesAClassNotAnInterface()
    {
        var report = await ScoreDefaultAsync();

        // Camp's largest declaration is an interface. maxClassLoc must describe the same set as
        // `classes` and as the largeClass rule, or the field names a type it does not measure.
        Assert.NotEqual("", report.MetricsBySection["Camp"].MaxClassLocName);

        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, SurfaceScoreConfig.Default(),
            LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        foreach (var (section, metrics) in report.MetricsBySection)
        {
            if (metrics.MaxClassLocName.Length == 0) continue;
            var named = classified.Where(c =>
                c.Group.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                c.Type.Name == metrics.MaxClassLocName).ToList();
            Assert.NotEmpty(named);
            Assert.All(named, c => Assert.True(
                c.Type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Class or Microsoft.CodeAnalysis.TypeKind.Struct,
                $"{section}.maxClassLocName names {metrics.MaxClassLocName}, a {c.Type.TypeKind}"));
        }
    }

    [Fact]
    public void Cyclomatic_CountsEachIndependentBranch()
    {
        // 1 (base) + if + && + foreach + catch + ternary = 6.
        var method = ParseMethod("""
            void M(int[] xs, bool a, bool b)
            {
                if (a && b) { }
                foreach (var x in xs) { }
                try { } catch { }
                var y = a ? 1 : 2;
            }
            """);

        Assert.Equal(6, ImplementationComplexity.Cyclomatic(method));
    }

    [Fact]
    public void Cyclomatic_CountsDeconstructingForeach()
    {
        // `foreach (var (k, v) in xs)` parses as ForEachVariableStatementSyntax — a sibling of
        // ForEachStatementSyntax, not a subtype. Matching only the latter undercounted every
        // deconstructing loop by one, in the section distributions and in `snapshot` alike.
        var deconstructing = ParseMethod(
            "void M(System.Collections.Generic.Dictionary<int, int> xs) { foreach (var (k, v) in xs) { } }");
        var plain = ParseMethod(
            "void M(System.Collections.Generic.Dictionary<int, int> xs) { foreach (var pair in xs) { } }");

        Assert.Equal(2, ImplementationComplexity.Cyclomatic(plain));
        Assert.Equal(ImplementationComplexity.Cyclomatic(plain), ImplementationComplexity.Cyclomatic(deconstructing));
    }

    [Fact]
    public void Cyclomatic_StraightLineMethodIsOne()
    {
        Assert.Equal(1, ImplementationComplexity.Cyclomatic(ParseMethod("void M() { var x = 1; }")));
    }

    [Fact]
    public void Cyclomatic_BodylessDeclarationIsZeroNotOne()
    {
        // An abstract declaration carries no implementation. Scoring it 1 would drag a section's
        // average toward 1 in proportion to how many interfaces it declares.
        Assert.Equal(0, ImplementationComplexity.Cyclomatic(ParseMethod("abstract void M();")));
    }

    private static BaseMethodDeclarationSyntax ParseMethod(string source)
    {
        var tree = CSharpSyntaxTree.ParseText($"abstract class C {{ {source} }}");
        return tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().First();
    }

    // ---------------- Generated-code exclusion ----------------

    [Fact]
    public async Task Analyze_FiltersGeneratedCodePerDeclarationNotPerPrimaryFile()
    {
        // A partial type is one symbol spanning several files. Deciding generated-ness from the
        // classifier's primary file alone would leak a generated half in (when the handwritten
        // file is primary) or drop the handwritten half (when the generated one is), so the
        // filter runs per declaring syntax reference and per method.
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, SurfaceScoreConfig.Default(),
            LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        var (bySection, solution) = SectionMetricsAnalyzer.Analyze(classified, CancellationToken.None);

        Assert.True(solution.Files > 0);
        // The invariant the per-declaration filter must not break: sections still partition the
        // corpus, with no file counted twice and none dropped.
        Assert.Equal(solution.LocProd, bySection.Values.Sum(m => m.LocProd));
        Assert.Equal(solution.Files, bySection.Values.Sum(m => m.Files));
        Assert.Equal(solution.Methods, bySection.Values.Sum(m => m.Methods));
    }

    [Fact]
    public async Task Analyze_IncludesConstructorsAndPartialImplementations()
    {
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, SurfaceScoreConfig.Default(),
            LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        var (bySection, solution) = SectionMetricsAnalyzer.Analyze(classified, CancellationToken.None);

        // Constructors carry implementation just as methods do, and `snapshot` has always sampled
        // them — excluding them understated a section and made the cyclomatic figure incomparable
        // with the series it sits beside. Count the corpus's constructors-with-a-body directly.
        int constructorsWithBodies = classified
            .SelectMany(c => c.Type.GetMembers().OfType<Microsoft.CodeAnalysis.IMethodSymbol>())
            .Where(m => m.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor
                        && !m.IsImplicitlyDeclared
                        && m.DeclaringSyntaxReferences.Length > 0)
            .Count(m => m.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(CancellationToken.None))
                .OfType<BaseMethodDeclarationSyntax>()
                .Any(bm => bm.Body is not null || bm.ExpressionBody is not null));

        Assert.True(constructorsWithBodies > 0, "sample solution declares no constructor with a body");
        Assert.True(solution.Methods >= constructorsWithBodies);
        Assert.Equal(solution.Methods, bySection.Values.Sum(m => m.Methods));
    }

    [Fact]
    public async Task Analyze_NamesAMaxMethodEvenWhenEveryScoreIsZero()
    {
        var report = await ScoreDefaultAsync();

        // A section of straight-line code scores 0 for every method, and `0 > 0` never fires — so
        // a strict comparison alone left a non-empty distribution claiming a max held by no method.
        foreach (var (section, m) in report.MetricsBySection)
        {
            if (m.Methods == 0) continue;
            Assert.False(string.IsNullOrEmpty(m.Cognitive.MaxMethod),
                $"{section} samples {m.Methods} methods but names no cognitive max");
            Assert.False(string.IsNullOrEmpty(m.Cyclomatic.MaxMethod),
                $"{section} samples {m.Methods} methods but names no cyclomatic max");
        }

        // The sample solution really does contain such a section — otherwise this test proves nothing.
        Assert.Contains(report.MetricsBySection.Values, m => m.Methods > 0 && m.Cognitive.Max == 0);
    }

    [Fact]
    public async Task Analyze_CountsEveryMemberThatDeclaresABody()
    {
        var classified = await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, SurfaceScoreConfig.Default(),
            LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None);

        var (bySection, solution) = SectionMetricsAnalyzer.Analyze(classified, CancellationToken.None);

        // The corpus's own count of body-declaring members, derived independently of the analyzer:
        // every method symbol that is not an accessor and not compiler-synthesized, whose
        // implementation declaration carries a body. An allowlist of MethodKinds silently dropped
        // constructors, then explicit interface implementations; this pins the whole class.
        // Generated-ness is per declaration, not per type: a partial type with a generated half
        // contributes its handwritten methods and not the others, whichever file is primary.
        int expected = classified
            .SelectMany(c => c.Type.GetMembers().OfType<Microsoft.CodeAnalysis.IMethodSymbol>())
            .Where(m => m.AssociatedSymbol is null && !m.IsImplicitlyDeclared)
            .Select(m => (m.PartialImplementationPart ?? m).DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(CancellationToken.None))
                .OfType<BaseMethodDeclarationSyntax>()
                .FirstOrDefault())
            .Count(bm => bm is not null
                         && !GeneratedCode.IsGeneratedFile(bm.SyntaxTree.FilePath)
                         && (bm.Body is not null || bm.ExpressionBody is not null));

        Assert.Equal(expected, solution.Methods);
        Assert.Equal(solution.Methods, bySection.Values.Sum(m => m.Methods));
    }

    // ---------------- Output ----------------

    [Fact]
    public async Task Json_EmitsMetricsPerGroupAndForTheSolution()
    {
        var report = await ScoreDefaultAsync();
        var output = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null));
        using var doc = JsonDocument.Parse(output);

        var solution = doc.RootElement.GetProperty("metrics");
        Assert.True(solution.GetProperty("locProd").GetInt32() > 0);
        Assert.True(solution.GetProperty("files").GetInt32() > 0);
        Assert.True(solution.GetProperty("classes").GetInt32() > 0);
        solution.GetProperty("interfaces").GetInt32();
        solution.GetProperty("methods").GetInt32();
        solution.GetProperty("maxClassLoc").GetInt32();
        solution.GetProperty("maxClassLocName").GetString();
        foreach (var axis in new[] { "cognitive", "cyclomatic" })
        {
            var d = solution.GetProperty(axis);
            d.GetProperty("avg").GetDouble();
            d.GetProperty("p95").GetInt32();
            d.GetProperty("max").GetInt32();
            d.GetProperty("maxMethod").GetString();
        }

        var groups = doc.RootElement.GetProperty("groups").EnumerateArray().ToList();
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.True(g.GetProperty("metrics").GetProperty("locProd").GetInt32() > 0));
    }

    [Fact]
    public async Task Compact_ShowsLocAndAComplexityFigurePerSection()
    {
        var report = await ScoreDefaultAsync();
        var output = Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 3, 0, null));

        Assert.Contains("corpus: loc=", output);
        var services = report.MetricsBySection["Services"];
        Assert.Contains($"loc={services.LocProd} cogP95={services.Cognitive.P95} cogMax={services.Cognitive.Max}", output);
    }

    [Fact]
    public async Task Markdown_ShowsMetricsInTheGroupTotalsTable()
    {
        var report = await ScoreDefaultAsync();
        var output = Capture(() => SurfaceScoreCommand.WriteMarkdown(report, null, 3, 0, null));

        Assert.Contains("| Group | Score | LOC | Files | Classes | Interfaces |", output);
        Assert.Contains("- **Corpus**: loc=", output);
    }

    [Fact]
    public async Task ListGroups_CoversSectionsThatScoredNothing()
    {
        var report = await ScoreDefaultAsync();
        // A section with classified types but no scored entry has metrics and no GroupScore.
        // Listing only the scored groups would drop it from a size-ranked view.
        report.ConfiguredSections.Add("SilentSection");
        report.MetricsBySection["SilentSection"] = new SectionMetrics(
            LocProd: 42, Files: 1, Classes: 1, Interfaces: 0, Methods: 0,
            Cognitive: MetricDistribution.Empty, Cyclomatic: MetricDistribution.Empty,
            MaxClassLoc: 7, MaxClassLocName: "Quiet");
        Assert.False(report.Groups.ContainsKey("SilentSection"));

        var compact = Capture(() => SurfaceScoreCommand.WriteGroupListForTest(report, OutputFormat.Compact));
        Assert.Contains("SilentSection", compact);
        Assert.Contains("loc=42", compact);

        var json = Capture(() => SurfaceScoreCommand.WriteGroupListForTest(report, OutputFormat.Json));
        using var doc = JsonDocument.Parse(json);
        var silent = doc.RootElement.GetProperty("sections").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "SilentSection");
        Assert.Equal(42, silent.GetProperty("locProd").GetInt32());
        Assert.Equal(0, silent.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Writers_TolerateAReportWithNoMetricsPass()
    {
        // Hand-built reports (the baseline and build-diagnostic suites make several) never run the
        // metrics pass. Every writer has to render them rather than dereference a null.
        var report = new ScoreReport();
        Assert.Equal(SectionMetrics.Empty, report.Metrics);

        Assert.Contains("loc=0", Capture(() => SurfaceScoreCommand.WriteCompact(report, null, 10, 25, null)));
        Assert.Contains("loc=0", Capture(() => SurfaceScoreCommand.WriteMarkdown(report, null, 10, 25, null)));

        var json = Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("metrics").GetProperty("locProd").GetInt32());
    }
}
