using System.Runtime.InteropServices;
using System.Text;
using CodexUpdater.Core;

namespace CodexUpdater.App;

internal static class WindowsPackageIdentityService
{
    private const int ErrorInsufficientBuffer = 122;

    public static string GetPackageFamilyName(PackageArtifactIdentity identity)
    {
        var packageId = new PackageId
        {
            ProcessorArchitecture = ArchitectureValue(identity.Architecture),
            Version = VersionValue(identity.Version),
            Name = identity.Name,
            Publisher = identity.Publisher,
            ResourceId = identity.ResourceId,
        };

        uint length = 0;
        var result = PackageFamilyNameFromId(ref packageId, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new InvalidOperationException($"无法计算 Package Family Name（错误 {result}）。");
        }

        var familyName = new StringBuilder((int)length);
        result = PackageFamilyNameFromId(ref packageId, ref length, familyName);
        if (result != 0)
        {
            throw new InvalidOperationException($"无法计算 Package Family Name（错误 {result}）。");
        }

        return familyName.ToString();
    }

    public static string GetPublisherId(string identityName, string publisher)
    {
        var familyName = GetPackageFamilyName(new PackageArtifactIdentity(
            identityName,
            publisher,
            "",
            "neutral",
            new Version(1, 0, 0, 0),
            StorePackageFormat.Msix,
            []));
        return PackageFamilyIdentity.TryParse(familyName, out var family)
            ? family.PublisherId
            : throw new InvalidOperationException($"无法解析 Package Family Name：{familyName}。");
    }

    private static uint ArchitectureValue(string architecture)
    {
        return architecture.ToLowerInvariant() switch
        {
            "x86" => 0,
            "arm" => 5,
            "x64" => 9,
            "neutral" => 11,
            "arm64" => 12,
            _ => throw new InvalidOperationException($"不支持的包架构：{architecture}。"),
        };
    }

    private static ulong VersionValue(Version version)
    {
        var parts = new[] { version.Major, version.Minor, version.Build, version.Revision };
        if (parts.Any(part => part is < 0 or > ushort.MaxValue))
        {
            throw new InvalidOperationException($"包版本超出 Windows 支持范围：{version}。");
        }

        return ((ulong)parts[0] << 48) |
            ((ulong)parts[1] << 32) |
            ((ulong)parts[2] << 16) |
            (uint)parts[3];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromId(
        ref PackageId packageId,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PackageId
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Publisher;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ResourceId;

        public IntPtr PublisherId;
    }
}
