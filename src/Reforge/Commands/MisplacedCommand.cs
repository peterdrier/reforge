using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reforge.Commands;

/// <summary>
/// <c>misplaced</c> — lists methods whose bodies work on another section's data more than their own,
/// with a named destination where there is one.
/// </summary>
/// <remarks>
/// This command <b>lists named problems rather than scoring them</b>, deliberately. A number cannot
/// carry a destination, which is the only actionable part of the finding; a score over this signal
/// would be gameable by inlining a call or adding one layer of indirection; and calibrating a weight
/// needs a second corpus that does not exist. "Not automating judgment calls" is the standing rule —
/// the tool says which method, where it should go, and what the evidence is, and the reader decides.
/// </remarks>
public static class MisplacedCommand
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
            Description = "Path to reforge.surface-score.json (default: search upward from solution dir). Only the type classification is used; sections come from the solution's assemblies."
        };
        var sectionOption = new Option<string?>("--section")
        {
            Description = "Only report methods currently IN this section (the assembly name, common solution prefix stripped)."
        };
        var toOption = new Option<string?>("--to")
        {
            Description = "Only report methods whose proposed destination is this section."
        };
        var ratioOption = new Option<int>("--foundation-ratio")
        {
            Description = $"How far a target section's fan-in must exceed its fan-out before it counts as shared infrastructure and no move is proposed (default {MisplacedAnalyzer.FoundationFanInRatio}). 0 disables the category. This is the one tuned number here — see MisplacedAnalyzer.FoundationFanInRatio for how it was chosen and on what.",
            DefaultValueFactory = _ => MisplacedAnalyzer.FoundationFanInRatio
        };
        var verdictOption = new Option<string[]>("--verdict")
        {
            Description = "Filter by verdict: move, move-would-duplicate, orchestrator, mapper, blocked, judgment. Repeatable. Default: move and move-would-duplicate — the two that name a destination.",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("misplaced",
            "List methods whose bodies work on another section's data more than their own, with a proposed destination. Separates pipes (one other section) from orchestrators (three or more).")
        {
            configOption, sectionOption, toOption, verdictOption, ratioOption
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var solutionPath = parseResult.GetValue(solutionOption);
            var configPath = parseResult.GetValue(configOption);
            var sectionFilter = parseResult.GetValue(sectionOption);
            var toFilter = parseResult.GetValue(toOption);
            var verdictFilter = parseResult.GetValue(verdictOption) ?? Array.Empty<string>();
            var foundationRatio = parseResult.GetValue(ratioOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);

            var wanted = ParseVerdicts(verdictFilter, out var unknown);
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine(
                    $"ERROR: unknown --verdict value(s): {string.Join(", ", unknown)}. " +
                    $"Known: {string.Join(", ", Enum.GetValues<MisplacedVerdict>().Select(Slug))}.");
                return 1;
            }

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var dir = LocationHelper.GetSolutionDirectory(solution);
                var config = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);
                var classified = (await SolutionClassifier.ClassifyAsync(solution, config, dir, ct)).ToList();

                var report = await MisplacedAnalyzer.AnalyzeAsync(solution, classified, dir, foundationRatio, ct);
                var all = report.Findings;

                var findings = all
                    .Where(f => wanted.Contains(f.Verdict))
                    .Where(f => sectionFilter is null || f.Section.Equals(sectionFilter, StringComparison.OrdinalIgnoreCase))
                    .Where(f => toFilter is null || (f.TargetSection?.Equals(toFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && findings.Count > limit.Value)
                {
                    totalBeforeLimit = findings.Count;
                    findings = findings.Take(limit.Value).ToList();
                }

                // A limited list looks like a complete one to anything reading the output, and this
                // command's whole purpose is to be acted on. Say what was dropped.
                if (totalBeforeLimit.HasValue)
                    Console.Error.WriteLine(
                        $"WARNING: --limit {limit!.Value} truncated the list; {totalBeforeLimit.Value - findings.Count} " +
                        $"more finding(s) match and are not shown.");

                if (format == OutputFormat.Json)
                    WriteJson(findings, all, report, totalBeforeLimit);
                else
                    WriteCompact(findings, all, totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("misplaced",
                    $"section={sectionFilter ?? "*"} to={toFilter ?? "*"}",
                    totalBeforeLimit ?? findings.Count, sw.ElapsedMilliseconds);
                return 0;
            }
        });

        return command;
    }

    private static HashSet<MisplacedVerdict> ParseVerdicts(string[] raw, out List<string> unknown)
    {
        unknown = new List<string>();
        if (raw.Length == 0)
            return new HashSet<MisplacedVerdict> { MisplacedVerdict.Move, MisplacedVerdict.MoveWouldDuplicate };

        var wanted = new HashSet<MisplacedVerdict>();
        foreach (var token in raw.SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var v in Enum.GetValues<MisplacedVerdict>()) wanted.Add(v);
                continue;
            }

            var match = Enum.GetValues<MisplacedVerdict>()
                .Cast<MisplacedVerdict?>()
                .FirstOrDefault(v => Slug(v!.Value).Equals(token, StringComparison.OrdinalIgnoreCase));
            if (match is null) unknown.Add(token);
            else wanted.Add(match.Value);
        }
        return wanted;
    }

    /// <summary>Kebab-case name for a verdict, used on both the CLI and in JSON.</summary>
    internal static string Slug(MisplacedVerdict verdict) => verdict switch
    {
        MisplacedVerdict.Move => "move",
        MisplacedVerdict.FoundationTarget => "foundation-target",
        MisplacedVerdict.MoveWouldDuplicate => "move-would-duplicate",
        MisplacedVerdict.Orchestrator => "orchestrator",
        MisplacedVerdict.Mapper => "mapper",
        MisplacedVerdict.Blocked => "blocked",
        MisplacedVerdict.Judgment => "judgment",
        _ => verdict.ToString().ToLowerInvariant()
    };

    private static void WriteCompact(
        IReadOnlyList<MisplacedMethod> findings, IReadOnlyList<MisplacedMethod> all, int? totalBeforeLimit)
    {
        var sb = new StringBuilder();

        if (findings.Count == 0)
            sb.AppendLine("No methods matched.");

        foreach (var group in findings.GroupBy(f => f.Section).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine(group.Key);
            foreach (var f in group)
            {
                // The destination TYPE where one was chosen: a section is not a place a method goes.
                // The destination TYPE where one was chosen, already namespace-qualified: a section is
                // not a place a method goes, and the section is still named in the evidence line below.
                var arrow = f.TargetSection is null
                    ? ""
                    : $" -> {f.DestinationType ?? f.TargetSection}";
                sb.AppendLine($"  {f.Method}{arrow}  [{Slug(f.Verdict)}]");
                sb.AppendLine($"    {f.File}:{f.Line}");
                sb.AppendLine($"    {f.Evidence}");
                if (f.DuplicateOf is not null)
                    sb.AppendLine($"    NOTE: {f.TargetSection} already declares {f.DuplicateOf} — reconcile, do not copy");
                if (f.BlockedBy is not null)
                    sb.AppendLine($"    NOTE: cannot move alone — {f.BlockedBy}");
            }
        }

        sb.AppendLine();
        sb.Append(Summary(findings, all, totalBeforeLimit));
        Console.WriteLine(sb.ToString().TrimStart('\n'));
    }

    /// <summary>
    /// The tail line always reports every verdict's count over the FULL result set, including the
    /// ones the filters excluded. A list of 4 "move" findings reads as "4 problems" unless the 60
    /// judgment calls beside them are visible.
    /// </summary>
    private static string Summary(
        IReadOnlyList<MisplacedMethod> findings, IReadOnlyList<MisplacedMethod> all, int? totalBeforeLimit)
    {
        var counts = Enum.GetValues<MisplacedVerdict>()
            .Select(v => (v, n: all.Count(f => f.Verdict == v)))
            .Where(x => x.n > 0)
            .Select(x => $"{Slug(x.v)} {x.n}");

        var shown = totalBeforeLimit.HasValue
            ? $"{findings.Count} shown of {totalBeforeLimit.Value} matching"
            : $"{findings.Count} shown";
        return $"{shown}; all verdicts across the solution: {string.Join(", ", counts)}";
    }

    private static void WriteJson(
        IReadOnlyList<MisplacedMethod> findings, IReadOnlyList<MisplacedMethod> all,
        MisplacedReport report, int? totalBeforeLimit)
    {
        var output = new
        {
            command = "misplaced",
            results = findings.Select(f => new
            {
                method = f.Method,
                file = f.File,
                line = f.Line,
                section = f.Section,
                targetSection = f.TargetSection,
                destinationType = f.DestinationType,
                verdict = Slug(f.Verdict),
                ownTouches = f.OwnTouches,
                targetBehaviorTouches = f.TargetBehaviorTouches,
                targetDataTouches = f.TargetDataTouches,
                sectionsTouched = f.SectionsTouched,
                evidence = f.Evidence,
                duplicateOf = f.DuplicateOf,
                blockedBy = f.BlockedBy
            }).ToArray(),
            total = findings.Count,
            totalBeforeLimit,
            verdictCounts = Enum.GetValues<MisplacedVerdict>()
                .Where(v => all.Any(f => f.Verdict == v))
                .ToDictionary(Slug, v => all.Count(f => f.Verdict == v)),
            sections = report.Sections.Values
                .OrderBy(p => p.Section, StringComparer.Ordinal)
                .Select(p => new { section = p.Section, fanIn = p.FanIn, fanOut = p.FanOut })
                .ToArray()
        };
        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }
}
