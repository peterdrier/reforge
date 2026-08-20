using System.Text.Json;
using Reforge.Commands;

namespace Reforge.Tests;

/// <summary>
/// The second axis is <c>implementationShape</c>, and the name is load-bearing: the old name
/// <c>internalComplexity</c> read as an accessibility gate, which the axis has never had — its rule
/// set is fixed, so public code scores on it too. These tests pin the emitted key and the axis
/// membership so a rename can't half-land.
/// </summary>
public class AxisNamingTests
{
    [Fact]
    public void Json_NamesTheAxisImplementationShape()
    {
        var report = new ScoreReport { SurfaceTotal = 100, ImplementationShapeTotal = 42, Total = 142 };

        using var doc = JsonDocument.Parse(Capture(() => SurfaceScoreCommand.WriteJson(report, null, 10, 25, null)));

        Assert.Equal(42, doc.RootElement.GetProperty("implementationShapeTotal").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("internalComplexityTotal", out _));
    }

    /// <summary>
    /// The axis is a fixed rule partition, not an accessibility filter. If a rule that charges
    /// declared public surface ever lands here, the two scalars stop being independent and the
    /// Pareto gate can be satisfied by moving a charge across the boundary.
    /// </summary>
    [Fact]
    public void ImplementationShape_IsTheFiveShapeRules()
    {
        Assert.Equal(
            new[]
            {
                "actionDispatcher", "cognitiveComplexity", "flagsControlFlow", "largeClass",
                "mutationModeParameter"
            },
            SurfaceScoreRuleGroups.ImplementationShape.OrderBy(r => r, StringComparer.Ordinal).ToArray());

        Assert.True(SurfaceScoreRuleGroups.IsImplementationShape("cognitiveComplexity"));
        Assert.False(SurfaceScoreRuleGroups.IsImplementationShape("fullServiceInterfaceMethod"));
    }

    private static string Capture(Action action)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { action(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }
}
