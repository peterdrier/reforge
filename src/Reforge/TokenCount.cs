using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Reforge;

/// <summary>
/// The raw <c>input_tokens</c> of a request whose only content is one user message holding
/// <paramref name="text"/>, or null when it could not be measured. Never an estimate.
/// </summary>
public interface ITokenCounter
{
    string Model { get; }
    Task<int?> CountAsync(string text, CancellationToken ct);
}

/// <summary>
/// Anthropic's <c>/v1/messages/count_tokens</c>, a few requests at a time. A 429 or overload waits
/// for <c>retry-after</c> (or backs off) and retries; anything else fails soft to null with one
/// stderr line per status.
/// </summary>
public sealed class AnthropicTokenCounter : ITokenCounter, IDisposable
{
    public const string DefaultModel = "claude-opus-5-5";
    private const int MaxAttempts = 6;

    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.anthropic.com/"), Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, bool> _warned = new();
    private volatile bool _unauthorized;

    public AnthropicTokenCounter(string apiKey, string model = DefaultModel, int concurrency = 4)
    {
        Model = model;
        _gate = new SemaphoreSlim(concurrency);
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public string Model { get; }

    public async Task<int?> CountAsync(string text, CancellationToken ct)
    {
        if (_unauthorized) return null;
        var body = JsonSerializer.Serialize(new { model = Model, messages = new[] { new { role = "user", content = text } } });

        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await _http.PostAsync("v1/messages/count_tokens", content, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt == MaxAttempts) { Warn("network", ex.Message); return null; }
                    await Task.Delay(Backoff(attempt), ct);
                    continue;
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        return doc.RootElement.GetProperty("input_tokens").GetInt32();
                    }
                    if (status is 429 or 500 or 502 or 503 or 504 or 529 && attempt < MaxAttempts)
                    {
                        await Task.Delay(RetryAfter(response.Headers.RetryAfter) ?? Backoff(attempt), ct);
                        continue;
                    }
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) _unauthorized = true;
                    var detail = await response.Content.ReadAsStringAsync(ct);
                    Warn(status.ToString(), detail.Length > 300 ? detail[..300] : detail);
                    return null;
                }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? h)
    {
        TimeSpan? wait = h?.Delta ?? (h?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is null || wait <= TimeSpan.Zero) return null;
        return wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait;
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));

    private void Warn(string key, string message)
    {
        if (_warned.TryAdd(key, true))
            Console.Error.WriteLine($"tokens: count_tokens failed ({key}): {message.ReplaceLineEndings(" ")}");
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// Raw counts keyed by SHA-256 of model and text, in one JSON file. Reruns only pay for changed
/// text. Failures are never stored. <see cref="Save"/> merges with what is on disk, so two runs
/// sharing a cache lose nothing.
/// </summary>
public sealed class TokenCache
{
    private readonly ConcurrentDictionary<string, int> _counts;
    private readonly string? _path;

    private TokenCache(string? path, Dictionary<string, int> counts)
    {
        _path = path;
        _counts = new ConcurrentDictionary<string, int>(counts, StringComparer.Ordinal);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "reforge", "token-cache.json");

    public static TokenCache InMemory() => new(null, new Dictionary<string, int>());

    public static TokenCache Load(string path) => new(path, Read(path));

    public int Count => _counts.Count;

    public static string Key(string model, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\0" + text)));

    public bool TryGet(string key, out int count) => _counts.TryGetValue(key, out count);

    public void Set(string key, int count) => _counts[key] = count;

    public void Save()
    {
        if (_path is null) return;
        var merged = Read(_path);
        foreach (var (k, v) in _counts) merged[k] = v;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(merged));
        File.Move(temp, _path, overwrite: true);
    }

    private static Dictionary<string, int> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tokens: ignoring unreadable cache {path}: {ex.Message}");
            return new();
        }
    }
}

/// <summary>
/// Token counts for arbitrary text, net of request overhead. The API counts a whole request and
/// rejects empty content, so a one-character message (<see cref="BaselineText"/>) is counted once
/// and subtracted from every measurement. File counts then add up instead of each carrying the
/// envelope; each is the text's tokens less the baseline character's, a constant per text.
/// Concurrent requests for the same text share one call.
/// </summary>
public sealed class TokenMeasurer(ITokenCounter counter, TokenCache cache)
{
    public const string BaselineText = ".";

    private readonly ConcurrentDictionary<string, Task<int?>> _inFlight = new(StringComparer.Ordinal);
    private Task<int?>? _baseline;
    private int _requests, _hits, _failures;

    public int Requests => _requests;
    public int CacheHits => _hits;
    public int Failures => _failures;

    /// <summary>Tokens in <paramref name="text"/> net of the baseline, or null when unmeasured.</summary>
    public async Task<int?> MeasureAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var baseline = await LazyInitializer.EnsureInitialized(ref _baseline, () => RawAsync(BaselineText, ct));
        var raw = await RawAsync(text, ct);
        return baseline is null || raw is null ? null : Math.Max(0, raw.Value - baseline.Value);
    }

    private Task<int?> RawAsync(string text, CancellationToken ct)
    {
        var key = TokenCache.Key(counter.Model, text);
        if (cache.TryGet(key, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return Task.FromResult<int?>(cached);
        }
        return _inFlight.GetOrAdd(key, _ => FetchAsync(key, text, ct));
    }

    private async Task<int?> FetchAsync(string key, string text, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var count = await counter.CountAsync(text, ct);
        if (count is null) Interlocked.Increment(ref _failures);
        else cache.Set(key, count.Value);
        return count;
    }
}

/// <summary>One type's text as the census reads it: the files it is declared in, and its declaration spans.</summary>
public sealed record TypeText(
    string Section, string Assembly, string Name, string File, int Line, bool IsClass,
    IReadOnlyList<(string Path, string Text)> Files, IReadOnlyList<string> Spans);

public sealed record ClassTokens(string Name, string File, int Line, int? Tokens);

/// <summary>A section's tokens: the sum of its whole handwritten files, null if any file went unmeasured.</summary>
public sealed record SectionTokens(int? Total, int Files, List<ClassTokens> Classes);

public sealed class TokenCensus
{
    public int? Total { get; init; }
    public int Files { get; init; }
    public Dictionary<string, SectionTokens> BySection { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every classified type's handwritten files and, for classes and structs, its declaration
    /// spans across all partial files, doc comments included. Generated files and migrations are
    /// skipped exactly as <see cref="SectionMetricsAnalyzer"/> skips them, so the file set is the
    /// one <c>metrics.files</c> counts.
    /// </summary>
    public static List<TypeText> Collect(IEnumerable<ClassifiedType> classified, CancellationToken ct)
    {
        var result = new List<TypeText>();
        foreach (var c in classified)
        {
            var files = new List<(string, string)>();
            var spans = new List<string>();
            foreach (var reference in c.Type.DeclaringSyntaxReferences)
            {
                var tree = reference.SyntaxTree;
                if (string.IsNullOrEmpty(tree.FilePath) || GeneratedCode.IsGeneratedFile(tree.FilePath)) continue;
                var source = tree.GetText(ct);
                files.Add((tree.FilePath, source.ToString()));
                if (reference.GetSyntax(ct) is TypeDeclarationSyntax declaration)
                    spans.Add(source.ToString(DeclarationSpan(declaration)));
            }
            if (files.Count == 0) continue;
            result.Add(new TypeText(c.Group, c.Type.ContainingAssembly?.Name ?? "", c.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                c.File, c.Line, c.Type.TypeKind is TypeKind.Class or TypeKind.Struct, files, spans));
        }
        return result;
    }

    private static TextSpan DeclarationSpan(TypeDeclarationSyntax declaration)
    {
        var start = declaration.Span.Start;
        foreach (var trivia in declaration.GetLeadingTrivia())
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                start = trivia.FullSpan.Start;
                break;
            }
        return TextSpan.FromBounds(start, declaration.Span.End);
    }

    /// <summary>
    /// Measures each distinct file once per (assembly, path), as <c>metrics.files</c> counts them,
    /// and each class as the sum of its spans. Whole files make a section's total independent of
    /// how its code is split into types or partials.
    /// </summary>
    public static async Task<TokenCensus> MeasureAsync(IReadOnlyList<TypeText> types, TokenMeasurer measurer, CancellationToken ct)
    {
        var fileText = new Dictionary<(string Assembly, string Path), string>();
        var fileSection = new Dictionary<(string Assembly, string Path), string>();
        foreach (var t in types)
            foreach (var (path, text) in t.Files)
                if (fileText.TryAdd((t.Assembly, path), text)) fileSection[(t.Assembly, path)] = t.Section;

        var fileCounts = fileText.ToDictionary(kv => kv.Key, kv => measurer.MeasureAsync(kv.Value, ct));
        var classes = types.Where(t => t.IsClass)
            .Select(t => (Type: t, Counts: t.Spans.Select(s => measurer.MeasureAsync(s, ct)).ToList()))
            .ToList();
        await Task.WhenAll(fileCounts.Values.Concat(classes.SelectMany(c => c.Counts)));

        var census = new TokenCensus { Total = Sum(fileCounts.Values.Select(t => t.Result)), Files = fileCounts.Count };
        foreach (var section in fileSection.GroupBy(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sectionClasses = classes
                .Where(c => string.Equals(c.Type.Section, section.Key, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ClassTokens(c.Type.Name, c.Type.File, c.Type.Line, Sum(c.Counts.Select(t => t.Result))))
                .OrderByDescending(c => c.Tokens ?? -1)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            census.BySection[section.Key] = new SectionTokens(
                Sum(section.Select(f => fileCounts[f].Result)), section.Count(), sectionClasses);
        }
        return census;
    }

    private static int? Sum(IEnumerable<int?> counts)
    {
        var total = 0;
        foreach (var c in counts)
        {
            if (c is null) return null;
            total += c.Value;
        }
        return total;
    }
}
