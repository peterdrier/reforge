using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

public class DtoInventoryTests
{
    private static INamedTypeSymbol Dto(string name) => _comp.Value.GetTypeByMetadataName(name)!;

    private static readonly Lazy<CSharpCompilation> _comp = new(() =>
    {
        var tree = CSharpSyntaxTree.ParseText("""
            using System; using System.Collections.Generic;
            public sealed class CampInfo {
                public Guid Id { get; set; }
                public List<CampSeasonInfo> Seasons { get; set; }
                public CampSeasonInfo CurrentSeason { get; set; }
                public List<string> ImageUrls { get; set; }
            }
            public sealed class CampSeasonInfo {
                public int Year { get; set; }
                public List<CampMemberInfo> Members { get; set; }
                public CampInfo Parent { get; set; }   // cycle back to CampInfo
            }
            public sealed class CampMemberInfo { public Guid UserId { get; set; } }

            // Data lives entirely on the base — the derived type declares nothing.
            public abstract class RateBase { public string Band { get; set; } }
            public sealed class SeasonalRateInfo : RateBase { }

            // Shadowed property: the most-derived declaration is the one fact, not two.
            public class ShadowBase { public string Note { get; set; } }
            public sealed class ShadowInfo : ShadowBase { public new string Note { get; set; } }

            // An indexer and a static are not facts a path can name on an instance.
            public class OddBase { public static string Global { get; set; } public string this[int i] => ""; }
            public sealed class OddInfo : OddBase { public string Real { get; set; } }
            """);
        return CSharpCompilation.Create("t", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
    });

    [Fact]
    public void Build_ProducesNestedCollectionPaths()
    {
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "CampInfo", "CampSeasonInfo", "CampMemberInfo" };
        var paths = DtoInventory.Build(Dto("CampInfo"), canonical, maxDepth: 5);

        Assert.Contains("CampInfo.Id", paths);
        Assert.Contains("CampInfo.Seasons[].Year", paths);
        Assert.Contains("CampInfo.Seasons[].Members[].UserId", paths);
        Assert.Contains("CampInfo.CurrentSeason.Year", paths);
        Assert.Contains("CampInfo.ImageUrls[]", paths);
    }

    [Fact]
    public void Build_IncludesInheritedProperties()
    {
        // The conservation gate proves facts against these paths. A DTO whose data lives on a
        // data-only base would otherwise anchor an EMPTY inventory, so every inherited fact reads
        // as absent and a refactor that dropped one would pass unnoticed.
        var paths = DtoInventory.Build(Dto("SeasonalRateInfo"), new HashSet<string>(StringComparer.Ordinal));

        Assert.Contains("SeasonalRateInfo.Band", paths);
    }

    [Fact]
    public void Build_ShadowedProperty_YieldsOnePath()
    {
        var paths = DtoInventory.Build(Dto("ShadowInfo"), new HashSet<string>(StringComparer.Ordinal));

        Assert.Single(paths, p => p == "ShadowInfo.Note");
    }

    [Fact]
    public void Build_SkipsIndexersAndStatics()
    {
        // Newly reachable now that the walk climbs base types — neither names a fact about an
        // instance, and an indexer's symbol name would emit a nonsense path.
        var paths = DtoInventory.Build(Dto("OddInfo"), new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(new[] { "OddInfo.Real" }, paths);
    }

    [Fact]
    public void Build_StopsAtCycle()
    {
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "CampInfo", "CampSeasonInfo", "CampMemberInfo" };
        var paths = DtoInventory.Build(Dto("CampInfo"), canonical, maxDepth: 10);
        Assert.DoesNotContain(paths, p => p.Contains("Parent.Seasons[].Members[].UserId"));
        Assert.Contains("CampInfo.Seasons[].Parent", paths);
    }
}
