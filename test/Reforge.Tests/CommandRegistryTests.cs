using System.CommandLine;
using System.Text;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// Ratchet for S001. The command surface used to be declared in four places that disagreed, and
/// the disagreement was invisible: a command the server did not know about produced the root help
/// text and exit 0, which a scripting agent reads as "ran fine, no findings". These tests pin the
/// single registry, the two hosts building from it, and the failure being loud.
///
/// No server or workspace is started here — every case below is decided before any command body
/// runs. A live round-trip over TCP is the remaining half of S004 and is not covered yet.
/// </summary>
public class CommandRegistryTests
{
    private static CommandOptions Options() => new(
        new Option<string?>("--solution") { Recursive = true },
        new Option<OutputFormat>("--format") { DefaultValueFactory = _ => OutputFormat.Compact, Recursive = true },
        new Option<int?>("--limit") { Recursive = true });

    [Fact]
    public void EverySpec_HasAFactory_AndTheNamesAgree()
    {
        var options = Options();

        foreach (var spec in CommandRegistry.Specs)
        {
            // Throws with a named message if a spec was added without a factory arm — the
            // half-added command that used to surface as missing output much later.
            var command = CommandRegistry.Create(spec.Name, options);

            Assert.Equal(spec.Name, command.Name);
        }
    }

    [Fact]
    public void SpecNames_AreUnique()
    {
        var names = CommandRegistry.Specs.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_ForAnUnregisteredName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CommandRegistry.Create("no-such-command", Options()));
        Assert.Contains("no-such-command", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cold host offers everything; the server offers only what the client will relay to it.
    /// The difference is exactly the five plumbing commands, and it is a registry property rather
    /// than a list either host maintains.
    /// </summary>
    [Fact]
    public void ColdHost_RegistersEveryCommand_ServerHost_RegistersOnlyRelayEligible()
    {
        var cold = CommandRegistry.CreateAll(Options()).Select(c => c.Name).ToList();
        var hot = CommandRegistry.CreateAll(Options(), relayEligibleOnly: true).Select(c => c.Name).ToList();

        Assert.Equal(CommandRegistry.Specs.Count, cold.Count);
        Assert.Equal(CommandRegistry.Specs.Count(s => s.RelayEligible), hot.Count);

        var onlyCold = cold.Except(hot, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "install", "request", "serve", "skill", "stop" }, onlyCold);
    }

    /// <summary>
    /// The four commands the stale server list never learned about — added between 2026-04-15 and
    /// 2026-05-30, and the direct cause of the silent-help-text bug. Two of them are the #3 and #4
    /// most-used commands in the tool.
    /// </summary>
    [Theory]
    [InlineData("snapshot")]
    [InlineData("cycles")]
    [InlineData("surface-score")]
    [InlineData("section-shape")]
    public void CommandsMissedByTheOldServerList_AreRelayEligible_AndRegisteredOnTheServer(string name)
    {
        Assert.True(CommandRegistry.IsRelayEligible(name));
        Assert.Contains(name, CommandRegistry.CreateAll(Options(), relayEligibleOnly: true).Select(c => c.Name));
    }

    /// <summary>
    /// The mirror-image defect: the old server list registered <c>skill</c>, which the client never
    /// relays, so the registration could never be reached. Eligibility now decides both sides.
    /// </summary>
    [Fact]
    public void Skill_IsNotRelayEligible_AndIsNotRegisteredOnTheServer()
    {
        Assert.False(CommandRegistry.IsRelayEligible("skill"));
        Assert.DoesNotContain("skill", CommandRegistry.CreateAll(Options(), relayEligibleOnly: true).Select(c => c.Name));
    }

    [Theory]
    [InlineData("serve")]      // would nest a server
    [InlineData("stop")]       // would kill the server being used
    [InlineData("install")]    // writes globally
    [InlineData("request")]    // writes outside the repo
    [InlineData("skill")]      // prints a static document
    public void PlumbingCommands_AreNotRelayed(string name) =>
        Assert.False(CommandRegistry.IsRelayEligible(name));

    /// <summary>
    /// An unknown first argument must reach the cold path so System.CommandLine can report it.
    /// Relaying it would hand the server something it cannot dispatch, which is how a typo used to
    /// come back as help text and exit 0.
    /// </summary>
    [Theory]
    [InlineData("refrences")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--list")]
    [InlineData("")]
    public void UnknownOrOptionArguments_AreNotRelayEligible(string arg) =>
        Assert.False(CommandRegistry.IsRelayEligible(arg));

    [Fact]
    public void IsRelayEligible_IsCaseSensitive() =>
        Assert.False(CommandRegistry.IsRelayEligible("References"));

    // ---------------- Relay eligibility across a full argument list ----------------

    /// <summary>
    /// The root's --solution/--format/--limit are recursive, so they parse ahead of the subcommand
    /// too. Deciding on args[0] would push every such invocation onto the cold path while a server
    /// sat idle.
    /// </summary>
    [Theory]
    [InlineData(new[] { "references", "Foo" }, "references")]
    [InlineData(new[] { "--format", "json", "cycles" }, "cycles")]
    [InlineData(new[] { "--solution", "app.slnx", "references", "Foo" }, "references")]
    [InlineData(new[] { "--limit", "5", "--format", "json", "surface-score" }, "surface-score")]
    public void FindCommandToken_LocatesTheCommandAfterGlobalOptions(string[] args, string expected)
    {
        Assert.Equal(expected, CommandRegistry.FindCommandToken(args));
        Assert.True(CommandRegistry.IsRelayEligible(args));
    }

    [Fact]
    public void FindCommandToken_IgnoresOptionsAndReturnsNullWhenNoCommandIsNamed()
    {
        Assert.Null(CommandRegistry.FindCommandToken(["--help"]));
        Assert.Null(CommandRegistry.FindCommandToken(["--solution", "app.slnx"]));
        Assert.Null(CommandRegistry.FindCommandToken([]));
        Assert.False(CommandRegistry.IsRelayEligible(["--solution", "app.slnx"]));
    }

    /// <summary>
    /// A symbol argument that happens to share a command's name must not steal the decision from
    /// the real command, which is why the scan runs left to right.
    /// </summary>
    [Fact]
    public void FindCommandToken_TakesTheFirstMatch_SoAnArgumentNamedLikeACommandCannotWin()
    {
        Assert.Equal("references", CommandRegistry.FindCommandToken(["references", "snapshot"]));
    }

    /// <summary>
    /// A root option's VALUE is not a command, even when it reads like one — the scan has to step
    /// over it and keep looking. In `reforge --solution references skill` the command is `skill`;
    /// matching the option's value instead would call the invocation relay-eligible and send it to
    /// a server that does not register `skill`, so the hot path would fail something the cold path
    /// runs fine.
    ///
    /// <para>Note this reports the command it finds whether or not that command is relayable —
    /// eligibility is <see cref="CommandRegistry.IsRelayEligible(string[])"/>'s question, asked of
    /// this answer. The last case returns null because the option consumed the only remaining
    /// argument, so there is no command token at all.</para>
    /// </summary>
    [Theory]
    [InlineData(new[] { "--solution", "references", "skill" }, "skill")]
    [InlineData(new[] { "--solution", "references", "callers", "Foo" }, "callers")]
    [InlineData(new[] { "--format", "cycles", "members", "Foo" }, "members")]
    [InlineData(new[] { "--limit", "snapshot" }, null)]
    public void FindCommandToken_SkipsTheValueOfAValueTakingRootOption(string[] args, string? expected)
    {
        Assert.Equal(expected, CommandRegistry.FindCommandToken(args));
    }

    /// <summary>
    /// The `--opt=value` form keeps its value in the same token, so nothing is consumed after it.
    /// </summary>
    [Fact]
    public void FindCommandToken_EqualsFormConsumesNoFollowingArgument()
    {
        Assert.Equal("references", CommandRegistry.FindCommandToken(["--solution=app.slnx", "references", "Foo"]));
    }

    /// <summary>
    /// The value-skip must not swallow a command that legitimately follows a valueless option.
    /// </summary>
    [Fact]
    public void FindCommandToken_ValuelessOptions_DoNotConsumeTheCommand()
    {
        Assert.Equal("references", CommandRegistry.FindCommandToken(["--verbose", "references", "Foo"]));
    }

    [Fact]
    public void IsRelayEligible_WhenAnOptionValueLooksLikeACommandAndTheRealOneIsPlumbing_IsFalse()
    {
        Assert.False(CommandRegistry.IsRelayEligible(["--solution", "references", "skill"]));
    }

    [Fact]
    public void IsRelayEligible_ForArgsNamingAPlumbingCommand_IsFalse()
    {
        Assert.False(CommandRegistry.IsRelayEligible(["--solution", "app.slnx", "install"]));
    }

    // ---------------- Server dispatch (no workspace involved) ----------------

    private static async Task<ServeCommand.RequestResult> DispatchAsync(params string[] args)
    {
        var request = ServerClient.FormatRequest(Directory.GetCurrentDirectory(), args);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource();
        return await ServeCommand.RunRequestAsync(reader, cts);
    }

    [Fact]
    public async Task Server_RefusesAnIneligibleCommand_WithNonZeroExit()
    {
        var result = await DispatchAsync("skill");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("skill", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("not a command the reforge server can run", result.Stderr, StringComparison.Ordinal);
        // Nothing that could be mistaken for a result.
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public async Task Server_RefusesAnUnknownCommand_WithNonZeroExit()
    {
        var result = await DispatchAsync("no-such-command", "--solution", "x.slnx");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no-such-command", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public async Task Server_RefusesAnEmptyCommand_WithNonZeroExit()
    {
        var result = await DispatchAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("empty command", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The arguments reach the registry as sent. This goes through the real frame, so it fails if
    /// the transport ever goes back to reconstructing a command line: under the old join-and-split
    /// the quote was deleted and the command name could be merged with its neighbour.
    /// </summary>
    [Fact]
    public async Task Server_ArgumentsContainingQuotesAndSpaces_ReachDispatchIntact()
    {
        var result = await DispatchAsync("skill", "--note", "say \"hi\"", "", "My Type");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("'skill' is not a command", result.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// An older client sends a bare command line with no framing. It must be refused rather than
    /// misparsed — its first line would otherwise be read as the working directory.
    /// </summary>
    [Fact]
    public async Task Server_RefusesAnUnframedRequest_WithNonZeroExit()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("references Foo\n"));
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource();

        var result = await ServeCommand.RunRequestAsync(reader, cts);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("framing", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("", result.Stdout);
    }

    /// <summary>
    /// Shutdown stays a bare line on purpose: `reforge stop` has to work against a server of any
    /// version, or the advertised fix for a version mismatch would itself need a matching build.
    /// </summary>
    [Fact]
    public async Task Server_ShutdownSentinel_AcksAndCancels_WithoutFraming()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("__shutdown__\n"));
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource();

        var result = await ServeCommand.RunRequestAsync(reader, cts);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("shutting down", result.Stdout, StringComparison.Ordinal);
        Assert.True(cts.IsCancellationRequested);
    }
}
