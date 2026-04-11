using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Commands;

public static class AuditEfCommand
{
    private static readonly HashSet<string> LinqPredicateMethods = new(StringComparer.Ordinal)
    {
        "Where", "FirstOrDefault", "First", "Single", "SingleOrDefault",
        "Any", "Count", "LongCount", "All", "Last", "LastOrDefault"
    };

    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var command = new Command("audit-ef", "Detect EF Core LINQ patterns that fail at runtime");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solutionPath = parseResult.GetValue(solutionOption);
            var format = parseResult.GetValue(formatOption);
            var limit = parseResult.GetValue(limitOption);
            var sw = Stopwatch.StartNew();

            var (solution, handle) = await WorkspaceHelper.OpenSolutionAsync(solutionPath);
            using (handle)
            {
                var solutionDir = LocationHelper.GetSolutionDirectory(solution);
                var entries = new List<ResultEntry>();

                foreach (var project in solution.Projects)
                {
                    if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var document in project.Documents)
                    {
                        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
                        if (tree is null)
                            continue;

                        var root = await tree.GetRootAsync(cancellationToken);
                        var semanticModel = compilation.GetSemanticModel(tree);

                        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                                continue;

                            var methodName = memberAccess.Name switch
                            {
                                GenericNameSyntax generic => generic.Identifier.Text,
                                SimpleNameSyntax simple => simple.Identifier.Text,
                                _ => null
                            };

                            if (methodName is null)
                                continue;

                            // 1. HasDefaultValue with CLR defaults (sentinel trap)
                            if (methodName == "HasDefaultValue" && invocation.ArgumentList.Arguments.Count == 1)
                            {
                                var arg = invocation.ArgumentList.Arguments[0].Expression;
                                if (IsCLRDefault(arg))
                                {
                                    var valueText = arg.ToString();
                                    AddViolation(entries, invocation, tree, solutionDir,
                                        $"HasDefaultValue({valueText}) uses CLR default -- EF won't send this value to DB");
                                }
                            }

                            // 2. HasConversion<string>() detection
                            if (methodName == "HasConversion" && memberAccess.Name is GenericNameSyntax genericName)
                            {
                                if (genericName.TypeArgumentList.Arguments.Count == 1)
                                {
                                    var typeArg = genericName.TypeArgumentList.Arguments[0].ToString();
                                    if (typeArg == "string")
                                    {
                                        AddViolation(entries, invocation, tree, solutionDir,
                                            "HasConversion<string>() -- comparison operators won't translate to SQL");
                                    }
                                }
                            }

                            // 3. String interpolation in LINQ predicates
                            if (LinqPredicateMethods.Contains(methodName))
                            {
                                foreach (var arg in invocation.ArgumentList.Arguments)
                                {
                                    LambdaExpressionSyntax? lambda = arg.Expression as SimpleLambdaExpressionSyntax;
                                    lambda ??= arg.Expression as ParenthesizedLambdaExpressionSyntax;

                                    if (lambda is not null)
                                    {
                                        var hasInterpolation = lambda.DescendantNodes()
                                            .OfType<InterpolatedStringExpressionSyntax>()
                                            .Any();
                                        if (hasInterpolation)
                                        {
                                            AddViolation(entries, invocation, tree, solutionDir,
                                                "string interpolation in LINQ predicate -- risk of client evaluation");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Dedup
                var deduped = entries
                    .GroupBy(e => $"{e.File}:{e.Line}:{e.Column}")
                    .Select(g => g.First())
                    .ToList();

                int? totalBeforeLimit = null;
                if (limit.HasValue && deduped.Count > limit.Value)
                {
                    totalBeforeLimit = deduped.Count;
                    deduped = deduped.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults(
                    "audit-ef",
                    "EF Core patterns",
                    deduped,
                    format,
                    entry => entry,
                    totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("audit-ef", "(all)", totalBeforeLimit ?? deduped.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static bool IsCLRDefault(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal)
        {
            return literal.Kind() switch
            {
                SyntaxKind.FalseLiteralExpression => true,
                SyntaxKind.NumericLiteralExpression => IsZeroLiteral(literal),
                SyntaxKind.StringLiteralExpression => literal.Token.ValueText == "",
                _ => false
            };
        }

        // Handle default(T) or default expressions
        if (expression is DefaultExpressionSyntax or LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression })
            return true;

        return false;
    }

    private static bool IsZeroLiteral(LiteralExpressionSyntax literal)
    {
        var text = literal.Token.ValueText;
        // Handle 0, 0.0, 0m, 0L, etc.
        return text == "0";
    }

    private static void AddViolation(
        List<ResultEntry> entries,
        SyntaxNode node,
        SyntaxTree tree,
        string solutionDir,
        string description)
    {
        var location = node.GetLocation();
        var lineSpan = location.GetLineSpan();
        var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        var text = tree.GetText();
        var sourceLine = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

        entries.Add(new ResultEntry(filePath, line, column, $"{sourceLine} -- {description}", ""));
    }
}
