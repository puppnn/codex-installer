using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class StorePackageCandidateTests
{
    private const string Family = "Contoso.App_abcd1234efghi";
    private const string Cdn = "https://tlu.dl.delivery.mp.microsoft.com/files/";

    [Theory]
    [InlineData("Contoso.App_1.2.3.4_x64__abcd1234efghi.msix", StorePackageFormat.Msix)]
    [InlineData("Contoso.App_1.2.3.4_x64__abcd1234efghi.appx", StorePackageFormat.Appx)]
    [InlineData("Contoso.App_1.2.3.4_neutral_~_abcd1234efghi.msixbundle", StorePackageFormat.MsixBundle)]
    [InlineData("Contoso.App_1.2.3.4_neutral_~_abcd1234efghi.appxbundle", StorePackageFormat.AppxBundle)]
    public void Parse_SupportsAllPackageFormats(string fileName, StorePackageFormat expectedFormat)
    {
        var candidate = Assert.Single(StorePackageCandidateParser.Parse(
            [Row(fileName)],
            [Family]));

        Assert.Equal(StorePackageRole.Main, candidate.Role);
        Assert.Equal(expectedFormat, candidate.Format);
        Assert.Equal(new Version(1, 2, 3, 4), candidate.Version);
        Assert.StartsWith("http://", candidate.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ClassifiesDependenciesAndResources()
    {
        var candidates = StorePackageCandidateParser.Parse(
            [
                Row("Microsoft.VCLibs.140.00_14.0.1.0_x64__8wekyb3d8bbwe.appx"),
                Row("Contoso.App_1.2.3.4_neutral_en-us_abcd1234efghi.msix"),
            ],
            [Family]);

        Assert.Contains(candidates, candidate => candidate.Role == StorePackageRole.Dependency);
        Assert.Contains(candidates, candidate => candidate.Role == StorePackageRole.Resource);
        Assert.DoesNotContain(candidates, candidate => candidate.Role == StorePackageRole.Main);
    }

    [Theory]
    [InlineData("Contoso.App_1.2.3.4_x64__abcd1234efghi.BlockMap")]
    [InlineData("Contoso.App_1.2.3.4_x64__abcd1234efghi.eappx")]
    [InlineData("Contoso.App_1.2.3.4_x64__abcd1234efghi.appxsym")]
    public void Parse_RejectsNonInstallableArtifacts(string fileName)
    {
        Assert.Empty(StorePackageCandidateParser.Parse([Row(fileName)], [Family]));
    }

    [Fact]
    public void Parse_RejectsNonMicrosoftLinksEvenWhenTextLooksValid()
    {
        const string fileName = "Contoso.App_1.2.3.4_x64__abcd1234efghi.msix";
        var row = new PackageLinkRow($"https://example.test/{fileName}", fileName, null, null);

        Assert.Empty(StorePackageCandidateParser.Parse([row], [Family]));
    }

    private static PackageLinkRow Row(string fileName)
    {
        return new PackageLinkRow(
            $"{Cdn.Replace("https://", "http://", StringComparison.Ordinal)}{Uri.EscapeDataString(fileName)}",
            fileName,
            "2099-07-15 12:00:00Z",
            "ABCDEF0123456789");
    }
}
