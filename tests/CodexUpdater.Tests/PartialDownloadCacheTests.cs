using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PartialDownloadCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-cache-tests-" + Guid.NewGuid().ToString("N"));
    private const string Name = "Contoso.App_1.0.0.0_x64__1234567890123.msix";

    public PartialDownloadCacheTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Lease_ExcludesSecondInstanceUntilDisposed()
    {
        using (var lease = PartialDownloadCache.Acquire(_directory, Name))
        {
            Assert.Throws<IOException>(() => PartialDownloadCache.Acquire(_directory, Name));
            Assert.Throws<IOException>(() => PartialDownloadCache.Acquire(_directory, Name.ToLowerInvariant()));
        }
        using var next = PartialDownloadCache.Acquire(_directory, Name);
    }

    [Fact]
    public void Cleanup_OnlyDeletesOwnedExpiredPairsAndSkipsActiveDownload()
    {
        var now = DateTimeOffset.UtcNow;
        var unrelated = Path.Combine(_directory, ".another-tool.partial.backup");
        File.WriteAllText(unrelated, "leave intact");
        File.SetLastWriteTimeUtc(unrelated, now.AddDays(-20).UtcDateTime);
        string partial;
        using (var cache = PartialDownloadCache.Acquire(_directory, Name))
        {
            partial = cache.PartialPath;
            File.WriteAllText(partial, "part");
            new PartialDownloadState(Name, null, "\"v1\"", 100, now.AddDays(-8)).Save(cache.StatePath);
            PartialDownloadCache.Cleanup(_directory, now);
            Assert.True(File.Exists(partial));
        }
        PartialDownloadCache.Cleanup(_directory, now);
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".json"));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Cleanup_LeavesUnknownFilesInCacheAlone()
    {
        using var cache = PartialDownloadCache.Acquire(_directory, Name);
        var other = Path.Combine(Path.GetDirectoryName(cache.PartialPath)!, "unrelated.json");
        new PartialDownloadState(Name, null, "\"v1\"", 100, DateTimeOffset.UtcNow.AddDays(-20)).Save(other);
        PartialDownloadCache.Cleanup(_directory, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(other));
    }
}
