using System.Security.Cryptography;
using System.Text;

namespace CodexUpdater.Core;

public sealed class PartialDownloadCache : IDisposable
{
    public const string DirectoryName = ".codex-installer-partials";
    private readonly FileStream _lease;
    public string PartialPath { get; }
    public string StatePath => PartialPath + ".json";

    private PartialDownloadCache(string partialPath, FileStream lease)
    {
        PartialPath = partialPath;
        _lease = lease;
    }

    public static PartialDownloadCache Acquire(string downloadsDirectory, string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("安装包文件名无效。");
        var directory = Path.GetFullPath(Path.Combine(downloadsDirectory, DirectoryName));
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        var partial = Path.Combine(directory, Key(fileName) + ".partial" + Path.GetExtension(fileName).ToLowerInvariant());
        var lockPath = partial + ".lock";
        RejectLink(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            RejectLink(partial);
            RejectLink(partial + ".json");
            return new PartialDownloadCache(partial, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static void Cleanup(string downloadsDirectory, DateTimeOffset now)
    {
        var directory = Path.GetFullPath(Path.Combine(downloadsDirectory, DirectoryName));
        if (!Directory.Exists(directory) || IsLink(directory)) return;
        try
        {
            foreach (var statePath in Directory.EnumerateFiles(directory, "*.json"))
            {
                var state = PartialDownloadState.Read(statePath);
                if (state is null || now - state.SavedAt <= PartialDownloadState.MaxAge) continue;
                var expected = Path.Combine(directory, Key(state.FileName) + ".partial" + Path.GetExtension(state.FileName).ToLowerInvariant() + ".json");
                if (!expected.Equals(statePath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var cache = Acquire(downloadsDirectory, state.FileName);
                    state = PartialDownloadState.Read(cache.StatePath);
                    if (state is null || now - state.SavedAt <= PartialDownloadState.MaxAge) continue;
                    File.Delete(cache.PartialPath);
                    File.Delete(cache.StatePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Key(string fileName) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName.ToUpperInvariant())));
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && IsLink(path))
            throw new IOException("续传缓存不能使用符号链接或重解析点。");
    }

    // Leave the small lock file in place: deleting it after release could race another owner.
    public void Dispose() => _lease.Dispose();
}
