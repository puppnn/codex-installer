using System.Text.Json;
using System.Net.Http.Headers;

namespace CodexUpdater.Core;

// Sidecar for a partially downloaded package, so an interrupted download survives closing the app.
// Resuming needs a strong ETag and the full length; the server re-checks the ETag through If-Range, and the
// finished file is still verified against the rg-adguard hash and its signature before installation.
public sealed record PartialDownloadState(string FileName, string? PageHash, string ETag, long TotalLength, DateTimeOffset SavedAt)
{
    public const int CurrentVersion = 1;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public int Version { get; init; } = CurrentVersion;
    public string Owner { get; init; } = "CodexUpdater";

    public bool HasValidIdentity => Version == CurrentVersion && Owner == "CodexUpdater" &&
        !string.IsNullOrWhiteSpace(FileName) && FileName == Path.GetFileName(FileName) &&
        FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        Path.GetExtension(FileName).ToLowerInvariant() is ".msix" or ".appx" or ".msixbundle" or ".appxbundle" &&
        EntityTagHeaderValue.TryParse(ETag, out var tag) && !tag.IsWeak && tag.Tag != "*" &&
        tag.Tag.Length > 2 && TotalLength > 0;

    public bool IsUsableFor(string fileName, string? pageHash, long partialLength, long maximumBytes, DateTimeOffset now)
    {
        var age = now - SavedAt;
        return HasValidIdentity &&
            FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(PageHash ?? "", pageHash ?? "", StringComparison.OrdinalIgnoreCase) &&
            TotalLength > 0 && TotalLength <= maximumBytes &&
            partialLength > 0 && partialLength <= TotalLength &&
            age >= TimeSpan.Zero && age <= MaxAge;
    }

    public static PartialDownloadState? TryLoad(
        string statePath, string fileName, string? pageHash, long partialLength, long maximumBytes, DateTimeOffset now)
    {
        try
        {
            var state = Read(statePath);
            return state is not null && state.IsUsableFor(fileName, pageHash, partialLength, maximumBytes, now) ? state : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static PartialDownloadState? Read(string statePath)
    {
        try
        {
            var info = new FileInfo(statePath);
            if (!info.Exists || info.Length is <= 0 or > 16384 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = JsonSerializer.Deserialize<PartialDownloadState>(stream);
            return state?.HasValidIdentity == true ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string statePath)
    {
        var tempPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, statePath, overwrite: true);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }
}
