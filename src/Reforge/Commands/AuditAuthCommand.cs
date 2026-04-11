using System.CommandLine;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Reforge.Commands;

public static class AuditAuthCommand
{
    public static Command Create(Option<string?> solutionOption, Option<OutputFormat> formatOption, Option<int?> limitOption)
    {
        var command = new Command("audit-auth", "Find controller actions missing security attributes");

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
                var results = new List<ResultEntry>();

                foreach (var project in solution.Projects)
                {
                    if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation is null)
                        continue;

                    foreach (var document in project.Documents)
                    {
                        var root = await document.GetSyntaxRootAsync(cancellationToken);
                        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                        if (root is null || semanticModel is null)
                            continue;

                        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

                        foreach (var classDecl in classes)
                        {
                            var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken) as INamedTypeSymbol;
                            if (classSymbol is null)
                                continue;

                            if (!IsController(classDecl, classSymbol))
                                continue;

                            var classHasAuthorize = HasAttributeOnClassOrBase(classSymbol, "Authorize");
                            var classHasAutoValidateAntiforgery = HasAttributeOnClassOrBase(classSymbol, "AutoValidateAntiforgeryToken");

                            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
                            {
                                // Only check public methods
                                if (!method.Modifiers.Any(m => m.Text == "public"))
                                    continue;

                                var attrs = method.AttributeLists;

                                bool hasHttpPost = HasAttribute(attrs, "HttpPost");
                                bool hasHttpPut = HasAttribute(attrs, "HttpPut");
                                bool hasHttpDelete = HasAttribute(attrs, "HttpDelete");
                                bool hasHttpPatch = HasAttribute(attrs, "HttpPatch");
                                bool hasHttpGet = HasAttribute(attrs, "HttpGet");
                                bool hasRoute = HasAttribute(attrs, "Route");

                                // Skip non-actions: no HTTP verb and no [Route]
                                if (!hasHttpPost && !hasHttpPut && !hasHttpDelete && !hasHttpPatch && !hasHttpGet && !hasRoute)
                                    continue;

                                bool isMutating = hasHttpPost || hasHttpPut || hasHttpDelete || hasHttpPatch;
                                bool hasMethodAuthorize = HasAttribute(attrs, "Authorize");
                                bool hasMethodAllowAnonymous = HasAttribute(attrs, "AllowAnonymous");
                                bool hasMethodValidateAntiforgeryToken = HasAttribute(attrs, "ValidateAntiForgeryToken");
                                bool hasMethodIgnoreAntiforgeryToken = HasAttribute(attrs, "IgnoreAntiforgeryToken");

                                var lineSpan = method.Identifier.GetLocation().GetLineSpan();
                                var filePath = LocationHelper.NormalizePath(lineSpan.Path, solutionDir);
                                var line = lineSpan.StartLinePosition.Line + 1;
                                var column = lineSpan.StartLinePosition.Character + 1;

                                // Check 1: mutating action missing [Authorize]
                                if (isMutating && !classHasAuthorize && !hasMethodAuthorize && !hasMethodAllowAnonymous)
                                {
                                    var verb = GetHttpVerb(hasHttpPost, hasHttpPut, hasHttpDelete, hasHttpPatch);
                                    var context = $"[{verb}] {method.Identifier.Text} — missing [Authorize]";
                                    results.Add(new ResultEntry(filePath, line, column, context, classSymbol.Name));
                                }

                                // Check 2: POST action missing [ValidateAntiForgeryToken]
                                if (hasHttpPost && !classHasAutoValidateAntiforgery && !hasMethodValidateAntiforgeryToken && !hasMethodIgnoreAntiforgeryToken)
                                {
                                    var context = $"[HttpPost] {method.Identifier.Text} — missing [ValidateAntiForgeryToken]";
                                    results.Add(new ResultEntry(filePath, line, column, context, classSymbol.Name));
                                }
                            }
                        }
                    }
                }

                int? totalBeforeLimit = null;
                if (limit.HasValue && results.Count > limit.Value)
                {
                    totalBeforeLimit = results.Count;
                    results = results.Take(limit.Value).ToList();
                }

                OutputFormatter.WriteResults("audit-auth", "issues", results, format, r => r, totalBeforeLimit);

                sw.Stop();
                Telemetry.Log("audit-auth", "", totalBeforeLimit ?? results.Count, sw.ElapsedMilliseconds);
            }
        });

        return command;
    }

    private static bool IsController(ClassDeclarationSyntax classDecl, INamedTypeSymbol classSymbol)
    {
        // Convention: name ends with "Controller"
        if (classSymbol.Name.EndsWith("Controller"))
            return true;

        // Has [ApiController] attribute
        if (HasAttribute(classDecl.AttributeLists, "ApiController"))
            return true;

        // Inherits from Controller or ControllerBase
        var current = classSymbol.BaseType;
        while (current != null)
        {
            if (current.Name is "Controller" or "ControllerBase")
                return true;
            current = current.BaseType;
        }

        return false;
    }

    private static bool HasAttributeOnClassOrBase(INamedTypeSymbol classSymbol, string attributeName)
    {
        var current = classSymbol;
        while (current != null)
        {
            if (current.GetAttributes().Any(a =>
                a.AttributeClass?.Name == attributeName ||
                a.AttributeClass?.Name == attributeName + "Attribute"))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, params string[] names)
    {
        return attributeLists
            .SelectMany(al => al.Attributes)
            .Any(a =>
            {
                var name = a.Name.ToString();
                return names.Any(n =>
                    name == n ||
                    name == n + "Attribute" ||
                    name.EndsWith("." + n) ||
                    name.EndsWith("." + n + "Attribute"));
            });
    }

    private static string GetHttpVerb(bool post, bool put, bool delete, bool patch)
    {
        if (post) return "HttpPost";
        if (put) return "HttpPut";
        if (delete) return "HttpDelete";
        if (patch) return "HttpPatch";
        return "Http?";
    }
}
