namespace Reforge;

/// <summary>
/// Which files hold code nobody wrote. One definition, because the internal-complexity axis and
/// the section metrics have to agree on it: the metrics block is documented as describing the
/// corpus the rules score, so a file counted by one and skipped by the other reports size the
/// score cannot explain.
/// </summary>
public static class GeneratedCode
{
    /// <summary>
    /// True for EF migrations and the designer/generated file suffixes. Path-based, because that
    /// is what both callers have at the point they decide — a <c>[GeneratedCode]</c> attribute scan
    /// would be more precise and is not what any of these emitters reliably write.
    /// </summary>
    public static bool IsGeneratedFile(string file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        var f = file.Replace('\\', '/');
        return f.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }
}
