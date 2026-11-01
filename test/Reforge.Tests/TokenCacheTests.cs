namespace Reforge.Tests;

/// <summary>The durable token cache: keys, file format, concurrent writers, compaction.</summary>
public class TokenCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"reforge-cache-{Guid.NewGuid():N}", "token-cache.txt");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static TokenKey Key(int i, string model = "m") => TokenCache.Key(model, $"text {i}");

    [Fact]
    public void Key_SeparatesModels()
    {
        var cache = TokenCache.InMemory();
        cache.Set(TokenCache.Key("model-a", "same text"), 5);

        Assert.True(cache.TryGet(TokenCache.Key("model-a", "same text"), out _));
        Assert.False(cache.TryGet(TokenCache.Key("model-b", "same text"), out _));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndAppendsOnlyNewEntries()
    {
        var first = TokenCache.Load(_path);
        first.Set(Key(1), 10);
        first.Set(Key(2, "other-model"), 20);
        first.Save();

        var second = TokenCache.Load(_path);
        second.Set(Key(1), 10); // already on disk
        second.Set(Key(3), 30);
        second.Save();

        var loaded = TokenCache.Load(_path);
        Assert.Equal(3, loaded.Count);
        Assert.True(loaded.TryGet(Key(2, "other-model"), out var two) && two == 20);
        Assert.Equal(3, File.ReadAllLines(_path).Length);
    }

    [Fact]
    public async Task TwoWriters_LoseNothing()
    {
        // Each writer loads before the other saves, then saves repeatedly while the other does too.
        var writers = Enumerable.Range(0, 2).Select(w => TokenCache.Load(_path)).ToList();
        await Task.WhenAll(writers.Select((cache, w) => Task.Run(() =>
        {
            for (var round = 0; round < 20; round++)
            {
                for (var i = 0; i < 50; i++) cache.Set(Key(w * 100_000 + round * 1000 + i), i + 1);
                cache.Save();
            }
        })));

        var loaded = TokenCache.Load(_path);
        Assert.Equal(2 * 20 * 50, loaded.Count);
        for (var w = 0; w < 2; w++)
            Assert.True(loaded.TryGet(Key(w * 100_000 + 19 * 1000 + 49), out var v) && v == 50);
    }

    [Fact]
    public async Task CompactingWhileAnotherAppends_LosesNothing()
    {
        // 2,000 lines over 1,000 entries: the next save rewrites the file.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var seed = TokenCache.Load(_path);
        for (var i = 0; i < 1000; i++) seed.Set(Key(i), 1);
        seed.Save();
        File.AppendAllText(_path, File.ReadAllText(_path));

        var compactor = TokenCache.Load(_path);
        var appender = TokenCache.Load(_path);
        compactor.Set(Key(5000), 2);
        for (var i = 0; i < 100; i++) appender.Set(Key(6000 + i), 3);
        await Task.WhenAll(Task.Run(compactor.Save), Task.Run(appender.Save));

        var loaded = TokenCache.Load(_path);
        Assert.Equal(1000 + 1 + 100, loaded.Count);
        Assert.True(File.ReadAllLines(_path).Length < 1200, "compaction dropped the duplicate lines");
    }

    [Fact]
    public void TornLine_IsIgnored_AndTheNextAppendStartsAFreshLine()
    {
        var cache = TokenCache.Load(_path);
        cache.Set(Key(1), 10);
        cache.Save();
        // A writer died mid-line: no newline, hash cut short.
        File.AppendAllText(_path, "123 m ABCDEF");

        Assert.Equal(1, TokenCache.Load(_path).Count);

        var next = TokenCache.Load(_path);
        next.Set(Key(2), 20);
        next.Save();

        var loaded = TokenCache.Load(_path);
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded.TryGet(Key(2), out var v) && v == 20);
    }

    [Fact]
    public void LegacyJson_IsReplacedOnSave()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"ABC\": 12}");

        var cache = TokenCache.Load(_path);
        Assert.Equal(0, cache.Count);
        cache.Set(Key(1), 10);
        cache.Save();

        Assert.Equal(1, TokenCache.Load(_path).Count);
        Assert.DoesNotContain("{", File.ReadAllText(_path));
    }

    [Fact]
    public void ResolvePath_OptionThenEnvironmentThenDefault()
    {
        var saved = Environment.GetEnvironmentVariable(TokenCache.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(TokenCache.EnvironmentVariable, "/from/env.txt");
            Assert.Equal("/from/option.txt", TokenCache.ResolvePath("/from/option.txt"));
            Assert.Equal("/from/env.txt", TokenCache.ResolvePath(null));

            Environment.SetEnvironmentVariable(TokenCache.EnvironmentVariable, null);
            Assert.Equal(TokenCache.DefaultPath, TokenCache.ResolvePath(null));
            Assert.DoesNotContain(Path.GetTempPath(), TokenCache.DefaultPath);
        }
        finally { Environment.SetEnvironmentVariable(TokenCache.EnvironmentVariable, saved); }
    }
}
