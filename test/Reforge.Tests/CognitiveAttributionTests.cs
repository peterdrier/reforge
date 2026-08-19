using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Tests;

/// <summary>
/// Issue #31 findings 3(a) and 3(b): cognitive complexity mis-attributed a member's score to its
/// signature when the body was one big delegate, and charged that delegate a nesting level it did
/// not structurally earn.
/// </summary>
public class CognitiveAttributionTests
{
    private static BaseMethodDeclarationSyntax Parse(string body)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {body} }}");
        return tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().First();
    }

    // ---------------- 3(b): the nesting overcharge ----------------

    [Fact]
    public void Cognitive_LambdaThatIsTheWholeBody_CostsTheSameAsTheBodyWrittenDirectly()
    {
        // The shape the finding is about: System.CommandLine takes an action delegate, so an entire
        // member body sits one level down. Nothing about that nesting is structure a reader has to
        // hold, and charging it made every branch inside cost 1 more than the same code as a method.
        var direct = Parse(@"
            void M(int[] xs)
            {
                foreach (var x in xs)
                {
                    if (x > 0) { }
                }
            }");
        var viaLambda = Parse(@"
            void M(int[] xs)
            {
                Run(() =>
                {
                    foreach (var x in xs)
                    {
                        if (x > 0) { }
                    }
                });
            }
            void Run(System.Action a) { }");

        Assert.Equal(ImplementationComplexity.Cognitive(direct), ImplementationComplexity.Cognitive(viaLambda));
    }

    [Fact]
    public void Cognitive_LambdaInsideEnclosingStructure_StillPaysTheNestingLevel()
    {
        // The exemption is only for a nested function with no increment-bearing node between it and
        // its member. Inside a loop, a lambda is genuinely nested and keeps its level.
        var nested = Parse(@"
            void M(int[] xs)
            {
                foreach (var x in xs)
                {
                    Run(() => { if (x > 0) { } });
                }
            }
            void Run(System.Action a) { }");
        var flat = Parse(@"
            void M(int[] xs)
            {
                foreach (var x in xs)
                {
                    if (x > 0) { }
                }
            }");

        Assert.True(ImplementationComplexity.Cognitive(nested) > ImplementationComplexity.Cognitive(flat));
    }

    [Fact]
    public void Cognitive_LambdaInsideTheExemptTopLevelLambda_StillPaysItsLevel()
    {
        // The exemption is for the outermost nested function only. The exempt body is walked at
        // nesting 0, so a lambda declared inside it would see 0 too and take the exemption again —
        // scoring its branches at member depth though it is genuinely two functions deep.
        var innerLambda = Parse(@"
            void M(int[] xs)
            {
                Run(() =>
                {
                    Each(xs, x => { if (x > 0) { } });
                });
            }
            void Run(System.Action a) { }
            void Each(int[] xs, System.Action<int> a) { }");
        var singleLambda = Parse(@"
            void M(int[] xs)
            {
                Run(() => { if (xs.Length > 0) { } });
            }
            void Run(System.Action a) { }");

        // Two functions deep costs one more than one function deep.
        Assert.Equal(
            ImplementationComplexity.Cognitive(singleLambda) + 1,
            ImplementationComplexity.Cognitive(innerLambda));
    }

    [Fact]
    public void Cognitive_SiblingTopLevelLambdas_EachGetTheExemption()
    {
        // The exemption tracks the path to the current node, not how many nested functions the walk
        // has seen — so three lambdas at the member's own top level are each exempt, and the member
        // costs exactly what the three branches cost.
        var siblings = Parse(@"
            void M(int[] xs)
            {
                Run(() => { if (xs.Length > 0) { } });
                Run(() => { if (xs.Length > 1) { } });
                Run(() => { if (xs.Length > 2) { } });
            }
            void Run(System.Action a) { }");

        Assert.Equal(3, ImplementationComplexity.Cognitive(siblings));
    }

    // ---------------- 3(a): attribution ----------------

    [Fact]
    public void CognitiveDetail_ReportsTheNestedFunctionThatHoldsTheScore()
    {
        var m = Parse(@"
            void M(int[] xs)
            {
                Run(() =>
                {
                    foreach (var x in xs)
                    {
                        if (x > 0) { }
                    }
                });
            }
            void Run(System.Action a) { }");

        var detail = ImplementationComplexity.CognitiveDetail(m);

        Assert.True(detail.Score > 0);
        Assert.Equal(detail.Score, detail.NestedScore);   // all of it is inside the delegate
        Assert.True(detail.NestedLine > 0);
        Assert.True(detail.NestedDominates);
    }

    [Fact]
    public void CognitiveDetail_PlainMethodBody_AttributesToTheMethod()
    {
        var m = Parse(@"
            void M(int[] xs)
            {
                foreach (var x in xs)
                {
                    if (x > 0) { }
                }
            }");

        var detail = ImplementationComplexity.CognitiveDetail(m);

        Assert.True(detail.Score > 0);
        Assert.Equal(0, detail.NestedScore);
        Assert.False(detail.NestedDominates);
    }

    [Fact]
    public void CognitiveDetail_ComplexitySpreadAcrossSeveralLambdas_AttributesToTheMethod()
    {
        // NestedDominates is a strict majority so a member whose score is spread across several
        // delegates still reports against the member — there is no single place to point at.
        var m = Parse(@"
            void M(int[] xs)
            {
                Run(() => { if (xs.Length > 0) { } });
                Run(() => { if (xs.Length > 1) { } });
                Run(() => { if (xs.Length > 2) { } });
            }
            void Run(System.Action a) { }");

        var detail = ImplementationComplexity.CognitiveDetail(m);

        Assert.True(detail.Score >= 3);
        Assert.False(detail.NestedDominates);
    }
}
