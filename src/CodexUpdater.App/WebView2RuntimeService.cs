using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CodexUpdater.App;

internal static class WebView2RuntimeService
{
    private const string EmbeddedInstallerName = "MicrosoftEdgeWebview2Setup.exe";
    private const string EmbeddedInstallerSha256 =
        "F91077E2C116DCF6377E555D0D4A3A564D242351AD6718B6954658D4F74819C1";
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
            "当前电脑缺少 Microsoft Edge WebView2 Runtime，无法打开内置链接浏览器。\n\n是否现在通过微软官方安装程序联网安装？",
            "需要安装 WebView2 Runtime",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            throw new InvalidOperationException("缺少 Microsoft Edge WebView2 Runtime。请安装后重新运行本工具。");
        }

        var extractedInstaller = ExtractEmbeddedInstaller();
        try
        {
            using var installerLock = OpenAndVerifyEmbeddedInstaller(extractedInstaller.FilePath);
            var exitCode = await RunInstallerAsync(extractedInstaller.FilePath);
            if (exitCode != 0 && exitCode != 3010)
            {
                throw new InvalidOperationException($"WebView2 Runtime 安装失败，安装器退出代码：{exitCode}");
            }
        }
        finally
        {
            TryDeleteDirectory(extractedInstaller.DirectoryPath);
        }

        if (!await WaitForRuntimeAsync())
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

    private static ExtractedInstaller ExtractEmbeddedInstaller()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedInstallerName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"当前 exe 未内置 WebView2 Runtime 安装器。请从微软官方下载并安装：{DownloadPageUrl}");
        }

        var directory = Path.Combine(Path.GetTempPath(), "CodexUpdater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var installerPath = Path.Combine(directory, EmbeddedInstallerName);
        using var file = new FileStream(
            installerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.WriteThrough);
        stream.CopyTo(file);
        file.Flush(flushToDisk: true);
        return new ExtractedInstaller(directory, installerPath);
    }

    private static FileStream OpenAndVerifyEmbeddedInstaller(string installerPath)
    {
        var stream = new FileStream(
            installerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(EmbeddedInstallerSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("内置 WebView2 安装器哈希校验失败，已停止运行。");
            }

            WindowsTrustVerifier.EnsureValidSignature(installerPath);
            WindowsTrustVerifier.EnsurePeSigner(installerPath, "Microsoft Corporation");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<int> RunInstallerAsync(string installerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/silent /install",
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 WebView2 Runtime 安装器。");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<bool> WaitForRuntimeAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (IsRuntimeAvailable())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return IsRuntimeAvailable();
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch
        {
            // The temporary installer can be removed by the OS if setup still holds a file handle.
        }
    }

    private sealed record ExtractedInstaller(string DirectoryPath, string FilePath);
}
