Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$vendorDirectory = Join-Path $PSScriptRoot "src\CodexUpdater.App\Vendor"
$runtimeInstaller = Join-Path $vendorDirectory "MicrosoftEdgeWebview2Setup.exe"
$runtimeInstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$runtimeInstallerSha256 = "F91077E2C116DCF6377E555D0D4A3A564D242351AD6718B6954658D4F74819C1"

New-Item -ItemType Directory -Path $vendorDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $runtimeInstaller)) {
    $temporaryInstaller = "$runtimeInstaller.download"
    Write-Host "Downloading Microsoft Edge WebView2 Evergreen Bootstrapper..."
    Invoke-WebRequest -Uri $runtimeInstallerUrl -OutFile $temporaryInstaller -MaximumRedirection 10
    Move-Item -LiteralPath $temporaryInstaller -Destination $runtimeInstaller -Force
}

$actualHash = (Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA256).Hash
if ($actualHash -ne $runtimeInstallerSha256) {
    throw "WebView2 Bootstrapper SHA-256 mismatch. Expected $runtimeInstallerSha256, got $actualHash."
}

$signature = Get-AuthenticodeSignature -LiteralPath $runtimeInstaller
if ($signature.Status -ne "Valid" -or
    $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne "Microsoft Corporation") {
    throw "WebView2 Bootstrapper does not have a valid Microsoft signature."
}

dotnet build .\CodexUpdater.sln
dotnet test .\tests\CodexUpdater.Tests\CodexUpdater.Tests.csproj

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
