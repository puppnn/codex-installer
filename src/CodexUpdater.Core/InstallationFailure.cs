namespace CodexUpdater.Core;

public sealed record InstallationFailure(string Summary, string Detail, bool IsPackageInUse)
{
    public static InstallationFailure FromOutput(string output)
    {
        var detail = PowerShellOutputFormatter.Clean(output);
        var summary = "安装未完成，请查看详细错误信息。";
        var inUse = detail.Contains("0x80073D02", StringComparison.OrdinalIgnoreCase);
        if (inUse)
        {
            summary = "应用仍在运行，Windows 无法替换占用的文件（0x80073D02）。请保存工作并退出相关应用后重试。";
        }
        else if (detail.Contains("0x80070490", StringComparison.OrdinalIgnoreCase))
        {
            summary = "Windows 包存储库注册失败：找不到元素（0x80070490）。请先重启 Windows 后重试；若仍失败，请根据下方部署日志排查系统注册状态。";
        }
        else if (detail.Contains("0x80073CF3", StringComparison.OrdinalIgnoreCase))
        {
            summary = "安装包依赖或兼容性检查失败（0x80073CF3）。请检查下方列出的依赖包、架构和 Windows 版本。";
        }
        else if (detail.Contains("0x80073D06", StringComparison.OrdinalIgnoreCase))
        {
            summary = "本机已安装更高版本（0x80073D06）。请使用相同或更新版本的安装包。";
        }
        else if (detail.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
        {
            summary = "Windows 拒绝访问（0x80070005）。请检查下载目录权限和系统安装策略。";
        }

        return new InstallationFailure(summary, detail, inUse);
    }
}
