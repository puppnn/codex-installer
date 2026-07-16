using System.Runtime.InteropServices;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PackageDependencyResolverTests
{
    [Fact]
    public void Resolve_PrefersExactArchitectureThenNewestVersion()
    {
        var requirement = Requirement();
        var resolved = Assert.Single(PackageDependencyResolver.Resolve(
            [requirement],
            [
                Candidate(new Version(2, 0, 0, 0), "x64", requirement.PublisherId!),
                Candidate(new Version(9, 0, 0, 0), "neutral", requirement.PublisherId!),
                Candidate(new Version(3, 0, 0, 0), "x64", requirement.PublisherId!),
            ],
            "x64"));

        Assert.Equal(new Version(3, 0, 0, 0), resolved.Candidate.Version);
        Assert.Equal("x64", resolved.Candidate.Architecture);
    }

    [Fact]
    public void Resolve_IgnoresWrongPublisherAndVersionsBelowMinimum()
    {
        var requirement = Requirement() with { MinimumVersion = new Version(2, 0, 0, 0) };
        var resolved = Assert.Single(PackageDependencyResolver.Resolve(
            [requirement],
            [
                Candidate(new Version(9, 0, 0, 0), "x64", "wrongpub12345"),
                Candidate(new Version(1, 0, 0, 0), "x64", requirement.PublisherId!),
                Candidate(new Version(2, 1, 0, 0), "neutral", requirement.PublisherId!),
            ],
            "x64"));

        Assert.Equal(new Version(2, 1, 0, 0), resolved.Candidate.Version);
        Assert.Equal(requirement.PublisherId, resolved.Candidate.PublisherId);
    }

    [Fact]
    public void Resolve_ThrowsWhenARequiredDependencyIsMissing()
    {
        var requirement = Requirement();

        Assert.Throws<InvalidOperationException>(() =>
            PackageDependencyResolver.Resolve([requirement], [], "arm64"));
    }

    [Theory]
    [InlineData("x64", Architecture.X64, true)]
    [InlineData("x86", Architecture.X64, true)]
    [InlineData("arm64", Architecture.X64, false)]
    [InlineData("x64", Architecture.Arm64, true)]
    [InlineData("arm64", Architecture.Arm64, true)]
    [InlineData("neutral", Architecture.X64, true)]
    public void IsCompatibleWithHost_UsesWindowsCompatibilityRules(
        string packageArchitecture,
        Architecture hostArchitecture,
        bool expected)
    {
        Assert.Equal(
            expected,
            PackageDependencyResolver.IsCompatibleWithHost(packageArchitecture, hostArchitecture));
    }

    private static PackageDependencyRequirement Requirement()
    {
        return new PackageDependencyRequirement(
            "Microsoft.VCLibs.140.00",
            "CN=Microsoft Corporation",
            new Version(1, 0, 0, 0),
            "8wekyb3d8bbwe");
    }

    private static StorePackageCandidate Candidate(
        Version version,
        string architecture,
        string publisherId)
    {
        var fileName = $"Microsoft.VCLibs.140.00_{version}_{architecture}__{publisherId}.appx";
        return new StorePackageCandidate(
            fileName,
            $"https://delivery.mp.microsoft.com/{fileName}",
            "Microsoft.VCLibs.140.00",
            publisherId,
            "",
            version,
            architecture,
            StorePackageFormat.Appx,
            StorePackageRole.Dependency,
            null,
            null);
    }
}
