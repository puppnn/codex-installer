param(
    [switch]$SkipValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$vendorDirectory = Join-Path $PSScriptRoot "src\CodexUpdater.App\Vendor"
$runtimeInstaller = Join-Path $vendorDirectory "MicrosoftEdgeWebview2Setup.exe"
$runtimeInstallerUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$runtimeInstallerSha256 = "F91077E2C116DCF6377E555D0D4A3A564D242351AD6718B6954658D4F74819C1"
$publishDirectory = Join-Path $PSScriptRoot "publish-lite"
$portableDirectory = Join-Path $PSScriptRoot "portable-lite"
$portableExecutable = Join-Path $portableDirectory "CodexInstaller-lite-win-x64.exe"
$checksumFile = Join-Path $portableDirectory "CodexInstaller-lite-win-x64.sha256"
$maximumPortableSizeBytes = 10 * 1024 * 1024

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

if (-not $SkipValidation) {
    dotnet restore .\CodexUpdater.sln /p:NuGetAudit=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    dotnet format .\CodexUpdater.sln --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet format failed with exit code $LASTEXITCODE."
    }

    dotnet build .\CodexUpdater.sln -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    dotnet test .\tests\CodexUpdater.Tests\CodexUpdater.Tests.csproj `
        -c Release `
        --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

dotnet restore .\src\CodexUpdater.App\CodexUpdater.App.csproj `
    -r win-x64 `
    /p:NuGetAudit=true
if ($LASTEXITCODE -ne 0) {
    throw "Runtime-specific dotnet restore failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null

dotnet publish .\src\CodexUpdater.App\CodexUpdater.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    --no-restore `
    -o $publishDirectory `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:SelfContained=false `
    /p:PublishReadyToRun=false `
    /p:PublishTrimmed=false `
    /p:DebugType=None `
    /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Lightweight dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $publishDirectory "CodexUpdater.App.exe") `
    -Destination $portableExecutable `
    -Force

$portableFile = Get-Item -LiteralPath $portableExecutable
if ($portableFile.Length -gt $maximumPortableSizeBytes) {
    throw "Lightweight executable exceeds 10 MB: $($portableFile.Length) bytes."
}

$portableHash = (Get-FileHash -LiteralPath $portableExecutable -Algorithm SHA256).Hash
Set-Content -LiteralPath $checksumFile `
    -Value "$portableHash  CodexInstaller-lite-win-x64.exe" `
    -Encoding ascii

$portableFile
Get-FileHash -LiteralPath $portableExecutable -Algorithm SHA256
