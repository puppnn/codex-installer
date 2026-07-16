using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class PackageDownloadService
{
    public const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxInstallationPlanBytes = 4L * 1024 * 1024 * 1024;

    public static async Task<PackageDownloadOutcome> DownloadAsync(
        PackageCandidate candidate,
        string downloadsDirectory,
        string targetArchitecture,
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        var result = await DownloadAndValidateAsync(
            candidate.FileName,
            candidate.Url,
            downloadsDirectory,
            candidate.PageHash,
            progress,
            path =>
            {
                ValidateDownloadedPackage(candidate, path, targetArchitecture);
                WindowsTrustVerifier.EnsureValidSignature(path);
                MsixPackageInspector.ValidateCodexPackage(path, candidate, targetArchitecture);
                return true;
            },
            cancellationToken);
        return new PackageDownloadOutcome(result.FilePath, result.ReusedExistingFile);
    }

    public static async Task<DownloadedStorePackage> DownloadMainPackageAsync(
        StorePackageCandidate candidate,
        StoreProductMetadata product,
        string downloadsDirectory,
        string targetArchitecture,
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        var expectedFamilies = new HashSet<string>(
            product.PackageFamilyNames,
            StringComparer.OrdinalIgnoreCase);
        var result = await DownloadAndValidateAsync(
            candidate.FileName,
            candidate.Url,
            downloadsDirectory,
            candidate.PageHash,
            progress,
            path => ValidateStorePackage(path, candidate, targetArchitecture, expectedFamilies, null),
            cancellationToken);
        return result.Validation with { FilePath = result.FilePath };
    }

    public static async Task<DownloadedStorePackage> DownloadDependencyPackageAsync(
        StorePackageCandidate candidate,
        PackageDependencyRequirement requirement,
        string downloadsDirectory,
        string targetArchitecture,
        IProgress<double> progress,
        CancellationToken cancellationToken = default)
    {
        var result = await DownloadAndValidateAsync(
            candidate.FileName,
            candidate.Url,
            downloadsDirectory,
            candidate.PageHash,
            progress,
            path => ValidateStorePackage(path, candidate, targetArchitecture, null, requirement),
            cancellationToken);
        return result.Validation with { FilePath = result.FilePath };
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

    public static FileStream OpenAndRevalidateForInstall(DownloadedStorePackage package)
    {
        var file = new FileInfo(package.FilePath);
        if (!file.Exists || file.Length == 0)
        {
            throw new FileNotFoundException("待安装包不存在或为空。", package.FilePath);
        }

        var packageLock = new FileStream(
            package.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(packageLock));
            packageLock.Position = 0;
            if (!actualHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("待安装包在下载后发生了变化。");
            }

            WindowsTrustVerifier.EnsureValidSignature(package.FilePath);
            var identity = PackageArtifactInspector.Inspect(
                package.FilePath,
                package.Candidate.Format,
                package.Identity.Architecture);
            var familyName = WindowsPackageIdentityService.GetPackageFamilyName(identity);
            if (!identity.Name.Equals(package.Identity.Name, StringComparison.OrdinalIgnoreCase) ||
                !identity.Publisher.Equals(package.Identity.Publisher, StringComparison.OrdinalIgnoreCase) ||
                !identity.ResourceId.Equals(package.Identity.ResourceId, StringComparison.OrdinalIgnoreCase) ||
                !identity.Architecture.Equals(package.Identity.Architecture, StringComparison.OrdinalIgnoreCase) ||
                identity.Version != package.Identity.Version ||
                identity.Format != package.Identity.Format ||
                !familyName.Equals(package.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("待安装包在下载后发生了变化。");
            }

            return packageLock;
        }
        catch
        {
            packageLock.Dispose();
            throw;
        }
    }

    private static DownloadedStorePackage ValidateStorePackage(
        string path,
        StorePackageCandidate candidate,
        string targetArchitecture,
        IReadOnlySet<string>? expectedMainFamilies,
        PackageDependencyRequirement? dependency)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > MaxPackageBytes)
        {
            throw new InvalidOperationException("下载的安装包不存在、为空或超过 2 GB 上限。");
        }

        WindowsTrustVerifier.EnsureValidSignature(path);
        var identity = PackageArtifactInspector.Inspect(path, candidate.Format, targetArchitecture);
        if (!identity.Name.Equals(candidate.IdentityName, StringComparison.OrdinalIgnoreCase) ||
            identity.Version != candidate.Version ||
            !NormalizeResourceId(identity.ResourceId).Equals(
                NormalizeResourceId(candidate.ResourceId),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包 Manifest 身份与 rg-adguard 文件名不匹配。");
        }

        if (candidate.Format is StorePackageFormat.Msix or StorePackageFormat.Appx &&
            !identity.Architecture.Equals(candidate.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"安装包 Manifest 架构 {identity.Architecture} 与文件名 {candidate.Architecture} 不匹配。");
        }

        var packageFamilyName = WindowsPackageIdentityService.GetPackageFamilyName(identity);
        if (!packageFamilyName.Equals(candidate.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"安装包 PFN 与文件名不匹配：{packageFamilyName} / {candidate.PackageFamilyName}。");
        }

        if (expectedMainFamilies is not null && !expectedMainFamilies.Contains(packageFamilyName))
        {
            throw new InvalidOperationException($"安装包 {packageFamilyName} 不属于目标 Store 应用。");
        }

        if (dependency is not null)
        {
            if (!identity.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase) ||
                !identity.Publisher.Equals(dependency.Publisher, StringComparison.OrdinalIgnoreCase) ||
                (dependency.PublisherId is not null &&
                 !candidate.PublisherId.Equals(dependency.PublisherId, StringComparison.OrdinalIgnoreCase)) ||
                identity.Version < dependency.MinimumVersion)
            {
                throw new InvalidOperationException($"依赖 {dependency.Name} 的 Manifest 身份不匹配。");
            }

            if (!identity.Architecture.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase) &&
                !identity.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"依赖 {dependency.Name} 的架构 {identity.Architecture} 与主包不兼容。");
            }
        }

        var sha256 = ComputeSha256(path);
        return new DownloadedStorePackage(candidate, "", identity, packageFamilyName, sha256);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string NormalizeResourceId(string resourceId)
    {
        return resourceId == "~" ? "" : resourceId;
    }

    private static async Task<DownloadResult<T>> DownloadAndValidateAsync<T>(
        string fileName,
        string url,
        string downloadsDirectory,
        string? expectedPageHash,
        IProgress<double> progress,
        Func<string, T> validator,
        CancellationToken cancellationToken)
    {
        if (!CodexPackage.IsTrustedDownloadUrl(url))
        {
            throw new InvalidOperationException("安装包链接不是受信任的 Microsoft 下载地址。");
        }

        if (!fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            throw new InvalidOperationException("安装包文件名无效。");
        }

        Directory.CreateDirectory(downloadsDirectory);
        var targetPath = Path.Combine(downloadsDirectory, fileName);
        if (TryValidateExistingPackage(
            targetPath,
            expectedPageHash,
            validator,
            out var existingValidation))
        {
            progress.Report(100);
            return new DownloadResult<T>(targetPath, existingValidation, true);
        }

        var tempPath = Path.Combine(
            downloadsDirectory,
            $".{Path.GetFileNameWithoutExtension(fileName)}.{Guid.NewGuid():N}.download{Path.GetExtension(fileName)}");

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
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CodexUpdater/1.1");

        try
        {
            HttpResponseMessage? response = null;
            var attemptUrls = BuildDownloadAttemptUrls(url);
            for (var index = 0; index < attemptUrls.Count; index++)
            {
                try
                {
                    response = await httpClient.GetAsync(
                        attemptUrls[index],
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                    break;
                }
                catch (HttpRequestException) when (index + 1 < attemptUrls.Count)
                {
                    response?.Dispose();
                    response = null;
                    progress.Report(0);
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }
            }

            using var successfulResponse = response
                ?? throw new HttpRequestException("Microsoft 安装包下载请求失败。");

            var finalUrl = successfulResponse.RequestMessage?.RequestUri?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(finalUrl) || !IsAllowedFinalDownloadUrl(url, finalUrl))
            {
                throw new InvalidOperationException("安装包下载被重定向到非 Microsoft 地址，已停止下载。");
            }

            var totalBytes = successfulResponse.Content.Headers.ContentLength;
            if (totalBytes > MaxPackageBytes)
            {
                throw new InvalidOperationException("单个安装包超过 2 GB 下载上限。");
            }

            await using var source = await successfulResponse.Content.ReadAsStreamAsync(cancellationToken);
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
                        throw new InvalidOperationException("单个安装包超过 2 GB 下载上限。");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (totalBytes is > 0)
                    {
                        progress.Report(downloaded * 100d / totalBytes.Value);
                    }
                }

                await destination.FlushAsync(cancellationToken);
            }

            var validation = validator(tempPath);
            File.Move(tempPath, targetPath, overwrite: true);
            progress.Report(100);
            return new DownloadResult<T>(targetPath, validation, false);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal static IReadOnlyList<string> BuildDownloadAttemptUrls(string trustedUrl)
    {
        if (!CodexPackage.IsTrustedDownloadUrl(trustedUrl))
        {
            throw new InvalidOperationException("安装包链接不是受信任的 Microsoft 下载地址。");
        }

        var original = new Uri(trustedUrl);
        if (!original.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return [original.AbsoluteUri];
        }

        var secure = new UriBuilder(original)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
        }.Uri.AbsoluteUri;
        return [secure, original.AbsoluteUri];
    }

    internal static bool IsAllowedFinalDownloadUrl(string originalUrl, string finalUrl)
    {
        if (!CodexPackage.IsTrustedDownloadUrl(originalUrl) ||
            !CodexPackage.IsTrustedDownloadUrl(finalUrl))
        {
            return false;
        }

        var original = new Uri(originalUrl);
        var final = new Uri(finalUrl);
        return original.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            final.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateExistingPackage<T>(
        string path,
        string? expectedPageHash,
        Func<string, T> validator,
        out T validation)
    {
        validation = default!;
        try
        {
            if (!File.Exists(path) || !MatchesExpectedPageHash(path, expectedPageHash))
            {
                return false;
            }

            validation = validator(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool MatchesExpectedPageHash(string path, string? expectedPageHash)
    {
        var expected = expectedPageHash?.Trim();
        if (expected is not { Length: 40 or 64 } || !expected.All(Uri.IsHexDigit))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = expected.Length == 40
            ? SHA1.HashData(stream)
            : SHA256.HashData(stream);
        return Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase);
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

    private sealed record DownloadResult<T>(
        string FilePath,
        T Validation,
        bool ReusedExistingFile);
}

internal sealed record PackageDownloadOutcome(string FilePath, bool ReusedExistingFile);
