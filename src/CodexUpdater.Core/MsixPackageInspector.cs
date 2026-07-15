using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace CodexUpdater.Core;

public static class MsixPackageInspector
{
    private const long MaxManifestBytes = 1024 * 1024;

    public static MsixIdentity ReadIdentity(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            throw new InvalidOperationException("MSIX 中缺少 AppxManifest.xml。");
        }

        if (manifestEntry.Length <= 0 || manifestEntry.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException("MSIX Manifest 大小异常。");
        }

        using var manifestStream = manifestEntry.Open();
        using var reader = XmlReader.Create(manifestStream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifestBytes,
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var identity = document.Root?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Identity")
            ?? throw new InvalidOperationException("MSIX Manifest 中缺少 Identity。");

        var name = RequiredAttribute(identity, "Name");
        var publisher = RequiredAttribute(identity, "Publisher");
        var architecture = RequiredAttribute(identity, "ProcessorArchitecture").ToLowerInvariant();
        var versionText = RequiredAttribute(identity, "Version");
        if (!Version.TryParse(versionText, out var version))
        {
            throw new InvalidOperationException("MSIX Manifest 版本号无效。");
        }

        return new MsixIdentity(name, publisher, architecture, version);
    }

    public static void ValidateCodexPackage(
        string packagePath,
        PackageCandidate candidate,
        string targetArchitecture)
    {
        var identity = ReadIdentity(packagePath);
        if (!identity.Name.Equals(CodexPackage.PackagePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"MSIX 包名不匹配：{identity.Name}。");
        }

        if (!identity.Publisher.Equals(CodexPackage.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MSIX 发布者不匹配：{identity.Publisher}。");
        }

        if (!identity.Architecture.Equals(targetArchitecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"MSIX 架构不匹配：{identity.Architecture}。");
        }

        if (identity.Version != candidate.Version)
        {
            throw new InvalidOperationException(
                $"MSIX 版本与候选文件名不匹配：Manifest {identity.Version}，文件名 {candidate.Version}。");
        }
    }

    private static string RequiredAttribute(XElement element, string attributeName)
    {
        return element.Attribute(attributeName)?.Value?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"MSIX Identity 缺少 {attributeName} 属性。");
    }
}

public sealed record MsixIdentity(
    string Name,
    string Publisher,
    string Architecture,
    Version Version);
