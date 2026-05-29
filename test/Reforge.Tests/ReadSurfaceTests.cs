using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Reforge.Tests;

public class ReadSurfaceTests
{
    private static IMethodSymbol Method(string iface, string member)
    {
        var tree = CSharpSyntaxTree.ParseText($$"""
            using System; using System.Threading.Tasks; using System.Collections.Generic;
            public sealed class CampInfo { public Guid Id { get; set; } }
            public sealed class CampSettingsInfo { public int Year { get; set; } }
            public sealed class CampSummary { public string Name { get; set; } }
            public sealed class CampSearchHit { public Guid Id { get; set; } }
            public sealed class CampSearchQuery { public string Term { get; set; } public int Page { get; set; } }
            public sealed class CampDetailData { public string Html { get; set; } }
            public sealed class CampActionResult { public bool Ok { get; set; } }
            public interface {{iface}} { {{member}} }
            """);
        var comp = CSharpCompilation.Create("t", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var sym = comp.GetTypeByMetadataName(iface)!;
        return sym.GetMembers().OfType<IMethodSymbol>().First(m => m.MethodKind == MethodKind.Ordinary);
    }

    [Theory]
    [InlineData("Task<bool> IsUserCampLeadAsync(Guid u);", ReadMethodKind.Predicate)]
    [InlineData("Task<Guid> GetCampLeadSeasonIdAsync(Guid c);", ReadMethodKind.ScalarFact)]
    [InlineData("Task<List<CampSummary>> GetCampSummariesForYearAsync(int y);", ReadMethodKind.ProjectionSummary)]
    [InlineData("Task<CampInfo> GetByIdAsync(Guid id);", ReadMethodKind.PrimitiveRead)]
    [InlineData("Task<CampSettingsInfo> GetSettingsAsync(Guid c);", ReadMethodKind.SettingsRead)]
    [InlineData("Task<List<CampSearchHit>> SearchAsync(CampSearchQuery q);", ReadMethodKind.Search)]
    [InlineData("Task<CampDetailData> GetDetailAsync(Guid id);", ReadMethodKind.UiBuilder)]
    [InlineData("Task<CampActionResult> FindAsync(CampSearchQuery q);", ReadMethodKind.ProjectionSummary)]
    public void Classify_AssignsExpectedKind(string member, ReadMethodKind expected)
    {
        var m = Method("ICampServiceRead", member);
        var kind = ReadSurface.Classify(m, primaryInfoDto: "CampInfo", settingsInfoDto: "CampSettingsInfo");
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Classify_SearchNamedButWrongShape_IsProjectionNotSearch()
    {
        var m = Method("ICampServiceRead", "Task<CampSummary> SearchOneAsync(string term);");
        var kind = ReadSurface.Classify(m, "CampInfo", "CampSettingsInfo");
        Assert.Equal(ReadMethodKind.ProjectionSummary, kind);
    }

    [Fact]
    public void IsCharged_TrueForProjectionPredicateScalarUiBuilder()
    {
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.ProjectionSummary));
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.Predicate));
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.ScalarFact));
        Assert.True(ReadSurface.IsCharged(ReadMethodKind.UiBuilder));
        Assert.False(ReadSurface.IsCharged(ReadMethodKind.PrimitiveRead));
        Assert.False(ReadSurface.IsCharged(ReadMethodKind.SettingsRead));
        Assert.False(ReadSurface.IsCharged(ReadMethodKind.Search));
    }
}
