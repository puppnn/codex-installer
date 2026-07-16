using System.Text.Json;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class StoreProductMetadataTests
{
    [Fact]
    public void Parse_ReadsStrictWindowsUpdateMetadata()
    {
        var metadata = StoreProductMetadataParser.Parse(Html(new
        {
            title = "Codex",
            installer = new { id = "9PLM9XGG6VKS", type = "WindowsUpdate" },
            allowedPlatforms = new[] { "Windows.Desktop", "Windows.Xbox" },
            packageFamilyNames = new[]
            {
                "OpenAI.Codex_2p2nqsd0c76g0",
                "OpenAI.Codex_2p2nqsd0c76g0",
            },
            productType = "Application",
            price = 0,
        }), "9PLM9XGG6VKS");

        Assert.Equal("Codex", metadata.DisplayName);
        Assert.Equal("WindowsUpdate", metadata.InstallerType);
        Assert.Single(metadata.PackageFamilyNames);
        Assert.True(metadata.IsFree);
    }

    [Theory]
    [InlineData("WRONGPRODUCT", "WindowsUpdate", true, true)]
    [InlineData("9PLM9XGG6VKS", "Exe", true, true)]
    [InlineData("9PLM9XGG6VKS", "WindowsUpdate", false, true)]
    [InlineData("9PLM9XGG6VKS", "WindowsUpdate", true, false)]
    public void Parse_RejectsUnverifiedMetadata(
        string actualProductId,
        string installerType,
        bool desktop,
        bool validFamily)
    {
        var platforms = desktop ? new[] { "Windows.Desktop" } : new[] { "Windows.Xbox" };
        var families = validFamily
            ? new[] { "OpenAI.Codex_2p2nqsd0c76g0" }
            : new[] { "malformed_family" };
        var html = Html(new
        {
            installer = new { id = actualProductId, type = installerType },
            allowedPlatforms = platforms,
            packageFamilyNames = families,
        });

        Assert.Throws<InvalidOperationException>(() =>
            StoreProductMetadataParser.Parse(html, "9PLM9XGG6VKS"));
    }

    [Fact]
    public void Parse_RejectsMissingPageMetadata()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StoreProductMetadataParser.Parse("<html></html>", "9PLM9XGG6VKS"));
    }

    private static string Html(object metadata)
    {
        return $"<html><script>window.pageMetadata = {JsonSerializer.Serialize(metadata)};</script></html>";
    }
}
