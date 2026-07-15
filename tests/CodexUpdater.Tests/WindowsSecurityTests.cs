using CodexUpdater.App;

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
}
