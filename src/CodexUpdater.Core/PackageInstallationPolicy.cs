using System.Runtime.InteropServices;

namespace CodexUpdater.Core;

public static class PackageInstallationPolicy
{
    public static string? GetBlockReason(
        PackageArtifactIdentity identity,
        Architecture hostArchitecture,
        Version windowsVersion,
        Version? installedVersion)
    {
        if (identity.IsFramework || identity.IsResourcePackage || !string.IsNullOrEmpty(identity.ResourceId))
        {
            return "请选择主应用包；框架包和资源包不能单独作为主应用安装。";
        }

        if (!PackageDependencyResolver.IsCompatibleWithHost(identity.Architecture, hostArchitecture))
        {
            return $"安装包架构 {identity.Architecture} 与当前 Windows 架构 {hostArchitecture} 不兼容。";
        }

        if (identity.MinimumWindowsVersion is { } minimum && windowsVersion < minimum)
        {
            return $"安装包需要 Windows {minimum} 或更新版本，当前为 {windowsVersion}。";
        }

        if (installedVersion is not null && identity.ApplicationVersion < installedVersion)
        {
            return $"本机版本 {installedVersion} 高于安装包的应用版本 {identity.ApplicationVersion}，已阻止降级安装。";
        }

        return null;
    }
}
