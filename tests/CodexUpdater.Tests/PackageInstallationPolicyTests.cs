using System.Runtime.InteropServices;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PackageInstallationPolicyTests
{
    private static PackageArtifactIdentity Identity => new(
        "Contoso.App", "CN=Contoso", "", "x64", new Version(2, 0, 0, 0), StorePackageFormat.Msix, []);

    [Fact]
    public void Bundle_UsesPayloadVersionToBlockDowngrade()
    {
        var identity = Identity with
        {
            Format = StorePackageFormat.MsixBundle,
            Version = new Version(2026, 9, 30, 0),
            PayloadVersion = new Version(1, 0, 0, 0),
        };
        var reason = PackageInstallationPolicy.GetBlockReason(identity, Architecture.X64,
            new Version(10, 0, 26100), new Version(2, 0, 0, 0));
        Assert.Contains("阻止降级", reason);
    }

    [Fact]
    public void Installation_RejectsNewerWindowsRequirement()
    {
        var identity = Identity with { MinimumWindowsVersion = new Version(10, 0, 26100) };
        Assert.Contains("需要 Windows", PackageInstallationPolicy.GetBlockReason(identity, Architecture.X64, new Version(10, 0, 19045), null));
    }

    [Theory]
    [InlineData("x64", Architecture.X64, true)]
    [InlineData("arm64", Architecture.X64, false)]
    [InlineData("x86", Architecture.X64, true)]
    public void Installation_ChecksArchitecture(string architecture, Architecture host, bool allowed)
    {
        var reason = PackageInstallationPolicy.GetBlockReason(Identity with { Architecture = architecture }, host, new Version(10, 0, 26100), null);
        Assert.Equal(allowed, reason is null);
    }

    [Fact]
    public void Installation_RejectsResourceAndFrameworkMainPackages()
    {
        Assert.NotNull(PackageInstallationPolicy.GetBlockReason(Identity with { IsResourcePackage = true }, Architecture.X64, new Version(10, 0, 26100), null));
        Assert.NotNull(PackageInstallationPolicy.GetBlockReason(Identity with { IsFramework = true }, Architecture.X64, new Version(10, 0, 26100), null));
    }
}
