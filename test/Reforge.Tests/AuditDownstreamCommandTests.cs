using System.CommandLine;
using System.Text.Json;
using Reforge;
using Reforge.Commands;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class AuditDownstreamCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public AuditDownstreamCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuditDownstream_OnInterface_Errors()
    {
        var output = await RunAsync("IUserService");
        Assert.Contains("only supports classes", output);
    }

    [Fact]
    public async Task AuditDownstream_ClassifiesDbSetReadsAndWrites()
    {
        using var doc = JsonDocument.Parse(await RunAsync("UserService", OutputFormat.Json));
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

        // Pure read — no SaveChanges, no Add/Remove/Update
        Assert.Contains("Users", DbReads(methods["GetUserAsync"]));
        Assert.Empty(DbWrites(methods["GetUserAsync"]));

        // Read into local + mutation + SaveChangesAsync → upgraded to write
        Assert.Contains("Users", DbWrites(methods["UpdateUserNameAsync"]));
        Assert.Empty(DbReads(methods["UpdateUserNameAsync"]));

        // Explicit AuditLogs.Add → write
        Assert.Contains("AuditLogs", DbWrites(methods["LogAudit"]));
    }

    [Fact]
    public async Task AuditDownstream_RecordsDependencyCalls()
    {
        using var doc = JsonDocument.Parse(await RunAsync("UserService", OutputFormat.Json));
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

        var calls = methods["GetUserWithPrivilegeCheckAsync"]
            .GetProperty("calls").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Contains(calls, c => c.Contains("_userRepository.FindByIdAsync"));
        Assert.Contains(calls, c => c.Contains("_logger.LogInfo"));
    }

    [Fact]
    public async Task AuditDownstream_TracksPrivateHelpers()
    {
        using var doc = JsonDocument.Parse(await RunAsync("UserService", OutputFormat.Json));
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

        var calls = methods["DeactivateUserAsync"]
            .GetProperty("calls").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Contains(calls, c => c.Contains("EvictUserCache") && c.Contains("private"));
    }

    [Fact]
    public async Task AuditDownstream_PropagatesDbSetThroughRepoCallWithViaAttribution()
    {
        // Issue #8: service.GetCountAsync calls _repo.CountAsync; repo body reads AuditLogs.
        // The read should bubble up to the service's dbSets with via=_repo.CountAsync.
        using var doc = JsonDocument.Parse(await RunAsync("AuditLogQueryService", OutputFormat.Json));
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

        var reads = DbEntries(methods["GetCountAsync"], "reads");
        Assert.Contains(reads, e => e.Table == "AuditLogs" && (e.Via?.Contains("CountAsync") ?? false));

        var writes = DbEntries(methods["PurgeAsync"], "writes");
        Assert.Contains(writes, e => e.Table == "AuditLogs" && (e.Via?.Contains("PurgeAsync") ?? false));
    }

    [Fact]
    public async Task AuditDownstream_SurfacesUntracedRepoToRepoCalls()
    {
        // Issue #8: AuditLogRepository.CountUsersAsync calls _userRepo.GetAllAsync (repo→repo).
        // The trace stops at one hop and surfaces the chain bottom in untracedRepoCalls.
        using var doc = JsonDocument.Parse(await RunAsync("AuditLogQueryService", OutputFormat.Json));
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

        var untraced = methods["GetUserCountAsync"].GetProperty("dbSets")
            .GetProperty("untracedRepoCalls").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        Assert.Contains(untraced, c => c.Contains("_userRepo.GetAllAsync"));
    }

    [Fact]
    public async Task AuditDownstream_DetectsDbSetAccessThroughIDbContextFactoryLocal()
    {
        // Issue #8: var db = await _factory.CreateDbContextAsync(); db.AuditLogs.Count();
        // The DbSet access lives on a local of DbContext type — semantic-model lookup
        // (rather than field-name match) should still resolve it as a read.
        using var doc = JsonDocument.Parse(await RunAsync("AuditLogQueryService", OutputFormat.Json));
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m);

        var reads = DbEntries(methods["CountWithFactoryAsync"], "reads")
            .Select(e => e.Table).ToHashSet();

        Assert.Contains("AuditLogs", reads);
    }

    private static HashSet<string> DbReads(JsonElement method) =>
        method.GetProperty("dbSets").GetProperty("reads").EnumerateArray()
            .Select(e => e.GetProperty("table").GetString()!).ToHashSet();

    private static HashSet<string> DbWrites(JsonElement method) =>
        method.GetProperty("dbSets").GetProperty("writes").EnumerateArray()
            .Select(e => e.GetProperty("table").GetString()!).ToHashSet();

    private static List<(string Table, string? Via)> DbEntries(JsonElement method, string kind) =>
        method.GetProperty("dbSets").GetProperty(kind).EnumerateArray()
            .Select(e =>
            {
                var via = e.TryGetProperty("via", out var v) && v.ValueKind != JsonValueKind.Null
                    ? v.GetString()
                    : null;
                return (Table: e.GetProperty("table").GetString()!, Via: via);
            })
            .ToList();

    private async Task<string> RunAsync(string symbol, OutputFormat format = OutputFormat.Compact)
    {
        WorkspaceHelper.HotSolution = _fixture.Solution;
        try
        {
            var solutionOption = new Option<string?>("--solution") { Recursive = true };
            var formatOption = new Option<OutputFormat>("--format")
            {
                DefaultValueFactory = _ => OutputFormat.Compact,
                Recursive = true
            };
            var limitOption = new Option<int?>("--limit") { Recursive = true };

            var root = new RootCommand
            {
                solutionOption,
                formatOption,
                limitOption
            };
            root.Add(AuditDownstreamCommand.Create(solutionOption, formatOption, limitOption));

            var args = format == OutputFormat.Json
                ? new[] { "audit-downstream", symbol, "--format", "json" }
                : new[] { "audit-downstream", symbol };

            var sw = new StringWriter();
            var origOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                var parse = root.Parse(args);
                await parse.InvokeAsync();
            }
            finally
            {
                Console.SetOut(origOut);
            }
            return sw.ToString();
        }
        finally
        {
            WorkspaceHelper.HotSolution = null;
        }
    }
}
