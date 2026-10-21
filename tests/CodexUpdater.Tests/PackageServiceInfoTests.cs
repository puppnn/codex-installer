using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PackageServiceInfoTests
{
    private const string InstallLocation = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.928.1915.0_x64__2p2nqsd0c76g0";

    [Theory]
    [InlineData("\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.928.1915.0_x64__2p2nqsd0c76g0\\app\\resources\\codex-windows-sandbox-service.exe\"", true)]
    [InlineData("\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.928.1915.0_x64__2p2nqsd0c76g0\\app\\svc.exe\" --service", true)]
    [InlineData("c:\\program files\\windowsapps\\openai.codex_26.928.1915.0_x64__2p2nqsd0c76g0\\svc.exe -k", true)]
    [InlineData("\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.928.1915.0_x64__2p2nqsd0c76g0X\\svc.exe\"", false)]
    [InlineData("\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\\svc.exe\"", false)]
    [InlineData("C:\\Windows\\system32\\svchost.exe -k netsvcs -p \"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.928.1915.0_x64__2p2nqsd0c76g0\\x\"", false)]
    [InlineData("", false)]
    public void IsInsidePackage_MatchesOnlyExecutablesUnderTheInstallDirectory(string pathName, bool expected) =>
        Assert.Equal(expected, PackageServiceInfo.IsInsidePackage(pathName, InstallLocation));

    [Fact]
    public void IsInsidePackage_AcceptsInstallLocationWithTrailingSeparator() =>
        Assert.True(PackageServiceInfo.IsInsidePackage($"\"{InstallLocation}\\svc.exe\"", InstallLocation + "\\"));

    [Theory]
    [InlineData("Stopped", true)]
    [InlineData("Running", false)]
    [InlineData("Stop Pending", false)]
    public void IsStopped_TreatsPendingStatesAsActive(string state, bool expected) =>
        Assert.Equal(expected, new PackageServiceInfo("svc", "", state, "").IsStopped);

    [Fact]
    public void Label_IncludesServiceNameWhenDisplayNameDiffers()
    {
        Assert.Equal("ChatGPT（CodexSandboxService.OpenAI.Codex）",
            new PackageServiceInfo("CodexSandboxService.OpenAI.Codex", "ChatGPT", "Running", "").Label);
        Assert.Equal("svc", new PackageServiceInfo("svc", "", "Running", "").Label);
    }
}
