using System.Globalization;
using System.Xml.Linq;

namespace Reforge;

/// <summary>
/// Statement (line) coverage of one section, read from a coverage report produced by a test run.
/// </summary>
/// <remarks>
/// Reforge does not run tests or collect coverage. It reads what the test runner wrote and
/// attributes it to sections by assembly name, the same mapping the score uses, so a coverage
/// figure sits beside the size and surface of exactly the code it describes. Informational:
/// nothing here feeds a score.
/// </remarks>
public sealed record SectionCoverage(int Covered, int Total)
{
    public static readonly SectionCoverage Empty = new(0, 0);
    public bool HasData => Total > 0;
    /// <summary>Whole percent, 0 when the section has no measured statements.</summary>
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Covered / Total);
    public SectionCoverage Add(SectionCoverage other) => new(Covered + other.Covered, Total + other.Total);
}

/// <summary>What one coverage file yielded, attributed to sections.</summary>
public sealed record CoverageIngest(
    /// <summary><c>cobertura</c> or <c>dotcover</c>.</summary>
    string Format,
    Dictionary<string, SectionCoverage> BySection,
    /// <summary>Rollup over the assemblies that mapped to a section; unmatched assemblies are left out.</summary>
    SectionCoverage Solution,
    /// <summary>Assemblies in the report that are not in the analyzed solution (test projects, usually).</summary>
    List<string> UnmatchedAssemblies);

/// <summary>
/// Reads a Cobertura XML file (Coverlet's default: <c>dotnet test --collect:"XPlat Code Coverage"</c>,
/// ReportGenerator's <c>Cobertura</c> target) or a dotCover <c>DetailedXML</c> report
/// (<c>dotnet dotcover test --dcReportType=DetailedXML</c>) and sums covered/total statements per
/// section.
/// </summary>
/// <remarks>
/// <para>Assembly names are the join key. Cobertura's <c>package name</c> is the assembly under
/// Coverlet and ReportGenerator; dotCover's <c>Assembly Name</c> is the assembly by definition.
/// A section's <c>.Contracts</c> satellite folds in exactly as it does for the score.</para>
/// <para>Cobertura carries <c>line-rate</c> per package but no counts, so lines are counted from the
/// <c>line</c> elements under each class: a line is covered when <c>hits &gt; 0</c>. That is what
/// every Cobertura consumer does; the numbers agree with ReportGenerator's summary.</para>
/// </remarks>
public static class CoverageReportReader
{
    public static CoverageIngest Read(string path, IReadOnlyDictionary<string, string> sectionByAssembly)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException($"{path}: empty document");

        var perAssembly = root.Name.LocalName switch
        {
            "coverage" => ReadCobertura(root),
            "Root" when root.Elements("Assembly").Any() || root.Attribute("DotCoverVersion") is not null => ReadDotCover(root),
            _ => throw new InvalidDataException(
                $"{path}: not a Cobertura (<coverage>) or dotCover DetailedXML (<Root><Assembly>) report; root element is <{root.Name.LocalName}>")
        };
        var format = root.Name.LocalName == "coverage" ? "cobertura" : "dotcover";

        var bySection = new Dictionary<string, SectionCoverage>(StringComparer.OrdinalIgnoreCase);
        var solution = SectionCoverage.Empty;
        var unmatched = new List<string>();

        foreach (var (assembly, coverage) in perAssembly.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!sectionByAssembly.TryGetValue(assembly, out var section))
            {
                unmatched.Add(assembly);
                continue;
            }
            bySection[section] = bySection.TryGetValue(section, out var existing) ? existing.Add(coverage) : coverage;
            solution = solution.Add(coverage);
        }

        return new CoverageIngest(format, bySection, solution, unmatched);
    }

    private static Dictionary<string, SectionCoverage> ReadCobertura(XElement root)
    {
        var result = new Dictionary<string, SectionCoverage>(StringComparer.Ordinal);
        foreach (var package in root.Descendants("package"))
        {
            var name = package.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;

            int covered = 0, total = 0;
            // Lines are keyed per class, not per package, so a line listed under two methods of
            // the same class (a lambda and its enclosing method, say) counts once.
            foreach (var cls in package.Descendants("class"))
            {
                var seen = new Dictionary<int, bool>();
                foreach (var line in cls.Descendants("line"))
                {
                    if (!int.TryParse(line.Attribute("number")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                    long.TryParse(line.Attribute("hits")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits);
                    seen[number] = seen.TryGetValue(number, out var was) ? was || hits > 0 : hits > 0;
                }
                total += seen.Count;
                covered += seen.Count(kv => kv.Value);
            }

            var add = new SectionCoverage(covered, total);
            result[name] = result.TryGetValue(name, out var existing) ? existing.Add(add) : add;
        }
        return result;
    }

    private static Dictionary<string, SectionCoverage> ReadDotCover(XElement root)
    {
        var result = new Dictionary<string, SectionCoverage>(StringComparer.Ordinal);
        foreach (var assembly in root.Elements("Assembly"))
        {
            var name = assembly.Attribute("Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            int.TryParse(assembly.Attribute("CoveredStatements")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var covered);
            int.TryParse(assembly.Attribute("TotalStatements")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total);
            var add = new SectionCoverage(covered, total);
            result[name] = result.TryGetValue(name, out var existing) ? existing.Add(add) : add;
        }
        return result;
    }
}
