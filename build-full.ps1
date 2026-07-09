Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$vendorDirectory = Join-Path $PSScriptRoot "src\CodexUpdater.App\Vendor"
$runtimeInstaller = Join-Path $vendorDirectory "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
$runtimeInstallerUrl = "https://go.microsoft.com/fwlink/?linkid=2124701"

New-Item -ItemType Directory -Path $vendorDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $runtimeInstaller)) {
    Write-Host "Downloading Microsoft Edge WebView2 Runtime x64 standalone installer..."
    Invoke-WebRequest -Uri $runtimeInstallerUrl -OutFile $runtimeInstaller -MaximumRedirection 10
}

dotnet build .\CodexUpdater.sln
dotnet run --project .\tests\CodexUpdater.Tests\CodexUpdater.Tests.csproj

if (Test-Path -LiteralPath .\publish-exe) {
    Remove-Item -LiteralPath .\publish-exe -Recurse -Force
}

if (-not (Test-Path -LiteralPath .\dist)) {
    New-Item -ItemType Directory -Path .\dist | Out-Null
}

dotnet publish .\src\CodexUpdater.App\CodexUpdater.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o .\publish-exe `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

Copy-Item -LiteralPath .\publish-exe\CodexUpdater.App.exe `
    -Destination .\dist\CodexInstaller-win-x64.exe `
    -Force

Get-Item -LiteralPath .\dist\CodexInstaller-win-x64.exe
Get-FileHash -LiteralPath .\dist\CodexInstaller-win-x64.exe -Algorithm SHA256
