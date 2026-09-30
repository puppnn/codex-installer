using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace CodexUpdater.Core;

public sealed record PackageDependencyRequirement(
    string Name,
    string Publisher,
    Version MinimumVersion,
    string? PublisherId = null,
    bool IsOptional = false);

public sealed record PackageArtifactIdentity(
    string Name,
    string Publisher,
    string ResourceId,
    string Architecture,
    Version Version,
    StorePackageFormat Format,
    IReadOnlyList<PackageDependencyRequirement> Dependencies)
{
    public Version? PayloadVersion { get; init; }
    public Version ApplicationVersion => PayloadVersion ?? Version;
    public Version? MinimumWindowsVersion { get; init; }
    public bool IsFramework { get; init; }
    public bool IsResourcePackage { get; init; }
}

public static class PackageArtifactInspector
{
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxNestedPackageBytes = 2L * 1024 * 1024 * 1024;

    public static PackageArtifactIdentity Inspect(
        string packagePath,
        StorePackageFormat format,
        string targetArchitecture)
    {
        return format is StorePackageFormat.MsixBundle or StorePackageFormat.AppxBundle
            ? InspectBundle(packagePath, format, targetArchitecture)
            : InspectSinglePackage(packagePath, format);
    }

    private static PackageArtifactIdentity InspectSinglePackage(
        string packagePath,
        StorePackageFormat format)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifest = LoadManifest(archive, "AppxManifest.xml");
        var identity = RequiredElement(manifest.Root, "Identity");
        return new PackageArtifactIdentity(
            RequiredAttribute(identity, "Name"),
            RequiredAttribute(identity, "Publisher"),
            OptionalAttribute(identity, "ResourceId") ?? "",
            RequiredAttribute(identity, "ProcessorArchitecture").ToLowerInvariant(),
            ParseVersion(RequiredAttribute(identity, "Version"), "MSIX Manifest"),
            format,
            ReadDependencies(manifest))
        {
            MinimumWindowsVersion = ReadMinimumWindowsVersion(manifest),
            IsFramework = ReadBooleanProperty(manifest, "Framework"),
            IsResourcePackage = ReadBooleanProperty(manifest, "ResourcePackage"),
        };
    }

    private static PackageArtifactIdentity InspectBundle(
        string packagePath,
        StorePackageFormat format,
        string targetArchitecture)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var bundleManifest = LoadManifest(archive, "AppxMetadata/AppxBundleManifest.xml");
        var bundleIdentity = RequiredElement(bundleManifest.Root, "Identity");
        var payload = bundleManifest
            .Descendants()
            .Where(element => element.Name.LocalName == "Package")
            .Select(element => new BundlePayload(
                RequiredAttribute(element, "FileName"),
                (OptionalAttribute(element, "Architecture") ?? "neutral").ToLowerInvariant(),
                OptionalAttribute(element, "Type") ?? "application",
                OptionalAttribute(element, "ResourceId") ?? ""))
            .Where(item => item.Type.Equals("application", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(item.ResourceId))
            .OrderBy(item => ArchitectureRank(item.Architecture, targetArchitecture))
            .FirstOrDefault(item => ArchitectureRank(item.Architecture, targetArchitecture) < int.MaxValue)
            ?? throw new InvalidOperationException($"Bundle 中没有兼容 {targetArchitecture} 的应用包。");

        var payloadEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals(payload.FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Bundle 中缺少应用 payload：{payload.FileName}。");
        if (payloadEntry.Length <= 0 || payloadEntry.Length > MaxNestedPackageBytes)
        {
            throw new InvalidOperationException("Bundle 内部应用包大小异常。");
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"CodexUpdater-{Guid.NewGuid():N}{Path.GetExtension(payload.FileName)}");
        try
        {
            using (var source = payloadEntry.Open())
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                FileOptions.SequentialScan))
            {
                source.CopyTo(destination);
            }

            var payloadFormat = payload.FileName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                ? StorePackageFormat.Msix
                : payload.FileName.EndsWith(".appx", StringComparison.OrdinalIgnoreCase)
                    ? StorePackageFormat.Appx
                    : throw new InvalidOperationException($"Bundle payload 格式不受支持：{payload.FileName}。");
            var payloadIdentity = InspectSinglePackage(temporaryPath, payloadFormat);
            var bundleName = RequiredAttribute(bundleIdentity, "Name");
            var bundlePublisher = RequiredAttribute(bundleIdentity, "Publisher");
            if (!payloadIdentity.Name.Equals(bundleName, StringComparison.OrdinalIgnoreCase) ||
                !payloadIdentity.Publisher.Equals(bundlePublisher, StringComparison.OrdinalIgnoreCase) ||
                !payloadIdentity.Architecture.Equals(payload.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Bundle 与应用 payload 的身份不一致。");
            }

            return new PackageArtifactIdentity(
                bundleName,
                bundlePublisher,
                OptionalAttribute(bundleIdentity, "ResourceId") ?? "",
                payloadIdentity.Architecture,
                ParseVersion(RequiredAttribute(bundleIdentity, "Version"), "Bundle Manifest"),
                format,
                payloadIdentity.Dependencies)
            {
                PayloadVersion = payloadIdentity.ApplicationVersion,
                MinimumWindowsVersion = payloadIdentity.MinimumWindowsVersion,
                IsFramework = payloadIdentity.IsFramework,
                IsResourcePackage = payloadIdentity.IsResourcePackage,
            };
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original inspection error.
            }
        }
    }

    private static IReadOnlyList<PackageDependencyRequirement> ReadDependencies(XDocument manifest)
    {
        return manifest
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageDependency")
            .Select(element => new PackageDependencyRequirement(
                RequiredAttribute(element, "Name"),
                RequiredAttribute(element, "Publisher"),
                ParseVersion(OptionalAttribute(element, "MinVersion") ?? "0.0.0.0", "PackageDependency"),
                IsOptional: string.Equals(
                    (string?)element.Attribute(XName.Get("Optional", "http://schemas.microsoft.com/appx/manifest/uap/windows10/6")),
                    "true",
                    StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();
    }

    private static Version? ReadMinimumWindowsVersion(XDocument manifest)
    {
        return manifest.Descendants()
            .Where(element => element.Name.LocalName == "TargetDeviceFamily" &&
                (OptionalAttribute(element, "Name") is "Windows.Desktop" or "Windows.Universal"))
            .Select(element => ParseVersion(RequiredAttribute(element, "MinVersion"), "TargetDeviceFamily"))
            .OrderByDescending(version => version)
            .FirstOrDefault();
    }

    private static bool ReadBooleanProperty(XDocument manifest, string name) => manifest.Root?.Elements()
        .Where(element => element.Name.LocalName == "Properties")
        .Elements()
        .Any(element => element.Name.LocalName == name &&
            element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) == true;

    private static XDocument LoadManifest(ZipArchive archive, string path)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"安装包中缺少 {path}。");
        if (entry.Length <= 0 || entry.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException($"{path} 大小异常。");
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifestBytes,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static XElement RequiredElement(XElement? root, string localName)
    {
        return root?.Elements().FirstOrDefault(element => element.Name.LocalName == localName)
            ?? throw new InvalidOperationException($"Manifest 中缺少 {localName}。");
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        return OptionalAttribute(element, name)
            ?? throw new InvalidOperationException($"Manifest 缺少 {name} 属性。");
    }

    private static string? OptionalAttribute(XElement element, string name)
    {
        return element.Attribute(name)?.Value?.Trim() is { Length: > 0 } value ? value : null;
    }

    private static Version ParseVersion(string value, string source)
    {
        return Version.TryParse(value, out var version)
            ? version
            : throw new InvalidOperationException($"{source} 版本号无效：{value}。");
    }

    private static int ArchitectureRank(string candidate, string target)
    {
        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.Equals("neutral", StringComparison.OrdinalIgnoreCase)) return 1;
        if (target.Equals("arm64", StringComparison.OrdinalIgnoreCase) &&
            candidate.Equals("x64", StringComparison.OrdinalIgnoreCase)) return 2;
        if ((target.Equals("arm64", StringComparison.OrdinalIgnoreCase) ||
             target.Equals("x64", StringComparison.OrdinalIgnoreCase)) &&
            candidate.Equals("x86", StringComparison.OrdinalIgnoreCase)) return 3;
        return int.MaxValue;
    }

    private sealed record BundlePayload(
        string FileName,
        string Architecture,
        string Type,
        string ResourceId);
}
