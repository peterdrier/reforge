namespace Reforge.Tests;

[Collection("SampleSolution")]
public class SolutionClassifierTests
{
    private readonly SampleSolutionFixture _fixture;
    public SolutionClassifierTests(SampleSolutionFixture fixture) => _fixture = fixture;

    private async Task<List<ClassifiedType>> ClassifyAsync()
    {
        var cfg = SurfaceScoreConfig.Default();
        return (await SolutionClassifier.ClassifyAsync(
            _fixture.Solution, cfg, LocationHelper.GetSolutionDirectory(_fixture.Solution), CancellationToken.None)).ToList();
    }

    [Fact]
    public async Task ClassifyAsync_TagsKnownTypes()
    {
        var classified = await ClassifyAsync();

        Assert.Contains(classified, c => c.Type.Name == "UserService" && c.Tags.Contains("applicationService"));
        // IUserService declares two Get* methods and nothing else, so it is a READ service interface.
        // It was classified as a full (write-capable) one until #54, purely because the name pattern
        // for `fullServiceInterface` is `I*Service` and the read escape hatch only caught
        // `I*ServiceRead` / `I*ReadService` / `I*QueryService`.
        Assert.Contains(classified, c => c.Type.Name == "IUserService" && c.Tags.Contains("readServiceInterface"));
        Assert.DoesNotContain(classified, c => c.Type.Name == "IUserService" && c.Tags.Contains("fullServiceInterface"));
        // Uniqueness is per (assembly, display name) — NOT per display name. Two assemblies may
        // legitimately declare the same fully qualified name; both must survive classification.
        var keys = classified
            .Select(c => $"{c.Type.ContainingAssembly?.Name}|{c.Type.ToDisplayString()}")
            .ToList();
        Assert.Equal(keys.Distinct().Count(), classified.Count);
    }

    [Fact]
    public async Task ClassifyAsync_WriteCapableServiceInterface_StaysFullService()
    {
        var classified = await ClassifyAsync();

        // IGreetingService declares RecordGreetingAsync — a Task returning no data, which
        // ImplementationComplexity.IsMutation reads as a command. The name is identical in shape to
        // IUserService, so only the behavior of the implementation separates them. That is the point:
        // the classification is rename-proof in both directions.
        Assert.Contains(classified, c => c.Type.Name == "IGreetingService" && c.Tags.Contains("fullServiceInterface"));
        Assert.DoesNotContain(classified, c => c.Type.Name == "IGreetingService" && c.Tags.Contains("readServiceInterface"));
    }

    [Fact]
    public async Task ClassifyAsync_ServiceInterfaceWithNoImplementation_KeepsFullService()
    {
        var classified = await ClassifyAsync();

        // ICampBillingService declares only `int BalanceFor(int)` — read-shaped — but nothing in the
        // solution implements it. Demotion requires evidence of read-only-ness, and an unimplemented
        // interface supplies none: the walk skips test projects and cannot see other assemblies, so
        // "no implementation found" means unknown. Repricing surface on an analysis gap is the failure
        // mode #51 fixed elsewhere, and this asserts it is not reintroduced here.
        Assert.Contains(classified, c => c.Type.Name == "ICampBillingService" && c.Tags.Contains("fullServiceInterface"));
    }

    [Fact]
    public async Task ClassifyAsync_SameTypeNameInTwoAssemblies_KeepsBoth()
    {
        var classified = await ClassifyAsync();

        // SampleSolution.Shared.SectionMarker is declared internally in BOTH .Dorm and .Tent.
        // Deduping on the display name alone dropped whichever was enumerated second.
        var markers = classified.Where(c => c.Type.ToDisplayString() == "SampleSolution.Shared.SectionMarker").ToList();

        Assert.Equal(2, markers.Count);
        Assert.Equal(new[] { "Dorm", "Tent" }, markers.Select(m => m.Group).OrderBy(g => g, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClassifyAsync_GroupsByContainingAssembly_WithNoSectionConfig()
    {
        var classified = await ClassifyAsync();

        // Group == the declaring assembly, common solution prefix stripped. Not the namespace,
        // and not the project that happened to enumerate the type first.
        Assert.Equal("Services", Group(classified, "UserService"));
        Assert.Equal("Core", Group(classified, "IUserRepository"));
        Assert.Equal("Camp", Group(classified, "CampSectionService"));
        Assert.Equal("Reporting", Group(classified, "CampReportBuilder"));

        Assert.All(classified, c => Assert.DoesNotContain('.', c.Group));
    }

    [Fact]
    public async Task ClassifyAsync_FoldsContractsAssemblyIntoParentSection()
    {
        var classified = await ClassifyAsync();

        // ICampServiceRead + CampInfo are declared in SampleSolution.Camp.Contracts; they must
        // land in the SAME section as ICampSectionService over in SampleSolution.Camp.
        Assert.Equal("Camp", Group(classified, "ICampServiceRead"));
        Assert.Equal("Camp", Group(classified, "CampInfo"));
        Assert.DoesNotContain(classified, c => c.Group.Contains("Contracts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TypeKey_DistinguishesSameNameInDifferentAssemblies()
    {
        var classified = await ClassifyAsync();
        var markers = classified.Where(c => c.Type.ToDisplayString() == "SampleSolution.Shared.SectionMarker").ToList();

        // Keeping both types in `classified` is only half the fix — the lookup maps the scoring
        // passes consult must also tell them apart, or a consumer resolves to the wrong section
        // and the cross-section rules fire (or stay silent) against the wrong assembly.
        var keys = markers.Select(m => SolutionClassifier.TypeKey(m.Type)).ToList();

        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.Contains("SampleSolution.Shared.SectionMarker", k));
    }

    [Fact]
    public async Task ClassifyAsync_ExcludesTestAssemblies()
    {
        var classified = await ClassifyAsync();
        Assert.DoesNotContain(classified, c => c.Group.Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

    private static string Group(List<ClassifiedType> classified, string typeName)
        => classified.Single(c => c.Type.Name == typeName).Group;

    // ---------------- Assembly -> section derivation ----------------

    [Fact]
    public void AssemblySections_StripsSharedPrefix()
    {
        var map = AssemblySections.Resolve(new[] { "Humans.Application", "Humans.Web", "Humans.Store" });

        Assert.Equal("Application", map["Humans.Application"]);
        Assert.Equal("Web", map["Humans.Web"]);
        Assert.Equal("Store", map["Humans.Store"]);
    }

    [Fact]
    public void AssemblySections_FoldsContractsIntoParent()
    {
        var map = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Store.Contracts", "Humans.Web" });

        Assert.Equal("Store", map["Humans.Store"]);
        Assert.Equal("Store", map["Humans.Store.Contracts"]);
    }

    [Fact]
    public void AssemblySections_MonolithRemainder_KeepsItsOwnName()
    {
        // The not-yet-split assembly IS the shared prefix; stripping would leave nothing, so it
        // keeps its last segment rather than collapsing to an empty section name.
        var map = AssemblySections.Resolve(new[] { "Humans", "Humans.Store", "Humans.Store.Contracts" });

        Assert.Equal("Humans", map["Humans"]);
        Assert.Equal("Store", map["Humans.Store"]);
        Assert.Equal("Store", map["Humans.Store.Contracts"]);
    }

    [Fact]
    public void AssemblySections_PrefixStrippingCollision_KeepsSectionsDistinct()
    {
        // `Company.Product` is consumed entirely by the shared prefix (falls back to its last
        // segment) while `Company.Product.Product` strips to its tail — both would be `Product`.
        // Two unrelated assemblies must not land in one section.
        var map = AssemblySections.Resolve(new[] { "Company.Product", "Company.Product.Product" });

        Assert.NotEqual(map["Company.Product"], map["Company.Product.Product"]);
    }

    [Fact]
    public void AssemblySections_CollisionGuard_DoesNotBreakContractsFold()
    {
        // The collision guard keys on the FOLDED name, so an intended `X` + `X.Contracts`
        // collapse must survive it even when it looks like a duplicate section name.
        var map = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Store.Contracts", "Humans.Web" });

        Assert.Equal(map["Humans.Store"], map["Humans.Store.Contracts"]);
        Assert.Equal("Store", map["Humans.Store"]);
    }

    [Fact]
    public void AssemblySections_NoSharedPrefix_KeepsFullNames()
    {
        var map = AssemblySections.Resolve(new[] { "Alpha.Core", "Beta.Core" });

        Assert.Equal("Alpha.Core", map["Alpha.Core"]);
        Assert.Equal("Beta.Core", map["Beta.Core"]);
    }

    [Fact]
    public void AssemblySections_IgnoresAssembliesThatDeclareNothing()
    {
        // Regression: Humans.slnx carries a `docs` project with no C#. Feeding it into the
        // prefix calculation left every section named "Humans.<X>". Only type-declaring
        // assemblies are passed in, so the prefix survives.
        var map = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Web" });
        Assert.Equal("Store", map["Humans.Store"]);

        var polluted = AssemblySections.Resolve(new[] { "Humans.Store", "Humans.Web", "docs" });
        Assert.Equal("Humans.Store", polluted["Humans.Store"]);
    }

    [Fact]
    public void AssemblySections_SingleAssembly_UsesLastSegment()
    {
        var map = AssemblySections.Resolve(new[] { "Contoso.Billing" });
        Assert.Equal("Billing", map["Contoso.Billing"]);
    }

    // ---------------- Section facts (policy overrides over derived repo-backing) ----------------

    private static IReadOnlySet<string> RepoSections(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SectionFacts_RepoBacked_FromDeclaredRepository()
    {
        var facts = SectionFacts.For("Camp", SectionRule.None, RepoSections("Camp"));
        Assert.True(facts.RepoBacked);
        Assert.True(facts.RequiresReadSurface);
        Assert.True(facts.RequiresWriteSurface);
        Assert.True(facts.RequiresPrimaryInfoDto);
    }

    [Fact]
    public void SectionFacts_OrchestratorOnly_NotRequired()
    {
        var facts = SectionFacts.For("Reporting", SectionRule.None, RepoSections());
        Assert.False(facts.RepoBacked);
        Assert.False(facts.RequiresReadSurface);
    }

    [Fact]
    public void SectionFacts_RequiresOverride_Wins()
    {
        var facts = SectionFacts.For("Reporting", new SectionRule { RequiresReadSurface = true }, RepoSections());
        Assert.False(facts.RepoBacked);
        Assert.True(facts.RequiresReadSurface);
    }
}
