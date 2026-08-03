# Codex Installer Lite

`CodexInstaller-lite-win-x64.exe` 是随源码提供的轻量单文件版本。

运行要求：

- Windows 10/11 x64
- [Microsoft .NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)
- 网络连接

轻量版不内置 .NET/WPF 运行时，因此体积明显小于 Release 中的完整独立版。它仍内置经过固定 SHA-256 和微软签名校验的 WebView2 Evergreen Bootstrapper；电脑缺少 WebView2 Runtime 时，程序会在用户确认后运行该引导程序。

可使用 PowerShell 核对文件哈希：

```powershell
Get-FileHash .\CodexInstaller-lite-win-x64.exe -Algorithm SHA256
```

期望值记录在 `CodexInstaller-lite-win-x64.sha256`。

如果电脑没有 .NET 10 Desktop Runtime，或者希望直接运行而不安装 .NET，请使用 GitHub Releases 中的完整独立版 `CodexInstaller-win-x64.exe`。
