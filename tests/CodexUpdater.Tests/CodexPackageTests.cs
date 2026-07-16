using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class CodexPackageTests
{
    private const string MicrosoftCdn = "https://tlu.dl.delivery.mp.microsoft.com";

    [Fact]
    public void SelectNewest_ChoosesNewestRequestedArchitecture()
    {
        var links = new[]
        {
            Link("OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix"),
            Link("OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.BlockMap"),
            Link("OpenAI.Codex_26.616.9593.0_x64__2p2nqsd0c76g0.msix"),
            Link("OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix"),
        };

        var candidate = CodexPackage.SelectNewest(links, "x64");

        Assert.NotNull(candidate);
        Assert.Equal("x64", candidate.Architecture);
        Assert.Equal(new Version(26, 616, 10790, 0), candidate.Version);
        Assert.Equal("OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix", candidate.FileName);
    }

    [Fact]
    public void SelectNewest_RejectsUntrustedHrefEvenWhenTextLooksValid()
    {
        const string fileName = "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix";
        var links = new[] { new PackageLink($"https://example.test/{fileName}", fileName) };

        Assert.Null(CodexPackage.SelectNewest(links));
    }

    [Theory]
    [InlineData("https://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/123", true)]
    [InlineData("https://delivery.mp.microsoft.com/package.msix", true)]
    [InlineData("http://tlu.dl.delivery.mp.microsoft.com/package.msix", true)]
    [InlineData("http://tlu.dl.delivery.mp.microsoft.com:8080/package.msix", false)]
    [InlineData("https://tlu.dl.delivery.mp.microsoft.com:8443/package.msix", false)]
    [InlineData("https://delivery.mp.microsoft.com.example.test/package.msix", false)]
    [InlineData("https://example.test/package.msix", false)]
    public void IsTrustedDownloadUrl_RestrictsMicrosoftHttpsHosts(string url, bool expected)
    {
        Assert.Equal(expected, CodexPackage.IsTrustedDownloadUrl(url));
    }

    [Fact]
    public void SelectNewest_PreservesTrustedMicrosoftHttpLinkForVerifiedFallback()
    {
        const string fileName = "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix";
        var candidate = CodexPackage.SelectNewest(new[]
        {
            new PackageLink($"http://tlu.dl.delivery.mp.microsoft.com/{fileName}", fileName),
        });

        Assert.NotNull(candidate);
        Assert.StartsWith("http://", candidate.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsExpectedFileName_SeparatesArchitecturesAndRejectsBlockMap()
    {
        Assert.False(CodexPackage.IsExpectedFileName(
            "OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix"));
        Assert.True(CodexPackage.IsExpectedFileName(
            "OpenAI.Codex_26.616.10790.0_arm64__2p2nqsd0c76g0.msix",
            "arm64"));
        Assert.False(CodexPackage.IsExpectedFileName(
            "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.BlockMap"));
    }

    [Fact]
    public void SelectNewest_CarriesAValidRgAdguardPageHash()
    {
        const string fileName = "OpenAI.Codex_26.616.10790.0_x64__2p2nqsd0c76g0.msix";
        const string sha1 = "0123456789ABCDEF0123456789ABCDEF01234567";
        var candidate = CodexPackage.SelectNewest(
            [new PackageLink($"{MicrosoftCdn}/{fileName}", fileName, sha1)]);

        Assert.NotNull(candidate);
        Assert.Equal(sha1, candidate.PageHash);
    }

    private static PackageLink Link(string fileName)
    {
        return new PackageLink($"{MicrosoftCdn}/{fileName}", fileName);
    }
}
