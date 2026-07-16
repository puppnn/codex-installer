using System.IO.Compression;
using System.Security;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PackageArtifactInspectorTests
{
    private const string Name = "Contoso.App";
    private const string Publisher = "CN=Contoso Software, O=Contoso Corporation, C=US";

    [Fact]
    public void Inspect_ReadsSinglePackageIdentityAndDependencies()
    {
        var path = WriteTemporaryPackage(CreatePackage("x64", "Microsoft.VCLibs.140.00"), ".msix");
        try
        {
            var identity = PackageArtifactInspector.Inspect(path, StorePackageFormat.Msix, "x64");

            Assert.Equal(Name, identity.Name);
            Assert.Equal(Publisher, identity.Publisher);
            Assert.Equal("x64", identity.Architecture);
            var dependency = Assert.Single(identity.Dependencies);
            Assert.Equal("Microsoft.VCLibs.140.00", dependency.Name);
            Assert.Equal(new Version(14, 0, 0, 0), dependency.MinimumVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InspectBundle_SelectsTargetPayloadAndItsDependencies()
    {
        var path = WriteTemporaryPackage(CreateBundle(), ".msixbundle");
        try
        {
            var identity = PackageArtifactInspector.Inspect(path, StorePackageFormat.MsixBundle, "arm64");

            Assert.Equal(Name, identity.Name);
            Assert.Equal("arm64", identity.Architecture);
            Assert.Equal("Arm.Framework", Assert.Single(identity.Dependencies).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InspectBundle_RejectsMismatchedPayloadIdentity()
    {
        var path = WriteTemporaryPackage(CreateBundle(payloadName: "Other.App"), ".msixbundle");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                PackageArtifactInspector.Inspect(path, StorePackageFormat.MsixBundle, "arm64"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateBundle(string payloadName = Name)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "x64.msix", CreatePackage("x64", "X64.Framework", payloadName));
            WriteEntry(archive, "arm64.msix", CreatePackage("arm64", "Arm.Framework", payloadName));
            WriteEntry(archive, "AppxMetadata/AppxBundleManifest.xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
                  <Identity Name="{Name}" Publisher="{Escape(Publisher)}" Version="2.0.0.0" />
                  <Packages>
                    <Package Type="application" Architecture="x64" FileName="x64.msix" />
                    <Package Type="application" Architecture="arm64" FileName="arm64.msix" />
                  </Packages>
                </Bundle>
                """);
        }

        return output.ToArray();
    }

    private static byte[] CreatePackage(
        string architecture,
        string dependencyName,
        string packageName = Name)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "AppxManifest.xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="{packageName}"
                            Publisher="{Escape(Publisher)}"
                            Version="2.0.0.0"
                            ProcessorArchitecture="{architecture}" />
                  <Dependencies>
                    <PackageDependency Name="{dependencyName}"
                                       Publisher="CN=Dependency Publisher"
                                       MinVersion="14.0.0.0" />
                  </Dependencies>
                </Package>
                """);
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string WriteTemporaryPackage(byte[] content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Escape(string value) => SecurityElement.Escape(value)!;
}
