using System.Collections.Concurrent;
using System.Globalization;
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

/// <summary>A cache key: the counting model, and the first 128 bits of the text's SHA-256.</summary>
public readonly record struct TokenKey(string Model, UInt128 Hash)
{
    /// <summary>The hash is already uniform, so its low bits are the dictionary hash.</summary>
    public sealed class Comparer : IEqualityComparer<TokenKey>
    {
        public static readonly Comparer Instance = new();
        public bool Equals(TokenKey x, TokenKey y) => x.Hash == y.Hash && string.Equals(x.Model, y.Model, StringComparison.Ordinal);
        public int GetHashCode(TokenKey key) => (int)(ulong)key.Hash;
    }
}

/// <summary>
/// Raw counts by model and content hash, durable across runs and shared by concurrent ones. The
/// file is append-only text, one <c>count model hash</c> line per entry, so a run writes only what
/// it measured and loading a million entries stays a single sequential read. Appends happen under
/// an exclusive lock file; a reader takes no lock and keeps only complete lines, and the count comes
/// first so a torn line never parses. When duplicate lines pile up (two runs counting the same
/// text at once) the file is rewritten whole, to a temp file renamed over it. Failures are never
/// stored.
/// </summary>
public sealed class TokenCache
{
    public const string EnvironmentVariable = "REFORGE_TOKEN_CACHE";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private readonly Dictionary<TokenKey, int> _loaded;
    private readonly ConcurrentDictionary<TokenKey, int> _added = new(TokenKey.Comparer.Instance);
    private readonly string? _path;
    private readonly bool _compact;

    private TokenCache(string? path, Dictionary<TokenKey, int> loaded, bool compact)
    {
        _path = path;
        _loaded = loaded;
        _compact = compact;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "reforge", "token-cache.txt");

    /// <summary>The cache file: <paramref name="option"/>, else <see cref="EnvironmentVariable"/>, else <see cref="DefaultPath"/>.</summary>
    public static string ResolvePath(string? option) =>
        !string.IsNullOrWhiteSpace(option) ? option
        : Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv
        : DefaultPath;

    public static TokenCache InMemory() => new(null, new Dictionary<TokenKey, int>(TokenKey.Comparer.Instance), false);

    public static TokenCache Load(string path)
    {
        var (entries, lines, legacy) = Read(path);
        // Rewrite when a quarter of the lines are duplicates, or the file is not in this format.
        return new TokenCache(path, entries, legacy || lines > 1024 && lines > entries.Count + entries.Count / 4);
    }

    public int Count => _loaded.Count + _added.Count(kv => !_loaded.ContainsKey(kv.Key));

    public static TokenKey Key(string model, string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return new TokenKey(model, System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(hash));
    }

    public bool TryGet(TokenKey key, out int count) => _added.TryGetValue(key, out count) || _loaded.TryGetValue(key, out count);

    public void Set(TokenKey key, int count) => _added[key] = count;

    /// <summary>Appends this run's new entries under the lock, or rewrites the file when it needs compacting.</summary>
    public void Save()
    {
        if (_path is null) return;
        var fresh = _added.Where(kv => !_loaded.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        if (fresh.Count == 0 && !_compact) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var held = AcquireLock(_path + ".lock");
        if (_compact)
        {
            // Under the lock, so nothing appended since this run loaded is lost.
            var merged = Read(_path).Entries;
            foreach (var (k, v) in fresh) merged[k] = v;
            var temp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false)))
                foreach (var (k, v) in merged) w.Write(Line(k, v));
            File.Move(temp, _path, overwrite: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in fresh) sb.Append(Line(k, v));
        using var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 0)
        {
            // A writer that died mid-line left no newline; start on a fresh line so this one parses.
            file.Seek(-1, SeekOrigin.End);
            if (file.ReadByte() != '\n') sb.Insert(0, '\n');
        }
        file.Seek(0, SeekOrigin.End);
        file.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        file.Flush(flushToDisk: true);
    }

    private static string Line(TokenKey key, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {key.Model} {key.Hash:X32}\n");

    private static FileStream AcquireLock(string lockPath)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(Random.Shared.Next(10, 50)); }
        }
    }

    private static (Dictionary<TokenKey, int> Entries, int Lines, bool Legacy) Read(string path)
    {
        var entries = new Dictionary<TokenKey, int>(TokenKey.Comparer.Instance);
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return (entries, 0, false);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tokens: ignoring unreadable cache {path}: {ex.Message}");
            return (entries, 0, false);
        }
        if (bytes.Length > 0 && bytes[0] == (byte)'{') return (entries, 0, true);
        entries.EnsureCapacity(bytes.Length / 56); // a line is about 55 bytes

        var models = new List<(byte[] Bytes, string Name)>();
        var span = bytes.AsSpan();
        var lines = 0;
        while (true)
        {
            var eol = span.IndexOf((byte)'\n');
            if (eol < 0) break; // an unfinished last line is still being written, or was torn
            var line = span[..eol].TrimEnd((byte)'\r');
            span = span[(eol + 1)..];
            if (line.IsEmpty) continue;
            lines++;
            var a = line.IndexOf((byte)' ');
            if (a <= 0) continue;
            var rest = line[(a + 1)..];
            var b = rest.IndexOf((byte)' ');
            if (b <= 0 || rest.Length - b - 1 != 32) continue;
            if (!int.TryParse(line[..a], NumberStyles.None, CultureInfo.InvariantCulture, out var count)) continue;
            if (!TryParseHex(rest[(b + 1)..], out var hash)) continue;
            entries[new TokenKey(Intern(models, rest[..b]), hash)] = count;
        }
        return (entries, lines, false);
    }

    private static bool TryParseHex(ReadOnlySpan<byte> hex, out UInt128 value)
    {
        value = 0;
        foreach (var c in hex)
        {
            int digit = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            value = (value << 4) | (uint)digit;
        }
        return true;
    }

    private static string Intern(List<(byte[] Bytes, string Name)> models, ReadOnlySpan<byte> model)
    {
        foreach (var (bytes, name) in models)
            if (model.SequenceEqual(bytes)) return name;
        var added = Encoding.UTF8.GetString(model);
        models.Add((model.ToArray(), added));
        return added;
    }
}

/// <summary>
/// Exact token counts for arbitrary text, net of request overhead, or null when there is none: the
/// cache answers first, and only a counter (an API key) asks the API. The API counts a whole
/// request and rejects empty content, so a one-character message (<see cref="BaselineText"/>) is
/// counted once and subtracted from every measurement. File counts then add up instead of each
/// carrying the envelope. Concurrent requests for the same text share one call.
/// </summary>
public sealed class TokenMeasurer(ITokenCounter? counter, TokenCache cache, string model = AnthropicTokenCounter.DefaultModel)
{
    public const string BaselineText = ".";

    private readonly ConcurrentDictionary<TokenKey, Task<int?>> _inFlight = new(TokenKey.Comparer.Instance);
    private Task<int?>? _baseline;
    private int _requests, _hits, _failures;

    public string Model { get; } = counter?.Model ?? model;
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
        var key = TokenCache.Key(Model, text);
        if (cache.TryGet(key, out var cached))
        {
            Interlocked.Increment(ref _hits);
            return Task.FromResult<int?>(cached);
        }
        if (counter is null) return Task.FromResult<int?>(null);
        return _inFlight.GetOrAdd(key, _ => FetchAsync(key, text, ct));
    }

    private async Task<int?> FetchAsync(TokenKey key, string text, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var count = await counter!.CountAsync(text, ct);
        if (count is null) Interlocked.Increment(ref _failures);
        else cache.Set(key, count.Value);
        return count;
    }
}

/// <summary>
/// A token figure: exact when every text in it was counted (or cached), otherwise an estimate from
/// <see cref="TokenEstimator"/>. <see cref="NearThreshold"/> marks an estimate within 5% of a size
/// threshold, where the estimate's error could put it on either side.
/// </summary>
public readonly record struct TokenFigure(int Value, bool Estimated)
{
    public bool NearThreshold { get; init; }

    /// <summary>An estimated sum some of whose parts were exact.</summary>
    public bool Mixed { get; init; }

    public string Source => Estimated ? "estimate" : "exact";

    /// <summary><see cref="Source"/>, with <c>mixed</c> for a sum of exact and estimated parts.</summary>
    public string Provenance => Mixed ? "mixed" : Source;

    public static TokenFigure Sum(IEnumerable<TokenFigure> figures)
    {
        int total = 0;
        bool estimated = false, exact = false;
        foreach (var f in figures)
        {
            total += f.Value;
            estimated |= f.Estimated;
            exact |= !f.Estimated || f.Mixed;
        }
        return new TokenFigure(total, estimated) { Mixed = estimated && exact };
    }

    /// <summary>This figure, marked near-threshold when it is estimated and within 5% of any of <paramref name="thresholds"/>.</summary>
    public TokenFigure Flag(params double[] thresholds)
    {
        var value = Value;
        return this with { NearThreshold = Estimated && thresholds.Any(t => IsNear(value, t)) };
    }

    public static bool IsNear(int value, double threshold) => threshold > 0 && Math.Abs(value - threshold) <= threshold * 0.05;

    public override string ToString() => (Estimated ? "~" : "") + Value + (NearThreshold ? " near-threshold" : "");
}

/// <summary>One type's text as the census reads it: the files it is declared in, and its declaration spans.</summary>
public sealed record TypeText(
    string Section, string Assembly, string Name, string File, int Line, bool IsClass,
    IReadOnlyList<(string Path, string Text)> Files, IReadOnlyList<string> Spans);

public sealed record ClassTokens(string Name, string File, int Line, TokenFigure Tokens);

/// <summary>
/// A section's tokens: the sum of its whole handwritten files. <see cref="Tests"/> is the same sum
/// over the test files attributed to the section.
/// </summary>
public sealed record SectionTokens(TokenFigure Total, int Files, List<ClassTokens> Classes)
{
    /// <summary>The part of <see cref="Total"/> in the satellite <c>.Contracts</c> assembly.</summary>
    public TokenFigure Contracts { get; init; }
    /// <summary>The rest of <see cref="Total"/>: the section's own assembly.</summary>
    public TokenFigure Main { get; init; }
    public TokenFigure Tests { get; init; }
    public int TestFiles { get; init; }
}

public sealed class TokenCensus
{
    public TokenFigure Total { get; init; }
    public int Files { get; init; }
    public Dictionary<string, SectionTokens> BySection { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every classified type's handwritten files and, for top-level classes and structs, its
    /// declaration spans across all partial files, doc comments and nested types included (a nested
    /// type is part of its container's text, so it is not a class of its own here). Generated files and migrations are
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
                c.File, c.Line, c.Type.TypeKind is TypeKind.Class or TypeKind.Struct && c.Type.ContainingType is null, files, spans));
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
    /// how its code is split into types or partials. Each text takes its exact count when
    /// <paramref name="measurer"/> has one and is estimated otherwise, so a figure is exact only
    /// when all of its texts are. <paramref name="tests"/> holds each section's attributed test files
    /// (see <see cref="TestMassAnalyzer.CollectFilesAsync"/>).
    /// </summary>
    public static async Task<TokenCensus> MeasureAsync(IReadOnlyList<TypeText> types, TokenMeasurer? measurer, CancellationToken ct,
        IReadOnlyDictionary<string, List<(string Path, string Text)>>? tests = null)
    {
        var fileText = new Dictionary<(string Assembly, string Path), string>();
        var fileSection = new Dictionary<(string Assembly, string Path), string>();
        foreach (var t in types)
            foreach (var (path, text) in t.Files)
                if (fileText.TryAdd((t.Assembly, path), text)) fileSection[(t.Assembly, path)] = t.Section;
        tests ??= new Dictionary<string, List<(string Path, string Text)>>();

        var figures = await FiguresAsync(fileText.Values
            .Concat(types.Where(t => t.IsClass).SelectMany(t => t.Spans))
            .Concat(tests.Values.SelectMany(files => files.Select(f => f.Text))), measurer, ct);

        var census = new TokenCensus { Total = TokenFigure.Sum(fileText.Values.Select(t => figures[t])), Files = fileText.Count };
        foreach (var section in fileSection.GroupBy(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sectionClasses = types
                .Where(t => t.IsClass && string.Equals(t.Section, section.Key, StringComparison.OrdinalIgnoreCase))
                .Select(t => new ClassTokens(t.Name, t.File, t.Line, TokenFigure.Sum(t.Spans.Select(s => figures[s]))))
                .OrderByDescending(c => c.Tokens.Value)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            var sectionTests = tests.FirstOrDefault(kv => string.Equals(kv.Key, section.Key, StringComparison.OrdinalIgnoreCase)).Value ?? [];
            census.BySection[section.Key] = new SectionTokens(
                TokenFigure.Sum(section.Select(f => figures[fileText[f]])), section.Count(), sectionClasses)
            {
                Main = TokenFigure.Sum(section.Where(f => !AssemblySections.IsContractsAssembly(f.Assembly)).Select(f => figures[fileText[f]])),
                Contracts = TokenFigure.Sum(section.Where(f => AssemblySections.IsContractsAssembly(f.Assembly)).Select(f => figures[fileText[f]])),
                Tests = TokenFigure.Sum(sectionTests.Select(f => figures[f.Text])),
                TestFiles = sectionTests.Count,
            };
        }
        return census;
    }

    /// <summary>Each distinct text's exact count where the measurer has one, else its estimate.</summary>
    private static async Task<Dictionary<string, TokenFigure>> FiguresAsync(IEnumerable<string> texts, TokenMeasurer? measurer, CancellationToken ct)
    {
        var distinct = texts.Distinct(StringComparer.Ordinal).ToList();
        var exact = measurer is null
            ? new int?[distinct.Count]
            : await Task.WhenAll(distinct.Select(t => measurer.MeasureAsync(t, ct)));
        var figures = new TokenFigure[distinct.Count];
        Parallel.For(0, distinct.Count, new ParallelOptions { CancellationToken = ct }, i =>
            figures[i] = exact[i] is { } count ? new TokenFigure(count, false) : new TokenFigure(TokenEstimator.Estimate(distinct[i]), true));
        var result = new Dictionary<string, TokenFigure>(distinct.Count, StringComparer.Ordinal);
        for (var i = 0; i < distinct.Count; i++) result[distinct[i]] = figures[i];
        return result;
    }
}

/// <summary>
/// The token path a command runs on: the durable cache, read on every run so cached exact counts
/// are used with or without a key, and the API for the rest only with <c>--count-tokens</c> and
/// <c>ANTHROPIC_API_KEY</c>. Whatever stays uncounted is estimated.
/// </summary>
public sealed class TokenSession : IDisposable
{
    private readonly AnthropicTokenCounter? _counter;
    private readonly TokenCache _cache;

    private TokenSession(AnthropicTokenCounter? counter, TokenCache cache, string cachePath)
    {
        _counter = counter;
        _cache = cache;
        CachePath = cachePath;
        Measurer = new TokenMeasurer(counter, cache);
    }

    public TokenMeasurer Measurer { get; }
    public string CachePath { get; }

    public static TokenSession Open(bool countTokens, string? cacheOption, TextWriter stderr)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (countTokens && string.IsNullOrWhiteSpace(apiKey))
            stderr.WriteLine("tokens: ANTHROPIC_API_KEY is not set; uncached files are estimated");
        var counter = countTokens && !string.IsNullOrWhiteSpace(apiKey) ? new AnthropicTokenCounter(apiKey) : null;
        var path = TokenCache.ResolvePath(cacheOption);
        return new TokenSession(counter, TokenCache.Load(path), path);
    }

    /// <summary>Appends what this run counted. A cache that cannot be written costs a warning, not the run.</summary>
    public void Save(TextWriter stderr)
    {
        try { _cache.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"tokens: cache not saved to {CachePath}: {ex.Message}");
        }
    }

    public void Dispose() => _counter?.Dispose();
}
