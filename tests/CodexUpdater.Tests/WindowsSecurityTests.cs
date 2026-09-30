using CodexUpdater.App;
using CodexUpdater.Core;
using System.Security.Cryptography;

namespace CodexUpdater.Tests;

public sealed class WindowsSecurityTests
{
    [Fact]
    public void EnsureValidSignature_AcceptsMicrosoftWebViewBootstrapperWhenPresent()
    {
        var bootstrapperPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "CodexUpdater.App",
            "Vendor",
            "MicrosoftEdgeWebview2Setup.exe"));
        if (!File.Exists(bootstrapperPath))
        {
            return;
        }

        WindowsTrustVerifier.EnsureValidSignature(bootstrapperPath);
        WindowsTrustVerifier.EnsurePeSigner(bootstrapperPath, "Microsoft Corporation");
    }

    [Fact]
    public void EnsureValidSignature_RejectsUnsignedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "not an executable");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                WindowsTrustVerifier.EnsureValidSignature(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PowerShellRunner_UsesEncodedSystemCommand()
    {
        var result = await PowerShellRunner.RunAsync("Write-Output 'codex-updater-test'");

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Contains("codex-updater-test", result.StandardOutput);
    }

    [Fact]
    public async Task CodexSystemService_CanQueryCurrentUserPackages()
    {
        var installed = await CodexSystemService.GetInstalledAsync();

        if (installed is not null)
        {
            Assert.Equal("OpenAI.Codex", installed.Name);
        }
    }

    [Fact]
    public async Task PowerShellRunner_PreservesChineseAndSuppressesProgressXml()
    {
        var result = await PowerShellRunner.RunAsync("Write-Progress -Activity '正在安装' -PercentComplete 50; Write-Output '中文路径和错误提示'");
        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal("中文路径和错误提示", result.StandardOutput);
        Assert.DoesNotContain("CLIXML", result.StandardError);
        Assert.DoesNotContain("<Objs", result.StandardError);
    }

    [Fact]
    public async Task PowerShellRunner_StopsOnNonTerminatingError()
    {
        var result = await PowerShellRunner.RunAsync("Write-Error '测试安装失败 0x80073D02'; Write-Output 'must-not-run'");
        Assert.False(result.Succeeded);
        Assert.Contains("测试安装失败", result.StandardError);
        Assert.DoesNotContain("must-not-run", result.StandardOutput);
        Assert.DoesNotContain("CLIXML", result.StandardError);
    }

    [Fact]
    public async Task LocalPackage_RejectsUnsignedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.msix");
        await File.WriteAllTextAsync(path, "unsigned fixture");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PackageInstallationService.InspectLocalAsync(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmbeddedBootstrapperPin_MatchesTheEmbeddedInstaller()
    {
        using var installer = typeof(PowerShellRunner).Assembly.GetManifestResourceStream("MicrosoftEdgeWebview2Setup.exe");
        if (installer is null) return;
        var actual = Convert.ToHexString(SHA256.HashData(installer));
        Assert.Equal(WebView2RuntimeService.ReadEmbeddedInstallerHash(), actual, ignoreCase: true);
    }

    [Fact]
    public void WindowsPackageIdentityService_ComputesKnownCodexFamilyName()
    {
        var identity = new PackageArtifactIdentity(
            CodexPackage.PackagePrefix,
            CodexPackage.Publisher,
            "",
            "x64",
            new Version(26, 707, 9981, 0),
            StorePackageFormat.Msix,
            []);

        Assert.Equal(
            "OpenAI.Codex_2p2nqsd0c76g0",
            WindowsPackageIdentityService.GetPackageFamilyName(identity));
        Assert.Equal(
            CodexPackage.PublisherId,
            WindowsPackageIdentityService.GetPublisherId(
                CodexPackage.PackagePrefix,
                CodexPackage.Publisher));
    }

    [Fact]
    public void BuildInstallCommand_EscapesMainAndDependencyPaths()
    {
        var command = CodexSystemService.BuildInstallCommand(
            @"C:\Packages\O'Brien\Main package.msix",
            [
                @"C:\Dependencies\First package.appx",
                @"C:\Dependencies\O'Brien.appx",
            ]);

        Assert.Equal(
            "Add-AppxPackage -Path 'C:\\Packages\\O''Brien\\Main package.msix' " +
            "-DependencyPath @('C:\\Dependencies\\First package.appx','C:\\Dependencies\\O''Brien.appx')",
            command);
    }

    [Fact]
    public void DownloadAttempts_PreferHttpsBeforeOriginalMicrosoftHttpLink()
    {
        const string original =
            "http://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/package.msix";

        var attempts = PackageDownloadService.BuildDownloadAttemptUrls(original);

        Assert.Equal(2, attempts.Count);
        Assert.StartsWith("https://", attempts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, attempts[1]);
    }

    [Fact]
    public void DownloadAttempts_NeverDowngradeAnOriginalHttpsLink()
    {
        const string original =
            "https://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/package.msix";

        Assert.Equal([original], PackageDownloadService.BuildDownloadAttemptUrls(original));
    }

    [Theory]
    [InlineData(
        "https://delivery.mp.microsoft.com/package.msix",
        "http://delivery.mp.microsoft.com/package.msix",
        false)]
    [InlineData(
        "http://delivery.mp.microsoft.com/package.msix",
        "http://delivery.mp.microsoft.com/package.msix",
        true)]
    [InlineData(
        "https://delivery.mp.microsoft.com/package.msix",
        "https://tlu.dl.delivery.mp.microsoft.com/package.msix",
        true)]
    [InlineData(
        "http://delivery.mp.microsoft.com/package.msix",
        "https://example.test/package.msix",
        false)]
    public void FinalDownloadUrl_EnforcesTheOriginalTransportPolicy(
        string originalUrl,
        string finalUrl,
        bool expected)
    {
        Assert.Equal(
            expected,
            PackageDownloadService.IsAllowedFinalDownloadUrl(originalUrl, finalUrl));
    }

    [Fact]
    public void ExceptionMessageFormatter_IncludesInnerTlsReason()
    {
        var exception = new HttpRequestException(
            "The SSL connection could not be established.",
            new System.Security.Authentication.AuthenticationException(
                "The remote certificate has a name mismatch."));

        var message = ExceptionMessageFormatter.Format(exception);

        Assert.Contains("SSL connection", message);
        Assert.Contains("name mismatch", message);
    }

    [Fact]
    public void ExistingPackageHash_RequiresExactFileContents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.msix");
        File.WriteAllText(path, "signed-package-placeholder");
        try
        {
            var expected = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)));
            Assert.True(PackageDownloadService.MatchesExpectedPageHash(path, expected));

            File.AppendAllText(path, "changed");
            Assert.False(PackageDownloadService.MatchesExpectedPageHash(path, expected));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
