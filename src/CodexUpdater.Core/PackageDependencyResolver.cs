namespace CodexUpdater.Core;

public sealed record ResolvedPackageDependency(
    PackageDependencyRequirement Requirement,
    StorePackageCandidate Candidate);

public static class PackageDependencyResolver
{
    public static IReadOnlyList<ResolvedPackageDependency> Resolve(
        IEnumerable<PackageDependencyRequirement> requirements,
        IEnumerable<StorePackageCandidate> candidates,
        string targetArchitecture)
    {
        var available = candidates
            .Where(candidate => candidate.Role == StorePackageRole.Dependency && !candidate.IsExpired)
            .ToArray();
        var resolved = new List<ResolvedPackageDependency>();

        foreach (var requirement in requirements.Where(requirement => !requirement.IsOptional).Distinct())
        {
            var candidate = available
                .Where(item => item.IdentityName.Equals(requirement.Name, StringComparison.OrdinalIgnoreCase) &&
                    (requirement.PublisherId is null ||
                     item.PublisherId.Equals(requirement.PublisherId, StringComparison.OrdinalIgnoreCase)) &&
                    item.Version >= requirement.MinimumVersion)
                .Select(item => new
                {
                    Candidate = item,
                    ArchitectureRank = DependencyArchitectureRank(item.Architecture, targetArchitecture),
                })
                .Where(item => item.ArchitectureRank < int.MaxValue)
                .OrderBy(item => item.ArchitectureRank)
                .ThenByDescending(item => item.Candidate.Version)
                .Select(item => item.Candidate)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"没有找到依赖 {requirement.Name} >= {requirement.MinimumVersion} ({targetArchitecture}/neutral)。");

            resolved.Add(new ResolvedPackageDependency(requirement, candidate));
        }

        return resolved;
    }

    public static bool IsCompatibleWithHost(string architecture, System.Runtime.InteropServices.Architecture host)
    {
        if (architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase)) return true;
        return host switch
        {
            System.Runtime.InteropServices.Architecture.X64 =>
                architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                architecture.Equals("x86", StringComparison.OrdinalIgnoreCase),
            System.Runtime.InteropServices.Architecture.Arm64 =>
                architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ||
                architecture.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                architecture.Equals("x86", StringComparison.OrdinalIgnoreCase),
            System.Runtime.InteropServices.Architecture.X86 =>
                architecture.Equals("x86", StringComparison.OrdinalIgnoreCase),
            System.Runtime.InteropServices.Architecture.Arm =>
                architecture.Equals("arm", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static int DependencyArchitectureRank(string candidate, string target)
    {
        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.Equals("neutral", StringComparison.OrdinalIgnoreCase)) return 1;
        return int.MaxValue;
    }
}

public sealed record DownloadedStorePackage(
    StorePackageCandidate Candidate,
    string FilePath,
    PackageArtifactIdentity Identity,
    string PackageFamilyName,
    string Sha256);

public sealed record PackageInstallationPlan(
    StoreProductMetadata Product,
    DownloadedStorePackage MainPackage,
    IReadOnlyList<DownloadedStorePackage> Dependencies);
