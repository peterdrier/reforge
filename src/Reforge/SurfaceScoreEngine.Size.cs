namespace Reforge;

// Pass 7: size in tokens, on the implementation-shape axis. Each file's exact count when the run
// has one (measured or cached), otherwise its lexical estimate, so the size rules always run.
public sealed partial class SurfaceScoreEngine
{
    /// <summary>
    /// Charges <c>sectionSize</c> on each section's production and contracts tokens, <c>testSize</c>
    /// on its test tokens beyond the free share of those, and <c>classSize</c> on each top-level
    /// class. An estimated figure within 5% of one of its thresholds is flagged near-threshold, in
    /// the census and in the entry's detail.
    /// </summary>
    private void ScoreSize(TokenCensus census, ScoreReport report)
    {
        var sectionW = _config.Weight("sectionSize");
        var classW = _config.Weight("classSize");
        var testW = _config.Weight("testSize");

        foreach (var (section, raw) in census.BySection.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            var prod = raw.Total.Flag(_config.SectionSizeFreeTokens, _config.SectionSizeTaxTokens);
            // The test threshold is a share of production, so an estimate on either side makes it uncertain.
            var tests = raw.Tests with
            {
                NearThreshold = (raw.Tests.Estimated || prod.Estimated)
                    && TokenFigure.IsNear(raw.Tests.Value, prod.Value * _config.TestSizeFreePercent / 100.0)
            };
            var t = raw with
            {
                Total = prod,
                Classes = raw.Classes.Select(c => c with { Tokens = c.Tokens.Flag(_config.ClassSizeFreeTokens, _config.ClassSizeTaxTokens) }).ToList(),
                Tests = tests,
            };
            census.BySection[section] = t;

            // A section has no single declaration; its largest class is where a reader starts.
            var anchor = t.Classes.FirstOrDefault();
            var (file, line) = (anchor?.File ?? "", anchor?.Line ?? 0);

            AddEntryByName(report, section, "sectionSize", SizePricing.Section(prod.Value, sectionW, _config),
                section, file, line, $"{section}: {prod} tokens in {t.Files} files");
            AddEntryByName(report, section, "testSize", SizePricing.Tests(tests.Value, prod.Value, testW, _config),
                section, file, line, $"{section}: {tests} test tokens in {t.TestFiles} files against {prod} production");

            foreach (var c in t.Classes)
                AddEntryByName(report, section, "classSize", SizePricing.Class(c.Tokens.Value, classW, _config),
                    c.Name, c.File, c.Line, $"{c.Name} ({c.Tokens} tokens)");
        }
    }
}

/// <summary>
/// The size curves, in tokens. Free up to a threshold, then the rule's weight per thousand
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
