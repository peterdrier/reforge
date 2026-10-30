namespace Reforge;

// Pass 7: size in exact tokens, on the implementation-shape axis. Runs only on a token census
// (surface-score --count-tokens); without one the three rules are skipped, never estimated.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Charges <c>sectionSize</c> on each section's production and contracts tokens, <c>testSize</c>
    /// on its test tokens beyond the free share of those, and <c>classSize</c> on each top-level
    /// class. A total that went unmeasured is not priced, and a <c>size-unpriced</c> diagnostic
    /// names it.
    /// </summary>
    private void ScoreSize(TokenCensus census, ScoreReport report)
    {
        var sectionW = _config.Weight("sectionSize");
        var classW = _config.Weight("classSize");
        var testW = _config.Weight("testSize");
        var unpriced = new List<string>();

        foreach (var (section, t) in census.BySection.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // A section has no single declaration; its largest class is where a reader starts.
            var anchor = t.Classes.FirstOrDefault();
            var (file, line) = (anchor?.File ?? "", anchor?.Line ?? 0);

            if (t.Total is not { } prod)
                unpriced.Add($"{section} (section)");
            else
            {
                AddEntryByName(report, section, "sectionSize", SizePricing.Section(prod, sectionW, _config),
                    section, file, line, $"{section}: {prod} tokens in {t.Files} files");
                if (t.Tests is not { } tests)
                    unpriced.Add($"{section} (tests)");
                else
                    AddEntryByName(report, section, "testSize", SizePricing.Tests(tests, prod, testW, _config),
                        section, file, line, $"{section}: {tests} test tokens in {t.TestFiles} files against {prod} production");
            }

            foreach (var c in t.Classes)
            {
                if (c.Tokens is { } tokens)
                    AddEntryByName(report, section, "classSize", SizePricing.Class(tokens, classW, _config),
                        c.Name, c.File, c.Line, $"{c.Name} ({tokens} tokens)");
                else
                    unpriced.Add(c.Name);
            }
        }

        if (unpriced.Count > 0)
            report.Diagnostics.Add(new ScoreDiagnostic("warning", "size-unpriced",
                $"Token counts failed, so these sizes are not priced: {string.Join(", ", unpriced)}."));
    }
}

/// <summary>
/// The size curves, in exact tokens. Free up to a threshold, then the rule's weight per thousand
/// tokens, plus (sections and classes only) a percentage of the excess past a second threshold, in
/// thousands, raised to <see cref="SurfaceScoreConfig.SizeTaxExponent"/>. A weight of 0 turns the
/// whole rule off, tax included.
/// </summary>
public static class SizePricing
{
    public static int Section(int tokens, int weight, SurfaceScoreConfig c) =>
        Curve(tokens, weight, c.SectionSizeFreeTokens, c.SectionSizeTaxTokens, c.SectionSizeTaxPercent, c.SizeTaxExponent);

    public static int Class(int tokens, int weight, SurfaceScoreConfig c) =>
        Curve(tokens, weight, c.ClassSizeFreeTokens, c.ClassSizeTaxTokens, c.ClassSizeTaxPercent, c.SizeTaxExponent);

    /// <summary>Linear only: test tokens beyond <see cref="SurfaceScoreConfig.TestSizeFreePercent"/>% of production.</summary>
    public static int Tests(int testTokens, int prodTokens, int weight, SurfaceScoreConfig c)
    {
        if (weight == 0) return 0;
        var excess = testTokens - (double)prodTokens * c.TestSizeFreePercent / 100;
        return Round(weight * Math.Max(0, excess) / 1000);
    }

    public static int Curve(int tokens, int weight, int freeTokens, int taxTokens, int taxPercent, double exponent)
    {
        if (weight == 0) return 0;
        var linear = weight * Math.Max(0, tokens - freeTokens) / 1000.0;
        var tax = taxPercent / 100.0 * Math.Pow(Math.Max(0, tokens - taxTokens) / 1000.0, exponent);
        return Round(linear + tax);
    }

    private static int Round(double points) => (int)Math.Round(points, MidpointRounding.AwayFromZero);
}
