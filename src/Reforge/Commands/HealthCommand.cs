using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;

namespace Reforge.Commands;

public static class HealthCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var topOption = new Option<int>("--top")
        {
            Description = "Number of types to show (default: 20)",
            DefaultValueFactory = _ => 20
        };
        var namespaceOption = new Option<string?>("--namespace")
        {
            Description = "Filter by namespace prefix"
        };

        var command = new Command("health", "Analyze code health and rank types by refactoring risk")
        {
            topOption, namespaceOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);
            var top = parseResult.GetValue(topOption);
            var ns = parseResult.GetValue(namespaceOption);
            var sw = Stopwatch.StartNew();

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var reports = await CodeHealthAnalyzer.AnalyzeAsync(solution, ns, cancellationToken);

                // Sort by score descending, take top N
                var ranked = reports.OrderByDescending(r => r.Score).ToList();
                var totalAnalyzed = ranked.Count;

                var effectiveLimit = limit ?? top;
                if (ranked.Count > effectiveLimit)
                    ranked = ranked.Take(effectiveLimit).ToList();

                if (format == OutputFormat.Json)
                    WriteJson(ranked, totalAnalyzed);
                else
                    WriteCompact(ranked, totalAnalyzed);

                sw.Stop();
                Telemetry.Log("health", ns ?? "(all)", totalAnalyzed, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static void WriteCompact(List<TypeHealthReport> reports, int totalAnalyzed)
    {
        Console.WriteLine($"{totalAnalyzed} types analyzed, showing top {reports.Count} by risk score");
        Console.WriteLine();

        foreach (var r in reports)
        {
            var instabilityLabel = r.Instability switch
            {
                > 0.8 => " (unstable)",
                < 0.2 => " (stable)",
                _ => ""
            };
            var cohesionLabel = r.CohesionScore switch
            {
                < 0.3 => $" (low — {r.FieldClusterCount} field clusters)",
                < 0.6 => "",
                _ => " (good)"
            };

            Console.WriteLine($"{r.Name,-55} score: {r.Score}");
            Console.WriteLine($"  {r.File}:{r.Line}");
            Console.WriteLine($"  coupling: {r.DependencyCount} dependencies, {r.DependentCount} dependents{instabilityLabel}");
            Console.WriteLine($"  complexity: {r.Lines} lines, {r.MethodCount} methods, max cyclomatic {r.MaxCyclomaticComplexity} ({r.MaxComplexityMethod})");
            Console.WriteLine($"  cohesion: {r.CohesionScore:F2}{cohesionLabel}");
            Console.WriteLine();
        }
    }

    private static void WriteJson(List<TypeHealthReport> reports, int totalAnalyzed)
    {
        var output = new
        {
            command = "health",
            totalAnalyzed,
            results = reports.Select(r => new
            {
                name = r.Name,
                qualifiedName = r.QualifiedName,
                file = r.File,
                line = r.Line,
                score = r.Score,
                lines = r.Lines,
                methodCount = r.MethodCount,
                maxCyclomaticComplexity = r.MaxCyclomaticComplexity,
                maxComplexityMethod = r.MaxComplexityMethod,
                dependencyCount = r.DependencyCount,
                dependentCount = r.DependentCount,
                instability = Math.Round(r.Instability, 2),
                cohesionScore = Math.Round(r.CohesionScore, 2),
                fieldClusterCount = r.FieldClusterCount
            }).ToArray(),
            total = reports.Count
        };

        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }
}
