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
        CancellationToken cancellationToken = default,
        long byteLimit = MaxPackageBytes,
        DownloadRoute route = DownloadRoute.Auto,
        IProgress<string>? status = null)
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
            cancellationToken,
            byteLimit, route, status);
        return result.Validation with { FilePath = result.FilePath };
    }

    public static async Task<DownloadedStorePackage> DownloadDependencyPackageAsync(
        StorePackageCandidate candidate,
        PackageDependencyRequirement requirement,
        string downloadsDirectory,
        string targetArchitecture,
        IProgress<double> progress,
        CancellationToken cancellationToken = default,
        long byteLimit = MaxPackageBytes,
        DownloadRoute route = DownloadRoute.Auto,
        IProgress<string>? status = null)
    {
        var result = await DownloadAndValidateAsync(
            candidate.FileName,
            candidate.Url,
            downloadsDirectory,
            candidate.PageHash,
            progress,
            path => ValidateStorePackage(path, candidate, targetArchitecture, null, requirement),
            cancellationToken,
            byteLimit, route, status);
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
                identity.ApplicationVersion != package.Identity.ApplicationVersion ||
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
                !identity.IsFramework || identity.ApplicationVersion < dependency.MinimumVersion)
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

    internal static async Task<DownloadResult<T>> DownloadAndValidateAsync<T>(
        string fileName,
        string url,
        string downloadsDirectory,
        string? expectedPageHash,
        IProgress<double> progress,
        Func<string, T> validator,
        CancellationToken cancellationToken,
        long byteLimit = MaxPackageBytes,
        DownloadRoute route = DownloadRoute.Auto,
        IProgress<string>? status = null,
        Func<bool, HttpClient>? clientFactory = null)
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

        var maximumBytes = Math.Min(byteLimit, MaxPackageBytes);
        if (maximumBytes <= 0) throw new InvalidOperationException("主包和依赖包已达到 4 GB 下载上限。");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(downloadsDirectory);
        PartialDownloadCache.Cleanup(downloadsDirectory, DateTimeOffset.UtcNow);
        using var cache = PartialDownloadCache.Acquire(downloadsDirectory, fileName);
        var targetPath = Path.Combine(downloadsDirectory, fileName);
        var existing = await Task.Run(() =>
        {
            var valid = TryValidateExistingPackage(targetPath, expectedPageHash, validator, out var value);
            return (Valid: valid, Value: value);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (existing.Valid)
        {
            if (new FileInfo(targetPath).Length > maximumBytes)
                throw new InvalidOperationException("安装包超过本次安装计划的剩余大小上限。");
            progress.Report(100);
            return new DownloadResult<T>(targetPath, existing.Value, true);
        }

        var partialPath = cache.PartialPath;
        var statePath = cache.StatePath;
        for (var pass = 0; ; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream destination;
            try
            {
                destination = new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 128 * 1024, useAsync: true);
            }
            catch (IOException exception) when (exception.HResult == unchecked((int)0x80070020))
            {
                throw new InvalidOperationException("同一个安装包正在由另一个窗口下载，请等待其完成后再试。");
            }

            var keepPartial = false;
            var resumed = false;
            var stateSaved = false;
            try
            {
                await using (destination)
                {
                    var resume = pass == 0
                        ? PartialDownloadState.TryLoad(statePath, fileName, expectedPageHash, destination.Length, maximumBytes, DateTimeOffset.UtcNow)
                        : null;
                    if (resume is null)
                    {
                        destination.SetLength(0);
                        TryDelete(statePath);
                    }
                    else
                    {
                        resumed = true;
                        stateSaved = true;
                        status?.Report($"发现上次未完成的下载（{destination.Length / 1048576d:F1} / {resume.TotalLength / 1048576d:F1} MiB），正在继续...");
                    }
                    await PackageTransferService.DownloadAsync(url, destination, maximumBytes, route, progress, status, cancellationToken,
                        clientFactory: clientFactory,
                        resume: resume,
                        validatorChanged: (etag, total) => stateSaved = SaveOrClearPartialState(statePath, fileName, expectedPageHash, etag, total));
                }
                cancellationToken.ThrowIfCancellationRequested();
                status?.Report("下载完成，正在验证文件完整性和数字签名...");
                T validation;
                using (var verificationLock = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        validation = await Task.Run(() =>
                        {
                            if (expectedPageHash is { Length: 40 or 64 } && expectedPageHash.All(Uri.IsHexDigit) &&
                                !MatchesExpectedPageHash(partialPath, expectedPageHash))
                                throw new InvalidDataException("安装包哈希与查询结果不一致，已停止安装，请重新检查更新。");
                            return validator(partialPath);
                        }, cancellationToken);
                    }
                    catch (Exception ex) when (resumed && pass == 0 && ex is InvalidDataException or InvalidOperationException)
                    {
                        status?.Report("续传文件未通过校验，正在重新完整下载...");
                        continue;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partialPath, targetPath, overwrite: true);
                progress.Report(100);
                return new DownloadResult<T>(targetPath, validation, false);
            }
            catch (Exception exception)
            {
                // Network interruptions, timeouts, cancellation and closing the app keep the bytes for next time;
                // anything that questions the data itself discards them.
                if ((exception is HttpRequestException or TimeoutException or OperationCanceledException) && stateSaved && File.Exists(statePath))
                {
                    keepPartial = true;
                    throw;
                }
                throw;
            }
            finally
            {
                if (!keepPartial)
                {
                    TryDelete(partialPath);
                    TryDelete(statePath);
                }
            }
        }
    }

    private static bool SaveOrClearPartialState(string statePath, string fileName, string? pageHash, string? etag, long? total)
    {
        try
        {
            if (etag is not null && total is > 0)
                new PartialDownloadState(fileName, pageHash, etag, total.Value, DateTimeOffset.UtcNow).Save(statePath);
            else
                File.Delete(statePath);
            return etag is not null && total is > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Resuming after a restart is best effort; the current download continues without it.
            return false;
        }
    }

    internal static Task<HttpResponseMessage> GetTrustedResponseAsync(
        HttpClient client,
        string originalUrl,
        string attemptUrl,
        CancellationToken cancellationToken)
        => TrustedHttpService.GetResponseAsync(client, new Uri(attemptUrl),
            uri => IsAllowedFinalDownloadUrl(originalUrl, uri.AbsoluteUri), cancellationToken);

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
            if (!File.Exists(path) || new FileInfo(path).Length > MaxPackageBytes)
                return false;
            using var packageLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!MatchesExpectedPageHash(path, expectedPageHash))
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

    internal sealed record DownloadResult<T>(
        string FilePath,
        T Validation,
        bool ReusedExistingFile);
}

internal sealed record PackageDownloadOutcome(string FilePath, bool ReusedExistingFile);
