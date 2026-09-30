using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class PackageInstallationService
{
    public static string HostArchitecture => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public static async Task ValidateForInstallAsync(DownloadedStorePackage package, CancellationToken cancellationToken)
    {
        var installed = (await CodexSystemService.GetInstalledPackagesAsync(package.Identity.Name, cancellationToken))
            .Where(item => item.PackageFamilyName.Equals(package.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();
        var reason = PackageInstallationPolicy.GetBlockReason(
            package.Identity, RuntimeInformation.OSArchitecture, Environment.OSVersion.Version, installed?.Version);
        if (reason is not null) throw new InvalidOperationException(reason);
    }

    public static async Task<ProcessRunResult> InstallAsync(PackageInstallationPlan plan, CancellationToken cancellationToken)
    {
        await ValidateForInstallAsync(plan.MainPackage, cancellationToken);
        var dependencies = await ResolveLocalDependenciesAsync(plan.MainPackage, plan.Dependencies, cancellationToken);
        if (dependencies.Missing.Count > 0)
            throw new InvalidOperationException("缺少必需依赖：\n" + string.Join("\n", dependencies.Missing));
        var locks = new List<FileStream>();
        try
        {
            foreach (var package in new[] { plan.MainPackage }.Concat(plan.Dependencies))
            {
                cancellationToken.ThrowIfCancellationRequested();
                locks.Add(await Task.Run(() => PackageDownloadService.OpenAndRevalidateForInstall(package), cancellationToken));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await CodexSystemService.InstallPackageAsync(
                plan.MainPackage.FilePath, plan.Dependencies.Select(package => package.FilePath).ToArray());
        }
        finally
        {
            foreach (var packageLock in locks) packageLock.Dispose();
        }
    }

    public static async Task<LocalDependencySelection> ResolveLocalDependenciesAsync(
        DownloadedStorePackage main,
        IReadOnlyList<DownloadedStorePackage> available,
        CancellationToken cancellationToken)
    {
        var architecture = main.Identity.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase)
            ? HostArchitecture : main.Identity.Architecture;
        var selected = new Dictionary<string, DownloadedStorePackage>(StringComparer.OrdinalIgnoreCase);
        var handled = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var queue = new Queue<PackageDependencyRequirement>(main.Identity.Dependencies.Where(item => !item.IsOptional));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requirement = queue.Dequeue();
            var key = $"{requirement.Name}_{WindowsPackageIdentityService.GetPublisherId(requirement.Name, requirement.Publisher)}";
            if (handled.TryGetValue(key, out var version) && version >= requirement.MinimumVersion) continue;
            var package = available.Where(item => item.Identity.IsFramework &&
                    item.PackageFamilyName.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    item.Identity.Publisher.Equals(requirement.Publisher, StringComparison.OrdinalIgnoreCase) &&
                    item.Identity.ApplicationVersion >= requirement.MinimumVersion &&
                    IsDependencyCompatible(item.Identity.Architecture, architecture))
                .OrderByDescending(item => item.Identity.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.Identity.ApplicationVersion)
                .FirstOrDefault();
            if (package is not null)
            {
                selected[key] = package;
                handled[key] = package.Identity.ApplicationVersion;
                foreach (var dependency in package.Identity.Dependencies.Where(item => !item.IsOptional)) queue.Enqueue(dependency);
            }
            else if (await IsDependencyInstalledAsync(requirement, architecture, cancellationToken))
            {
                handled[key] = requirement.MinimumVersion;
            }
            else
            {
                handled[key] = requirement.MinimumVersion;
                missing.Add($"{requirement.Name} >= {requirement.MinimumVersion} ({architecture}/neutral)");
            }
        }

        if (new FileInfo(main.FilePath).Length + selected.Values.Sum(package => new FileInfo(package.FilePath).Length)
            > PackageDownloadService.MaxInstallationPlanBytes)
            throw new InvalidOperationException("主包与依赖包合计超过 4 GB 上限。");
        return new LocalDependencySelection(selected.Values.ToArray(), missing);
    }

    public static Task<DownloadedStorePackage> InspectLocalAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            var format = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".msix" => StorePackageFormat.Msix,
                ".appx" => StorePackageFormat.Appx,
                ".msixbundle" => StorePackageFormat.MsixBundle,
                ".appxbundle" => StorePackageFormat.AppxBundle,
                _ => throw new InvalidOperationException("请选择 MSIX、APPX 或对应 Bundle 安装包。"),
            };
            using var packageLock = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (packageLock.Length <= 0 || packageLock.Length > PackageDownloadService.MaxPackageBytes)
                throw new InvalidOperationException("本地安装包为空或超过 2 GB 上限。");
            WindowsTrustVerifier.EnsureValidSignature(fullPath);
            var identity = PackageArtifactInspector.Inspect(fullPath, format, HostArchitecture);
            var familyName = WindowsPackageIdentityService.GetPackageFamilyName(identity);
            if (identity.Name.Equals(CodexPackage.PackagePrefix, StringComparison.OrdinalIgnoreCase) &&
                !identity.Publisher.Equals(CodexPackage.Publisher, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("本地 Codex 安装包的发布者与预期不一致。");
            if (!PackageFamilyIdentity.TryParse(familyName, out var family))
                throw new InvalidOperationException("本地安装包的包族身份无效。");
            var candidate = new StorePackageCandidate(
                Path.GetFileName(fullPath), "", identity.Name, family.PublisherId, identity.ResourceId,
                identity.Version, identity.Architecture, format, StorePackageRole.Main, null, null);
            var hash = Convert.ToHexString(SHA256.HashData(packageLock));
            cancellationToken.ThrowIfCancellationRequested();
            return new DownloadedStorePackage(candidate, fullPath, identity, familyName, hash);
        }, cancellationToken);

    public static async Task<bool> IsDependencyInstalledAsync(
        PackageDependencyRequirement requirement, string architecture, CancellationToken cancellationToken)
    {
        var family = $"{requirement.Name}_{WindowsPackageIdentityService.GetPublisherId(requirement.Name, requirement.Publisher)}";
        return (await CodexSystemService.GetInstalledPackagesAsync(requirement.Name, cancellationToken)).Any(package =>
            package.PackageFamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) &&
            package.Publisher.Equals(requirement.Publisher, StringComparison.OrdinalIgnoreCase) &&
            package.Version >= requirement.MinimumVersion && IsDependencyCompatible(package.Architecture, architecture));
    }

    internal static bool IsDependencyCompatible(string architecture, string target) =>
        architecture.Equals(target, StringComparison.OrdinalIgnoreCase) ||
        architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase);
}

internal sealed record LocalDependencySelection(
    IReadOnlyList<DownloadedStorePackage> Packages,
    IReadOnlyList<string> Missing);
