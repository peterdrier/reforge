using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;

namespace Reforge.Commands;

public static class ServiceMapCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var namespaceOption = new Option<string?>("--namespace") { Description = "Filter services by namespace prefix" };

        var command = new Command("service-map", "Bird's-eye view of each service's DbSet accesses and injected interfaces")
        {
            namespaceOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);
            var namespaceFilter = parseResult.GetValue(namespaceOption);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var consumers = await DbContextAnalyzer.FindDbContextConsumersAsync(solution, cancellationToken);

                // Apply namespace filter
                if (!string.IsNullOrEmpty(namespaceFilter))
                {
                    consumers = consumers
                        .Where(c => c.Type.ContainingNamespace?.ToDisplayString()
                            ?.StartsWith(namespaceFilter, StringComparison.Ordinal) == true)
                        .ToList();
                }

                var services = new List<ServiceInfo>();

                foreach (var (type, dbContextParam) in consumers)
                {
                    // Collect injected interfaces (constructor params whose type is an interface)
                    var injected = new List<string>();
                    foreach (var ctor in type.Constructors)
                    {
                        if (ctor.IsImplicitlyDeclared)
                            continue;

                        foreach (var param in ctor.Parameters)
                        {
                            if (param.Type.TypeKind == TypeKind.Interface)
                            {
                                injected.Add(param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                            }
                        }
                    }

                    // Collect DbSet accesses (deduplicated property names)
                    var accesses = await DbContextAnalyzer.FindDbSetAccessesAsync(type, solution, cancellationToken);
                    var tables = accesses
                        .Select(a => a.DbSetName)
                        .Distinct()
                        .OrderBy(n => n)
                        .ToList();

                    // Get file location
                    var loc = type.Locations.FirstOrDefault(l => l.IsInSource);
                    var filePath = "";
                    var line = 0;
                    if (loc is not null)
                    {
                        var lineSpan = loc.GetLineSpan();
                        filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                        line = lineSpan.StartLinePosition.Line + 1;
                    }

                    services.Add(new ServiceInfo(
                        type.Name,
                        filePath,
                        line,
                        injected,
                        tables));
                }

                // Sort by name for stable output
                services.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

                int totalCount = services.Count;
                if (limit.HasValue && services.Count > limit.Value)
                {
                    services = services.Take(limit.Value).ToList();
                }

                if (format == OutputFormat.Json)
                {
                    WriteJson(services, totalCount, limit.HasValue && totalCount > services.Count ? totalCount : (int?)null);
                }
                else
                {
                    WriteCompact(services, totalCount, limit.HasValue && totalCount > services.Count ? totalCount : (int?)null);
                }

                sw.Stop();
                Telemetry.Log("service-map", namespaceFilter ?? "(all)", totalCount, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static void WriteCompact(List<ServiceInfo> services, int total, int? totalBeforeLimit)
    {
        if (totalBeforeLimit.HasValue)
            Console.WriteLine($"{services.Count} of {totalBeforeLimit.Value} services in service-map");
        else
            Console.WriteLine($"{total} services in service-map");

        if (services.Count == 0)
            return;

        Console.WriteLine();

        foreach (var svc in services)
        {
            Console.WriteLine(svc.Name);
            if (svc.Injected.Count > 0)
                Console.WriteLine($"  injected: {string.Join(", ", svc.Injected)}");
            if (svc.Tables.Count > 0)
                Console.WriteLine($"  tables: {string.Join(", ", svc.Tables)}");
            Console.WriteLine();
        }
    }

    private static void WriteJson(List<ServiceInfo> services, int total, int? totalBeforeLimit)
    {
        if (totalBeforeLimit.HasValue)
        {
            var output = new
            {
                command = "service-map",
                services = services.Select(s => new
                {
                    name = s.Name,
                    file = s.File,
                    line = s.Line,
                    injected = s.Injected,
                    tables = s.Tables
                }).ToArray(),
                total = services.Count,
                totalBeforeLimit = totalBeforeLimit.Value
            };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
        else
        {
            var output = new
            {
                command = "service-map",
                services = services.Select(s => new
                {
                    name = s.Name,
                    file = s.File,
                    line = s.Line,
                    injected = s.Injected,
                    tables = s.Tables
                }).ToArray(),
                total = services.Count
            };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
    }

    private record ServiceInfo(
        string Name,
        string File,
        int Line,
        List<string> Injected,
        List<string> Tables);
}
