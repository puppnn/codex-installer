using System.IO.Compression;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class MsixPackageInspectorTests
{
    [Fact]
    public void ValidateCodexPackage_AcceptsExpectedIdentity()
    {
        var path = CreateMsix(CodexPackage.Publisher);
        var candidate = Candidate();
        try
        {
            MsixPackageInspector.ValidateCodexPackage(path, candidate, "x64");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateCodexPackage_RejectsUnexpectedPublisher()
    {
        var path = CreateMsix("CN=Unexpected Publisher");
        var candidate = Candidate();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                MsixPackageInspector.ValidateCodexPackage(path, candidate, "x64"));
            Assert.Contains("发布者不匹配", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PackageCandidate Candidate()
    {
        return new PackageCandidate(
            "OpenAI.Codex_26.707.9981.0_x64__2p2nqsd0c76g0.msix",
            "https://tlu.dl.delivery.mp.microsoft.com/package.msix",
            new Version(26, 707, 9981, 0),
            "x64");
    }

    private static string CreateMsix(string publisher)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.msix");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("AppxManifest.xml");
        using var writer = new StreamWriter(manifest.Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="OpenAI.Codex"
                        Publisher="{publisher}"
                        Version="26.707.9981.0"
                        ProcessorArchitecture="x64" />
            </Package>
            """);
        return path;
    }
}
