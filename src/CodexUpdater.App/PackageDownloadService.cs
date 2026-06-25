using System.IO;
using System.Net.Http;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class PackageDownloadService
{
    public static async Task<string> DownloadAsync(
        PackageCandidate candidate,
        string downloadsDirectory,
        string targetArchitecture,
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(downloadsDirectory);
        var targetPath = DownloadPaths.PackagePath(candidate, downloadsDirectory);
        var tempPath = targetPath + ".download";

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CodexUpdater/1.0");

        using var response = await httpClient.GetAsync(
            candidate.Url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(tempPath);

        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;

            if (totalBytes is > 0)
            {
                progress.Report(downloaded * 100d / totalBytes.Value);
            }
        }

        destination.Close();

        ValidateDownloadedPackage(candidate, tempPath, targetArchitecture);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(tempPath, targetPath);
        progress.Report(100);
        return targetPath;
    }

    public static void ValidateDownloadedPackage(
        PackageCandidate candidate,
        string path,
        string targetArchitecture)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidOperationException("下载的文件不存在或为空。");
        }

        if (!Path.GetExtension(candidate.FileName).Equals(".msix", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("下载的文件不是 MSIX 安装包。");
        }

        if (!CodexPackage.IsExpectedFileName(candidate.FileName, targetArchitecture))
        {
            throw new InvalidOperationException($"下载的安装包名称不匹配 OpenAI.Codex {targetArchitecture} MSIX。");
        }
    }
}
