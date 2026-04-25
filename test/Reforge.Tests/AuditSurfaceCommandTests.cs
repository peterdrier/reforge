using System.CommandLine;
using System.Text.Json;
using Reforge;
using Reforge.Commands;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class AuditSurfaceCommandTests
{
    private readonly SampleSolutionFixture _fixture;

    public AuditSurfaceCommandTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuditSurface_OnInterface_ListsMethodsAndImplementations()
    {
        var output = await RunAsync("IUserService", OutputFormat.Json);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.Equal("interface", root.GetProperty("kind").GetString());

        var impls = root.GetProperty("implementations").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet();
        Assert.Contains("SampleSolution.Services.UserService", impls);
        Assert.Contains("SampleSolution.Services.CachedUserService", impls);

        var methods = root.GetProperty("methods").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()!).ToHashSet();
        Assert.Contains("GetUserAsync", methods);
        Assert.Contains("GetAllUsersAsync", methods);
    }

    [Fact]
    public async Task AuditSurface_OnInterface_AggregatesCallersThroughInterfaceAndImplementations()
    {
        var output = await RunAsync("IUserService", OutputFormat.Json);
        using var doc = JsonDocument.Parse(output);
        var method = doc.RootElement.GetProperty("methods").EnumerateArray()
            .First(m => m.GetProperty("name").GetString() == "GetUserAsync");

        // GetUserAsync is called by UserController, OrderService, and CachedUserService.GetUserAsync.
        // None of them are in test paths.
        var prodCount = method.GetProperty("callers").GetProperty("prodCount").GetInt32();
        var testCount = method.GetProperty("callers").GetProperty("testCount").GetInt32();
        Assert.True(prodCount >= 3, $"expected >=3 prod callers, got {prodCount}");
        Assert.Equal(0, testCount);
    }

    [Fact]
    public async Task AuditSurface_OnClass_ClassifiesBodyShape()
    {
        var output = await RunAsync("UserService", OutputFormat.Json);
        using var doc = JsonDocument.Parse(output);
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("bodyShape").GetString());

        // SaveChangesAsync → write
        Assert.Equal("write", methods["UpdateUserNameAsync"]);
        Assert.Equal("write", methods["UpdateUserEmailAsync"]);
        // _dbContext.AuditLogs.Add → write
        Assert.Equal("write", methods["LogAudit"]);
        // Single _logger.LogInfo (ILogger is not a repo) → passthrough-service (issue #7)
        Assert.Equal("passthrough-service", methods["OnUserLoaded"]);
        // Single LINQ chain over _dbContext.Users (DbContext) → linq-over-repo (issue #7)
        Assert.Equal("linq-over-repo", methods["FindByInterpolation"]);
        // _cache.Remove is not a DbSet write — should NOT be classified as "write"
        Assert.NotEqual("write", methods["EvictUserCache"]);
    }

    [Fact]
    public async Task AuditSurface_OnClass_DistinguishesRepoServiceAndSelf()
    {
        // Issue #7: passthrough split — repo (IUserRepository) vs service (ILogger) vs self (private helper).
        var output = await RunAsync("UserService", OutputFormat.Json);
        using var doc = JsonDocument.Parse(output);
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("bodyShape").GetString());

        // _userRepository.FindByIdAsync — IUserRepository ends in "Repository" → repo
        Assert.Equal("passthrough-repo", methods["GetByRepoAsync"]);
        // EvictUserCache(id) — bare identifier invocation on `this` → self
        Assert.Equal("passthrough-self", methods["EvictUserCacheById"]);
    }

    [Fact]
    public async Task AuditSurface_OnClass_LinqOverServiceClassified()
    {
        // Issue #7: linq-over split — LINQ over the result of a service call should be linq-over-service.
        var output = await RunAsync("CachedUserService", OutputFormat.Json);
        using var doc = JsonDocument.Parse(output);
        var methods = doc.RootElement.GetProperty("methods").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("bodyShape").GetString());

        Assert.Equal("linq-over-service", methods["CountActiveAsync"]);
    }

    [Fact]
    public async Task AuditSurface_OnInterface_DoesNotEmitBodyShape()
    {
        var output = await RunAsync("IUserService", OutputFormat.Json);
        using var doc = JsonDocument.Parse(output);

        foreach (var m in doc.RootElement.GetProperty("methods").EnumerateArray())
        {
            // bodyShape is omitted entirely for interfaces (DefaultIgnoreCondition.WhenWritingNull).
            Assert.False(m.TryGetProperty("bodyShape", out _),
                $"interface method {m.GetProperty("name").GetString()} unexpectedly carries bodyShape");
        }
    }

    [Fact]
    public async Task AuditSurface_NotFound_EmitsHelpfulMessage()
    {
        var output = await RunAsync("ZzzNoSuchType", OutputFormat.Compact);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> RunAsync(string symbol, OutputFormat format)
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
            root.Add(AuditSurfaceCommand.Create(solutionOption, formatOption, limitOption));

            var args = format == OutputFormat.Json
                ? new[] { "audit-surface", symbol, "--format", "json" }
                : new[] { "audit-surface", symbol };

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
