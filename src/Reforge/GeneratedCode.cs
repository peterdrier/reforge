namespace Reforge;

/// <summary>
/// Which files hold code nobody wrote. One definition: the metrics block describes the corpus the
/// rules score, so the two must not disagree about what is generated.
/// </summary>
public static class GeneratedCode
{
    /// <summary>EF migrations and the designer/generated suffixes, by path — what both callers have.</summary>
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
