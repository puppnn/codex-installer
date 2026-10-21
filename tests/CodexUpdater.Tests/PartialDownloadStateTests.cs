using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PartialDownloadStateTests
{
    private const string FileName = "OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0.msix";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static PartialDownloadState State(string etag = "\"v1\"", DateTimeOffset? savedAt = null) =>
        new(FileName, "ABC123", etag, 100, savedAt ?? Now);

    [Fact]
    public void Usable_ForSameFileAndHashIgnoringCase() =>
        Assert.True(State().IsUsableFor(FileName.ToLowerInvariant(), "abc123", 40, 1000, Now));

    [Theory]
    [InlineData("OpenAI.Codex_26.928.1915.0_x64__2p2nqsd0c76g0.msix", "ABC123", 40, 1000, 0)]
    [InlineData(FileName, "DEF456", 40, 1000, 0)]
    [InlineData(FileName, null, 40, 1000, 0)]
    [InlineData(FileName, "ABC123", 0, 1000, 0)]
    [InlineData(FileName, "ABC123", 101, 1000, 0)]
    [InlineData(FileName, "ABC123", 40, 50, 0)]
    [InlineData(FileName, "ABC123", 40, 1000, 8)]
    [InlineData(FileName, "ABC123", 40, 1000, -1)]
    public void NotUsable_WhenAnythingDiffers(string fileName, string? hash, long partialLength, long maximumBytes, int ageDays) =>
        Assert.False(State(savedAt: Now.AddDays(-ageDays)).IsUsableFor(fileName, hash, partialLength, maximumBytes, Now));

    [Theory]
    [InlineData("W/\"v1\"")]
    [InlineData("v1")]
    [InlineData("\"\"")]
    public void WeakOrMalformedETag_IsNotUsable(string etag) =>
        Assert.False(State(etag).IsUsableFor(FileName, "ABC123", 40, 1000, Now));

    [Fact]
    public void SaveAndLoad_RoundTripsAndRejectsCorruptState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.partial.json");
        try
        {
            State().Save(path);
            Assert.Equal(State(), PartialDownloadState.TryLoad(path, FileName, "ABC123", 40, 1000, Now));
            Assert.False(File.Exists(path + ".tmp"));

            File.WriteAllText(path, "{not json");
            Assert.Null(PartialDownloadState.TryLoad(path, FileName, "ABC123", 40, 1000, Now));
            Assert.Null(PartialDownloadState.TryLoad(path + ".missing", FileName, "ABC123", 40, 1000, Now));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FileName\":null,\"ETag\":null}")]
    [InlineData("{\"FileName\":\"test.msix\",\"ETag\":null}")]
    public void NullOrMissingFields_AreRejected(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, json);
            Assert.Null(PartialDownloadState.TryLoad(path, FileName, "ABC123", 40, 1000, Now));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"bad\"etag\"")]
    [InlineData("\"tag\r\ninjected\"")]
    [InlineData("*")]
    public void HeaderInjectionOrInvalidETag_IsRejected(string etag) =>
        Assert.False(State(etag).IsUsableFor(FileName, "ABC123", 40, 1000, Now));
}
