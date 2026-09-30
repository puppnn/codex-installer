using System.IO;
using System.Net;
using System.Net.Http;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal sealed record PackageOperationProgress(string Message, double? Percentage = null);

internal static class PackagePlanService
{
    public static async Task<PackageInstallationPlan> DownloadAsync(
        StoreProductMetadata product,
        StorePackageCandidate selected,
        IReadOnlyList<StorePackageCandidate> candidates,
        string directory,
        string targetArchitecture,
        bool includeInstalledDependencies,
        Func<CancellationToken, Task<IReadOnlyList<StorePackageCandidate>>> refreshCandidates,
        IProgress<PackageOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var available = candidates;
        if (selected.IsExpired || candidates.Any(candidate => candidate.IsExpired))
        {
            progress.Report(new("临时链接已过期，正在重新查询..."));
            available = await refreshCandidates(cancellationToken);
            selected = Rematch(selected, available);
        }
        async Task<DownloadedStorePackage> DownloadOneAsync(
            StorePackageCandidate candidate, PackageDependencyRequirement? requirement, string architecture, long remainingBytes)
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress.Report(new($"正在下载并校验：{candidate.FileName}", 0));
                    var downloadProgress = new Progress<double>(percentage => progress.Report(new(
                        percentage >= 100 ? $"正在验证安装包：{candidate.FileName}" : $"正在下载：{candidate.FileName}", percentage)));
                    return requirement is null
                        ? await PackageDownloadService.DownloadMainPackageAsync(
                            candidate, product, directory, architecture, downloadProgress, cancellationToken, remainingBytes)
                        : await PackageDownloadService.DownloadDependencyPackageAsync(
                            candidate, requirement, directory, architecture, downloadProgress, cancellationToken, remainingBytes);
                }
                catch (HttpRequestException ex) when (attempt == 0 && ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    progress.Report(new("临时链接失效，正在重新查询一次..."));
                    available = await refreshCandidates(cancellationToken);
                    candidate = Rematch(candidate, available);
                }
                catch (HttpRequestException ex) when (attempt == 0 &&
                    (ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                     (int?)ex.StatusCode >= 500))
                {
                    progress.Report(new("网络请求失败，正在重试一次..."));
                    await Task.Delay(1500, cancellationToken);
                }
            }
        }

        var main = await DownloadOneAsync(selected, null, targetArchitecture, PackageDownloadService.MaxPackageBytes);
        var totalBytes = new FileInfo(main.FilePath).Length;
        var dependencyArchitecture = main.Identity.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            ? targetArchitecture : main.Identity.Architecture;
        var dependencies = new Dictionary<string, DownloadedStorePackage>(StringComparer.OrdinalIgnoreCase);
        var handled = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<PackageDependencyRequirement>(main.Identity.Dependencies.Where(item => !item.IsOptional));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requirement = queue.Dequeue();
            requirement = requirement with { PublisherId = WindowsPackageIdentityService.GetPublisherId(requirement.Name, requirement.Publisher) };
            var key = $"{requirement.Name}_{requirement.PublisherId}";
            if (handled.TryGetValue(key, out var version) && version >= requirement.MinimumVersion) continue;
            if (!includeInstalledDependencies && await PackageInstallationService.IsDependencyInstalledAsync(
                requirement, dependencyArchitecture, cancellationToken))
            {
                handled[key] = requirement.MinimumVersion;
                progress.Report(new($"依赖已安装：{requirement.Name}"));
                continue;
            }

            var resolved = PackageDependencyResolver.Resolve([requirement], available, dependencyArchitecture)[0];
            var dependency = await DownloadOneAsync(resolved.Candidate, requirement, dependencyArchitecture,
                PackageDownloadService.MaxInstallationPlanBytes - totalBytes);
            dependencies[key] = dependency;
            handled[key] = dependency.Identity.ApplicationVersion;
            totalBytes += new FileInfo(dependency.FilePath).Length;
            foreach (var transitive in dependency.Identity.Dependencies.Where(item => !item.IsOptional)) queue.Enqueue(transitive);
        }

        return new PackageInstallationPlan(product, main, dependencies.Values.ToArray());
    }

    internal static StorePackageCandidate Rematch(StorePackageCandidate original, IReadOnlyList<StorePackageCandidate> candidates) =>
        candidates.FirstOrDefault(candidate => !candidate.IsExpired &&
            candidate.PackageFamilyName.Equals(original.PackageFamilyName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Version == original.Version &&
            candidate.Architecture.Equals(original.Architecture, StringComparison.OrdinalIgnoreCase) &&
            candidate.Format == original.Format &&
            candidate.ResourceId.Equals(original.ResourceId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("重新查询后没有找到与原选择一致的安装包，请重新检查更新。");
}
