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

    // ---------------- Server dispatch (no workspace involved) ----------------

    private static async Task<(int ExitCode, string Output)> DispatchAsync(string commandLine)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(commandLine + "\n"));
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource();
        return await ServeCommand.RunRequestAsync(reader, cts);
    }

    [Fact]
    public async Task Server_RefusesAnIneligibleCommand_WithNonZeroExit()
    {
        var (exitCode, output) = await DispatchAsync("skill");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("skill", output, StringComparison.Ordinal);
        Assert.Contains("not a command the reforge server can run", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_RefusesAnUnknownCommand_WithNonZeroExit()
    {
        var (exitCode, output) = await DispatchAsync("no-such-command --solution x.slnx");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("no-such-command", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_RefusesAnEmptyCommand_WithNonZeroExit()
    {
        var (exitCode, output) = await DispatchAsync("");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("empty command", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_ShutdownSentinel_AcksAndCancels()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("__shutdown__\n"));
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource();

        var (exitCode, output) = await ServeCommand.RunRequestAsync(reader, cts);

        Assert.Equal(0, exitCode);
        Assert.Contains("shutting down", output, StringComparison.Ordinal);
        Assert.True(cts.IsCancellationRequested);
    }
}
