using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CodexUpdater.App;

internal static class WebView2RuntimeService
{
    private const string EmbeddedInstallerName = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    private const string DownloadPageUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    public static async Task EnsureInstalledAsync(Window owner)
    {
        if (IsRuntimeAvailable())
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            "当前电脑缺少 Microsoft Edge WebView2 Runtime，无法打开内置链接浏览器。\n\n是否现在安装内置的 WebView2 Runtime？安装时可能会弹出管理员权限确认。",
            "需要安装 WebView2 Runtime",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            throw new InvalidOperationException("缺少 Microsoft Edge WebView2 Runtime。请安装后重新运行本工具。");
        }

        var installerPath = ExtractEmbeddedInstaller();
        var exitCode = await RunInstallerAsync(installerPath);
        if (exitCode != 0 && exitCode != 3010)
        {
            throw new InvalidOperationException($"WebView2 Runtime 安装失败，安装器退出代码：{exitCode}");
        }

        if (!IsRuntimeAvailable())
        {
            throw new InvalidOperationException(
                $"WebView2 Runtime 安装后仍不可用。请手动从微软官方下载并安装：{DownloadPageUrl}");
        }

        System.Windows.MessageBox.Show(
            owner,
            "WebView2 Runtime 安装完成，可以继续检查更新。",
            "安装完成",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string ExtractEmbeddedInstaller()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedInstallerName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"当前 exe 未内置 WebView2 Runtime 安装器。请从微软官方下载并安装：{DownloadPageUrl}");
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUpdater",
            "Runtime");
        Directory.CreateDirectory(directory);

        var installerPath = Path.Combine(directory, EmbeddedInstallerName);
        using var file = File.Create(installerPath);
        stream.CopyTo(file);
        return installerPath;
    }

    private static async Task<int> RunInstallerAsync(string installerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/silent /install",
            UseShellExecute = true,
        };

        if (!Elevation.IsAdministrator())
        {
            startInfo.Verb = "runas";
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 WebView2 Runtime 安装器。");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
