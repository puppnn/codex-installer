using System.IO;
using System.Net.Http;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class PackageDownloadService
{
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;

    public static async Task<string> DownloadAsync(
        PackageCandidate candidate,
        string downloadsDirectory,
        string targetArchitecture,
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        if (!CodexPackage.IsTrustedDownloadUrl(candidate.Url))
        {
            throw new InvalidOperationException("安装包链接不是受信任的 Microsoft HTTPS 下载地址。");
        }

        Directory.CreateDirectory(downloadsDirectory);
        var targetPath = DownloadPaths.PackagePath(candidate, downloadsDirectory);
        var tempPath = Path.Combine(
            downloadsDirectory,
            $".{Path.GetFileNameWithoutExtension(candidate.FileName)}.{Guid.NewGuid():N}.download.msix");

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CodexUpdater/1.0");

        try
        {
            using var response = await httpClient.GetAsync(
                candidate.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(finalUrl) || !CodexPackage.IsTrustedDownloadUrl(finalUrl))
            {
                throw new InvalidOperationException("安装包下载被重定向到非 Microsoft 地址，已停止下载。");
            }

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes > MaxPackageBytes)
            {
                throw new InvalidOperationException("安装包超过 2 GB 下载上限。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long downloaded = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;

                    downloaded += read;
                    if (downloaded > MaxPackageBytes)
                    {
                        throw new InvalidOperationException("安装包超过 2 GB 下载上限。");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (totalBytes is > 0)
                    {
                        progress.Report(downloaded * 100d / totalBytes.Value);
                    }
                }

                await destination.FlushAsync(cancellationToken);
            }

            ValidateDownloadedPackage(candidate, tempPath, targetArchitecture);
            WindowsTrustVerifier.EnsureValidSignature(tempPath);
            MsixPackageInspector.ValidateCodexPackage(tempPath, candidate, targetArchitecture);

            File.Move(tempPath, targetPath, overwrite: true);
            progress.Report(100);
            return targetPath;
        }
        finally
        {
            TryDelete(tempPath);
        }
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original download or validation error.
        }
    }
}
