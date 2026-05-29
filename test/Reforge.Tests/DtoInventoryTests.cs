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
    public void Build_StopsAtCycle()
    {
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "CampInfo", "CampSeasonInfo", "CampMemberInfo" };
        var paths = DtoInventory.Build(Dto("CampInfo"), canonical, maxDepth: 10);
        Assert.DoesNotContain(paths, p => p.Contains("Parent.Seasons[].Members[].UserId"));
        Assert.Contains("CampInfo.Seasons[].Parent", paths);
    }
}
