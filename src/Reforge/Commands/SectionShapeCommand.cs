using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reforge.Commands;

/// <summary>
/// <c>section-shape</c> — renders the resolved architectural shape of each section (one per
/// assembly): owned repositories, read/full service interfaces, primary/settings/cache DTOs (with
/// provenance), documented read shards, cross-section read/write-surface use, missing surfaces,
/// visible-debt suppressions (grandfathered deps + escape-hatch reads), and advisory candidates
/// (derivable reads, missing info facts, cache-answerable facts).
/// </summary>
public static class SectionShapeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var configOption = new Option<string?>("--config")
        {
            Description = "Path to reforge.surface-score.json (default: search upward from solution dir). Supplies per-section policy; sections themselves come from the solution's assemblies."
        };
        var sectionOption = new Option<string?>("--section")
        {
            Description = "Restrict output to a single section by name (the assembly name, common solution prefix stripped)."
        };
        var maxBuildDiagnosticsOption = new Option<int>("--max-build-diagnostics")
        {
            Description = "Cap on the number of individual compile errors listed when the workspace compile is degraded (default 25). 0 = unlimited. Only the listed detail is capped — the error/unresolved counts are always exact.",
            DefaultValueFactory = _ => 25
        };
        var allowDegradedOption = new Option<bool>("--allow-degraded")
        {
            Description = "Render the shape even when the solution did not compile cleanly. Without this, a degraded build prints nothing and exits 2 — the anchors and missing* findings are read off the same semantic model a score is, and are wrong in the same way. With it, the shape is printed and the exit code is 0."
        };

        var command = new Command("section-shape",
            "Render each section's architectural shape (interfaces, DTO anchors, cross-section use, missing surfaces, visible debt, advisories). Supports Compact, Markdown, and JSON.")
        {
            configOption,
            sectionOption,
            maxBuildDiagnosticsOption,
            allowDegradedOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var configPath = parseResult.GetValue(configOption);
            var sectionFilter = parseResult.GetValue(sectionOption);
            var maxBuildDiagnostics = parseResult.GetValue(maxBuildDiagnosticsOption);
            var allowDegraded = parseResult.GetValue(allowDegradedOption);
            var format = parseResult.GetValue(formatOption);

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var dir = LocationHelper.GetSolutionDirectory(solution);
                var config = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out var loadedFrom);

                // This command never checked build health, though its anchors and missing* findings
                // come off the same semantic model a score does and break the same way. Inspected
                // BEFORE the analysis rather than after: the compilations it forces are the bulk of
                // the cost either way (Roslyn caches them), so checking first means a broken tree
                // skips the section analysis entirely instead of computing output nobody may print.
                var buildHealth = await BuildInspector.InspectAsync(solution, maxBuildDiagnostics, ct);
                if (buildHealth.Degraded)
                {
                    if (!allowDegraded)
                    {
                        sw.Stop();
                        Telemetry.Log("section-shape",
                            $"refused=degraded-build errors={buildHealth.CompilationErrorCount} unresolved={buildHealth.UnresolvedReferenceCount}",
                            0, sw.ElapsedMilliseconds);
                        return DegradedBuildGate.Refuse(buildHealth, "section-shape", Console.Error);
                    }

                    DegradedBuildGate.Warn(buildHealth, "section-shape", Console.Error);
                }

                // This command resolves the primary/settings anchors, which the removed
                // canonicalReadDtos list used to feed. Anchors and missing-surface output can move
                // for a config that still declares it, so say so rather than change silently.
                var removedField = config.RemovedCanonicalReadDtosWarning();
                if (removedField is not null)
                    Console.Error.WriteLine($"WARNING: {removedField}");

                var classified = (await SolutionClassifier.ClassifyAsync(solution, config, dir, ct)).ToList();
                var arch = await SectionShapeAnalyzer.AnalyzeAsync(solution, classified, config, dir, ct);

                var sections = arch.Sections
                    .Where(s => sectionFilter is null || s.Name.Equals(sectionFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (sectionFilter is not null && sections.Count == 0)
                    Console.Error.WriteLine($"WARNING: --section '{sectionFilter}' is not a section of this solution. " +
                        $"Sections: {string.Join(", ", arch.Sections.Select(s => s.Name))}.");

                if (format == OutputFormat.Json)
                    WriteJson(sections, loadedFrom);
                else
                    WriteText(sections, loadedFrom, markdown: format == OutputFormat.Markdown);

                sw.Stop();
                Telemetry.Log("section-shape",
                    $"sections={sections.Count} cfg={(loadedFrom is null ? "default" : Path.GetFileName(loadedFrom))}",
                    sections.Count, sw.ElapsedMilliseconds);

                return 0;
            }
        });

        return command;
    }

    private static void WriteJson(List<SectionShape> sections, string? configPath)
    {
        var payload = new
        {
            command = "section-shape",
            configPath,
            sections = sections.Select(s => new
            {
                name = s.Name,
                repoBacked = s.Facts.RepoBacked,
                ownedRepositoryInterfaces = s.OwnedRepositoryInterfaces,
                ownedRepositoryImplementations = s.OwnedRepositoryImplementations,
                readServiceInterfaces = s.ReadServiceInterfaces.Select(Iface).ToArray(),
                fullServiceInterfaces = s.FullServiceInterfaces.Select(Iface).ToArray(),
                primaryInfoDto = Dto(s.PrimaryInfoDto),
                settingsInfoDto = Dto(s.SettingsInfoDto),
                cacheDto = Dto(s.CacheDto),
                cacheDtoProvenance = s.CacheDtoProvenance,
                readShards = s.ReadShards.Select(sh => new { name = sh.Name, purpose = sh.Purpose }).ToArray(),
                readSurfaceCallers = s.ReadSurfaceCallers.Select(Use).ToArray(),
                writeSurfaceCallers = s.WriteSurfaceCallers.Select(Use).ToArray(),
                missing = s.Missing.Select(m => new { rule = m.Rule, detail = m.Detail }).ToArray(),
                grandfathered = s.Grandfathered.Select(g => new { dependency = g.Dependency, reason = g.Reason, since = g.Since, owner = g.Owner }).ToArray(),
                escapeHatches = s.EscapeHatches.Select(e => new { method = e.Method, reason = e.Reason, since = e.Since, owner = e.Owner }).ToArray(),
                chargedReadMethods = s.ChargedReadMethods.Select(c => new { @interface = c.Interface, method = c.Method, kind = c.Kind.ToString(), returns = c.Returns, escapeHatch = c.EscapeHatch }).ToArray(),
                advisory = new
                {
                    derivableReadMethods = s.DerivableReadMethods.Select(d => new { @interface = d.Interface, method = d.Method, kind = d.Kind.ToString(), targetDto = d.TargetDto, hint = d.Hint }).ToArray(),
                    missingInfoFacts = s.MissingInfoFacts.Select(f => new { fact = f.Fact, targetDto = f.TargetDto }).ToArray(),
                    cacheFactCandidates = s.CacheFactCandidates.Select(c => new { method = c.Method, fact = c.Fact, cacheDto = c.CacheDto }).ToArray(),
                    crossSectionWriteSurfaceUnverified = s.WriteSurfaceUnverified.Select(Use).ToArray()
                }
            }).ToArray()
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));

        static object? Dto(DtoAnchor? a) => a is null ? null : new { display = a.Display, paths = a.Paths };
        // `exported` says which population an entry belongs to: the scoring passes charge exported
        // types only, so a consumer counting write surface off this array has to filter, and until
        // the flag was here nothing told it so.
        static object Iface(ServiceInterfaceListing i) => new { name = i.Name, exported = i.Exported };
        static object Use(CrossSectionUse u) => new { caller = u.Caller, dependency = u.Dependency, dependencySection = u.DependencySection, suggestedReadInterface = u.SuggestedReadInterface, observedCalls = u.ObservedCalls };
    }

    private static void WriteText(List<SectionShape> sections, string? configPath, bool markdown)
    {
        var sb = new StringBuilder();
        var h1 = markdown ? "# " : "";
        var h2 = markdown ? "## " : "";

        if (sections.Count == 0)
        {
            Console.WriteLine("No sections to shape (no non-test project produced source types).");
            return;
        }

        sb.AppendLine($"{h1}section-shape ({sections.Count} section{(sections.Count == 1 ? "" : "s")})");
        if (configPath is not null) sb.AppendLine($"config: {configPath}");
        sb.AppendLine();

        foreach (var s in sections)
        {
            sb.AppendLine($"{h2}{s.Name}{(s.Facts.RepoBacked ? " (repo-backed)" : "")}");
            if (s.OwnedRepositoryInterfaces.Count > 0) sb.AppendLine($"  repositories: {string.Join(", ", s.OwnedRepositoryInterfaces)}");
            if (s.ReadServiceInterfaces.Count > 0) sb.AppendLine($"  read: {Ifaces(s.ReadServiceInterfaces)}");
            if (s.FullServiceInterfaces.Count > 0) sb.AppendLine($"  full: {Ifaces(s.FullServiceInterfaces)}");
            if (s.PrimaryInfoDto is not null) sb.AppendLine($"  primaryInfoDto: {Short(s.PrimaryInfoDto.Display)} ({s.PrimaryInfoDto.Paths.Count} paths)");
            if (s.SettingsInfoDto is not null) sb.AppendLine($"  settingsInfoDto: {Short(s.SettingsInfoDto.Display)}");
            if (s.CacheDto is not null) sb.AppendLine($"  cacheDto: {Short(s.CacheDto.Display)} [{s.CacheDtoProvenance}]");

            foreach (var m in s.Missing) sb.AppendLine($"  MISSING {m.Rule}: {m.Detail}");
            foreach (var c in s.WriteSurfaceCallers) sb.AppendLine($"  crossSectionWriteSurface: {c.Caller} <- {c.Dependency} (use {c.SuggestedReadInterface})");
            foreach (var c in s.ChargedReadMethods) sb.AppendLine($"  chargedRead: {c.Interface}.{c.Method} ({c.Kind}){(c.EscapeHatch ? " [escape-hatch]" : "")}");

            foreach (var g in s.Grandfathered) sb.AppendLine($"  grandfathered: {g.Dependency} ({g.Reason}, since {g.Since})");

            // Advisory
            foreach (var d in s.DerivableReadMethods) sb.AppendLine($"  advisory derivable: {d.Method} -> {d.TargetDto}");
            foreach (var f in s.MissingInfoFacts) sb.AppendLine($"  advisory missing-fact: {f.Fact}");
            foreach (var u in s.WriteSurfaceUnverified) sb.AppendLine($"  advisory unverified: {u.Caller} <- {u.Dependency} (escapes analysis)");
            sb.AppendLine();
        }

        Console.Write(sb.ToString());

        static string Short(string display) => display.Split('.').Last();

        // An interface its assembly does not export reads with the `internal` keyword in front of
        // it, which is both what the declaration says and the reason it scores nothing.
        static string Ifaces(IReadOnlyList<ServiceInterfaceListing> list)
            => string.Join(", ", list.Select(i => i.Exported ? i.Name : $"internal {i.Name}"));
    }
}
