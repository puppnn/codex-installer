namespace CodexUpdater.Core;

public static class DownloadPaths
{
    public static string DefaultDownloadsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUpdater",
            "Downloads");

    public static string PackagePath(PackageCandidate candidate, string downloadsDirectory)
    {
        return Path.Combine(downloadsDirectory, candidate.FileName);
    }
}
