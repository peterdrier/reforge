using System.CommandLine;
using Reforge.Commands;

namespace Reforge;

/// <summary>
/// The global options every command shares. Bundled so <see cref="CommandRegistry"/> can hand one
/// value to factories that need different subsets of them.
/// </summary>
public sealed record CommandOptions(
    Option<string?> Solution,
    Option<OutputFormat> Format,
    Option<int?> Limit);

/// <summary>
/// One command's identity: the name it is invoked by, and whether it may be relayed to a hot
/// server. Deliberately holds <b>no</b> <c>System.CommandLine</c> types — see the class remarks on
/// <see cref="CommandRegistry"/> for why that matters.
/// </summary>
/// <param name="Name">The command name as typed on the CLI.</param>
/// <param name="RelayEligible">
/// Whether <c>Program</c> may forward this command to a running server, and equivalently whether
/// the server registers it. Ineligible commands are the five that cannot meaningfully run inside
/// the server process: <c>serve</c> would nest a server, <c>stop</c> would kill the one being
/// used, <c>install</c> and <c>request</c> write outside the repo, and <c>skill</c> just prints a
/// static document. Registering an ineligible command on the server is not merely useless — the
/// client never relays it, so the registration is unreachable.
/// </param>
public sealed record CommandSpec(string Name, bool RelayEligible);

/// <summary>
/// The single list of commands reforge offers. <c>Program</c> (cold path) and
/// <see cref="ServeCommand"/> (hot path) both build their root command from this, so a command
/// cannot exist on one host and not the other.
///
/// <para>Before this existed the surface was declared in four places that disagreed:
/// <c>Program</c> listed 29, <c>ServeCommand</c> 21, the agent-facing skill doc 24, and the README
/// 14. Every command added after 2026-04-13 reached only <c>Program</c>. The consequence was not a
/// missing feature but a <b>silent</b> one: <c>TryRelayAsync</c> reports success for any completed
/// socket round-trip and the server nulls stderr, so with a server running <c>reforge
/// surface-score</c> printed the root help text and exited 0 — which a scripting agent reads as
/// "ran fine, no findings".</para>
///
/// <para><b>Why the specs carry no factory delegates.</b> <c>Program</c> consults this registry on
/// the relay path, which runs before <c>MSBuildLocator.RegisterDefaults()</c> and must not load
/// Roslyn (see the comments at the top of <c>Program.cs</c>). Keeping <see cref="Specs"/> to
/// strings and bools means asking "is this relayable?" touches no <c>System.CommandLine</c> or
/// Roslyn type at all; the factories live in <see cref="Create"/>, a method whose body is JIT'd
/// only when a host actually builds its commands. The cost of that split is that the factory
/// mapping is a second enumeration of the names — so a spec with no factory throws loudly at
/// startup on both hosts, and <c>CommandRegistryTests</c> pins that every spec resolves.</para>
/// </summary>
public static class CommandRegistry
{
    /// <summary>
    /// Every command reforge registers, in the order the root command lists them.
    /// Adding a command means adding one entry here and one arm in <see cref="Create"/>.
    /// </summary>
    public static readonly IReadOnlyList<CommandSpec> Specs =
    [
        // Phase 1 — semantic queries
        new("references", RelayEligible: true),
        new("callers", RelayEligible: true),
        new("implementations", RelayEligible: true),
        new("members", RelayEligible: true),
        new("dependencies", RelayEligible: true),
        new("injected", RelayEligible: true),
        new("inheritors", RelayEligible: true),
        new("call-chain", RelayEligible: true),
        new("usages", RelayEligible: true),
        new("parameters", RelayEligible: true),

        // Service ownership analysis
        new("dbset-usage", RelayEligible: true),
        new("ownership-violations", RelayEligible: true),
        new("service-map", RelayEligible: true),

        // Code health analysis
        new("health", RelayEligible: true),
        new("snapshot", RelayEligible: true),
        new("cycles", RelayEligible: true),

        // Audit commands
        new("audit-auth", RelayEligible: true),
        new("audit-cache", RelayEligible: true),
        new("audit-immutable", RelayEligible: true),
        new("audit-ef", RelayEligible: true),
        new("audit-surface", RelayEligible: true),
        new("audit-downstream", RelayEligible: true),
        new("surface-score", RelayEligible: true),
        new("section-shape", RelayEligible: true),
        new("misplaced", RelayEligible: true),

        // Help & setup — never relayed (see CommandSpec.RelayEligible)
        new("skill", RelayEligible: false),
        new("install", RelayEligible: false),
        new("request", RelayEligible: false),

        // Server lifecycle — never relayed
        new("serve", RelayEligible: false),
        new("stop", RelayEligible: false)
    ];

    /// <summary>
    /// Whether <paramref name="arg"/> names a command the client may forward to a running server.
    /// An unknown name is <b>not</b> relayable: it must reach the cold path so
    /// <c>System.CommandLine</c> can produce its own "unrecognized command" error rather than the
    /// server silently answering with help text. Options (<c>--help</c>, <c>--list</c>, …) are not
    /// command names and fall out of this the same way.
    /// </summary>
    public static bool IsRelayEligible(string arg)
    {
        foreach (var spec in Specs)
            if (spec.RelayEligible && string.Equals(spec.Name, arg, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Whether a full argument list names a relay-eligible command — i.e. the same question as the
    /// single-argument overload, asked of <see cref="FindCommandToken"/>'s answer rather than of
    /// <c>args[0]</c>.
    /// </summary>
    /// <remarks>
    /// The root's <c>--solution</c>, <c>--format</c> and <c>--limit</c> are <c>Recursive</c>, so
    /// they parse before the subcommand too: <c>reforge --format json cycles</c> is a valid
    /// invocation whose first argument is an option. Testing <c>args[0]</c> would send every such
    /// call down the cold path while a server sits idle.
    /// </remarks>
    public static bool IsRelayEligible(string[] args)
    {
        var command = FindCommandToken(args);
        return command is not null && IsRelayEligible(command);
    }

    /// <summary>
    /// The first argument that names a registered command, or null if none does.
    /// </summary>
    /// <remarks>
    /// A scan rather than a parse, because this runs before <c>System.CommandLine</c> is loaded
    /// (see the class remarks). <see cref="ValueTakingRootOptions"/> is what keeps the scan honest:
    /// a root option's value must not be mistaken for a command name.
    /// </remarks>
    public static string? FindCommandToken(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Length == 0) continue;

            if (arg[0] == '-')
            {
                // `--solution app.slnx` consumes the next argument. The `--solution=app.slnx` form
                // carries its value in the same token and consumes nothing.
                if (TakesSeparateValue(arg)) i++;
                continue;
            }

            foreach (var spec in Specs)
                if (string.Equals(spec.Name, arg, StringComparison.Ordinal))
                    return arg;
        }
        return null;
    }

    /// <summary>
    /// The root options that take a separate value. Kept here rather than read off the parser
    /// because <see cref="FindCommandToken"/> runs before <c>System.CommandLine</c> loads; both
    /// hosts declare exactly these three, and a fourth added there must be added here too.
    /// </summary>
    private static readonly string[] ValueTakingRootOptions = ["--solution", "--format", "--limit"];

    private static bool TakesSeparateValue(string arg)
    {
        foreach (var option in ValueTakingRootOptions)
            if (string.Equals(arg, option, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Builds the commands a host should register. <paramref name="relayEligibleOnly"/> is set by
    /// the server, which must not offer the five commands the client never relays to it.
    /// </summary>
    public static IEnumerable<Command> CreateAll(CommandOptions options, bool relayEligibleOnly = false)
    {
        foreach (var spec in Specs)
        {
            if (relayEligibleOnly && !spec.RelayEligible) continue;
            yield return Create(spec.Name, options);
        }
    }

    /// <summary>
    /// The name → factory mapping. Kept as a method rather than delegates stored on
    /// <see cref="CommandSpec"/> so that consulting the registry costs no assembly loads; see the
    /// class remarks. Throws for a name with no arm, which surfaces a half-added command
    /// immediately on both hosts instead of as missing output later.
    /// </summary>
    public static Command Create(string name, CommandOptions o) => name switch
    {
        "references" => ReferencesCommand.Create(o.Solution, o.Format, o.Limit),
        "callers" => CallersCommand.Create(o.Solution, o.Format, o.Limit),
        "implementations" => ImplementationsCommand.Create(o.Solution, o.Format, o.Limit),
        "members" => MembersCommand.Create(o.Solution, o.Format, o.Limit),
        "dependencies" => DependenciesCommand.Create(o.Solution, o.Format, o.Limit),
        "injected" => InjectedCommand.Create(o.Solution, o.Format, o.Limit),
        "inheritors" => InheritorsCommand.Create(o.Solution, o.Format, o.Limit),
        "call-chain" => CallChainCommand.Create(o.Solution, o.Format, o.Limit),
        "usages" => UsagesCommand.Create(o.Solution, o.Format, o.Limit),
        "parameters" => ParametersCommand.Create(o.Solution, o.Format, o.Limit),

        "dbset-usage" => DbSetUsageCommand.Create(o.Solution, o.Format, o.Limit),
        "ownership-violations" => OwnershipViolationsCommand.Create(o.Solution, o.Format, o.Limit),
        "service-map" => ServiceMapCommand.Create(o.Solution, o.Format, o.Limit),

        "health" => HealthCommand.Create(o.Solution, o.Format, o.Limit),
        "snapshot" => SnapshotCommand.Create(o.Solution, o.Format, o.Limit),
        "cycles" => CyclesCommand.Create(o.Solution, o.Format, o.Limit),

        "audit-auth" => AuditAuthCommand.Create(o.Solution, o.Format, o.Limit),
        "audit-cache" => AuditCacheCommand.Create(o.Solution, o.Format, o.Limit),
        "audit-immutable" => AuditImmutableCommand.Create(o.Solution, o.Format, o.Limit),
        "audit-ef" => AuditEfCommand.Create(o.Solution, o.Format, o.Limit),
        "audit-surface" => AuditSurfaceCommand.Create(o.Solution, o.Format, o.Limit),
        "audit-downstream" => AuditDownstreamCommand.Create(o.Solution, o.Format, o.Limit),
        "surface-score" => SurfaceScoreCommand.Create(o.Solution, o.Format, o.Limit),
        "section-shape" => SectionShapeCommand.Create(o.Solution, o.Format, o.Limit),
        "misplaced" => MisplacedCommand.Create(o.Solution, o.Format, o.Limit),

        "skill" => SkillCommand.Create(),
        "install" => InstallCommand.Create(),
        "request" => RequestCommand.Create(),

        "serve" => ServeCommand.Create(o.Solution),
        "stop" => StopCommand.Create(o.Solution),

        _ => throw new InvalidOperationException(
            $"Command '{name}' is listed in CommandRegistry.Specs but has no factory in CommandRegistry.Create.")
    };
}
