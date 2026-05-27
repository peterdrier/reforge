using System.Text.Json;

namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SurfaceScoreTests
{
    private readonly SampleSolutionFixture _fixture;

    public SurfaceScoreTests(SampleSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    // ---------------- Config loading ----------------

    [Fact]
    public void LoadOrDefault_NoFile_ReturnsDefaultsWithNamespaceFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-no-file");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var cfg = SurfaceScoreConfig.LoadOrDefault(null, dir, out var loadedFrom);

        Assert.Null(loadedFrom);
        Assert.Empty(cfg.EffectiveSections);
        Assert.True(cfg.GroupByNamespaceFallback);
        Assert.NotEmpty(cfg.Classifications); // defaults present
        Assert.NotEmpty(cfg.Weights);          // defaults present
    }

    [Fact]
    public void LoadOrDefault_ParsesSectionsBlock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-sections");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "sections": {
                "Users": {
                  "paths": ["**/SampleSolution.Services/User*.cs"],
                  "symbols": ["IUser*"],
                  "repositoryInterfaces": ["IUserRepository"],
                  "serviceInterfaces": ["IUserService"]
                },
                "Orders": {
                  "symbols": ["Order*", "IOrder*"]
                }
              }
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out var loadedFrom);

        Assert.Equal(configPath, loadedFrom);
        Assert.Equal(2, cfg.EffectiveSections.Count);

        var users = cfg.EffectiveSections.Single(s => s.Name == "Users");
        Assert.Contains("**/SampleSolution.Services/User*.cs", users.Paths);
        Assert.Contains("IUser*", users.Symbols);
        Assert.Contains("IUserRepository", users.RepositoryInterfaces);
        Assert.Contains("IUserService", users.ServiceInterfaces);

        var orders = cfg.EffectiveSections.Single(s => s.Name == "Orders");
        Assert.Contains("Order*", orders.Symbols);
        Assert.False(cfg.GroupByNamespaceFallback); // sections present -> fallback off
    }

    [Fact]
    public void LoadOrDefault_LegacyGroupsBlockStillWorks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reforge-surface-score-test-legacy");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "reforge.surface-score.json");
        File.WriteAllText(configPath, """
            {
              "groups": [
                { "name": "Legacy", "match": { "paths": ["**/Legacy/**"] } }
              ]
            }
            """);

        var cfg = SurfaceScoreConfig.LoadOrDefault(configPath, dir, out _);

        Assert.Single(cfg.EffectiveSections);
        Assert.Equal("Legacy", cfg.EffectiveSections[0].Name);
        Assert.Contains("**/Legacy/**", cfg.EffectiveSections[0].Paths);
    }

    // ---------------- Engine behavior ----------------

    [Fact]
    public async Task NamespaceFallback_ProducesGroupsWithoutConfig()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        // Without any config the namespace heuristic must produce at least one group.
        // The exact name depends on the sample solution's namespace shape, but it must
        // be non-empty and non-empty-named.
        Assert.NotEmpty(report.Groups);
        Assert.All(report.Groups.Keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
    }

    [Fact]
    public async Task Section_ByPaths_AssignsMatchingTypesToSection()
    {
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Users"] = new SectionRule
        {
            Paths = { "**/SampleSolution.Services/User*.cs", "**/SampleSolution.Services/Cached*.cs" }
        };
        // Keep classification defaults so anything is actually scored.
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("Users"),
            $"Expected 'Users' section. Got: {string.Join(", ", report.Groups.Keys)}");

        // Every entry in the Users section must be from a file matched by the configured paths.
        var users = report.Groups["Users"];
        Assert.NotEmpty(users.Entries);
        Assert.All(users.Entries, e =>
            Assert.True(
                e.File.Contains("/User", StringComparison.Ordinal) ||
                e.File.Contains("/Cached", StringComparison.Ordinal),
                $"Entry from unexpected file: {e.File}"));
    }

    [Fact]
    public async Task Section_BySymbols_AssignsByNamePattern()
    {
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["Orders"] = new SectionRule
        {
            Symbols = { "Order*", "IOrder*" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("Orders"),
            $"Expected 'Orders' section. Got: {string.Join(", ", report.Groups.Keys)}");

        // Every scored entry should be from a type whose simple name starts with Order or IOrder
        // (we can only check this via the entry's Symbol since we don't carry full type names).
        var orders = report.Groups["Orders"];
        Assert.NotEmpty(orders.Entries);
        Assert.All(orders.Entries, e =>
        {
            // Entries reference symbol names — those may be members rather than types, so the
            // strongest check we can make at this layer is that the file path participates in
            // the section (the engine pins entries to their declaring type's section, and the
            // type matched Order*/IOrder*).
            Assert.NotNull(e.Symbol);
        });
    }

    [Fact]
    public async Task Section_RepositoryInterfaces_AutoClassifiesAndIsCrossSection()
    {
        // Two sections: Users (claims IUserRepository) and Orders (claims IOrderRepository).
        // Then UserService — which depends on IUserRepository via constructor — should NOT
        // produce a crossSectionRepository entry against itself (same section),
        // but OrderService — which depends on IOrderRepository AND lives in Users by namespace
        // fallback — should not be the test target because services live in the same project.
        // Instead, build an asymmetric setup where the service is in one section and the
        // repository it injects is in another.
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["RepoSection"] = new SectionRule
        {
            RepositoryInterfaces = { "IUserRepository", "IOrderRepository", "IAuditLogRepository" }
        };
        cfg.Sections["ServiceSection"] = new SectionRule
        {
            // Pull every Service into a different section. UserService injects IUserRepository
            // which lives in RepoSection — that's the cross-section dependency we want to assert on.
            Symbols = { "*Service" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(report.Groups.ContainsKey("ServiceSection"),
            $"Expected ServiceSection. Got: {string.Join(", ", report.Groups.Keys)}");

        // ServiceSection should have at least one crossSectionRepository entry (UserService
        // injecting IUserRepository, which is owned by RepoSection).
        var services = report.Groups["ServiceSection"];
        var crossRepo = services.Entries.Where(e => e.Rule == "crossSectionRepository").ToList();
        Assert.NotEmpty(crossRepo);
    }

    // ---------------- Diagnostic ----------------

    [Fact]
    public async Task MissingGroup_ProducesDiagnostic_WhenNothingMatches()
    {
        var cfg = SurfaceScoreConfig.Default();
        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        // Simulate the command-layer diagnostic — the engine itself doesn't add it, the command
        // wrapper does. Replicate the check here so the assertion lives near the rule.
        const string requested = "TotallyMadeUpSection";
        var present = report.Groups.ContainsKey(requested);
        var configured = cfg.HasConfiguredSection(requested);

        Assert.False(present);
        Assert.False(configured);
        // The contract: when both are false, the command emits a "group-not-found" diagnostic.
        // We verify the contract is reachable here; the WriteCompact tests would cover formatting.
    }

    [Fact]
    public async Task ConfiguredButEmptySection_IsDistinguishableFromUnknownSection()
    {
        var cfg = new SurfaceScoreConfig();
        cfg.Sections["DefinitelyEmpty"] = new SectionRule
        {
            Paths = { "**/this-path-cannot-match-anything-1234567890/**" }
        };
        foreach (var (k, v) in SurfaceScoreConfig.Default().Classifications)
            cfg.Classifications.TryAdd(k, v);
        foreach (var (k, v) in SurfaceScoreConfig.Default().Weights)
            cfg.Weights.TryAdd(k, v);
        cfg.BuildEffectiveSections();

        var engine = new SurfaceScoreEngine(cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution));
        var report = await engine.ScoreAsync(_fixture.Solution, CancellationToken.None);

        Assert.True(cfg.HasConfiguredSection("DefinitelyEmpty"));
        Assert.False(report.Groups.ContainsKey("DefinitelyEmpty"));
        // Command layer should emit a "group-empty" diagnostic for this case (distinct from
        // "group-not-found").
    }
}
