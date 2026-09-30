param(
    [switch]$SkipValidation,
    [switch]$FrameworkDependent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $SkipValidation) {
        Invoke-DotNet -Arguments @('restore', '.\CodexUpdater.sln', '/p:NuGetAudit=true')
        Invoke-DotNet -Arguments @('format', '.\CodexUpdater.sln', '--verify-no-changes', '--no-restore')
        Invoke-DotNet -Arguments @('build', '.\CodexUpdater.sln', '-c', 'Release', '--no-restore', '-warnaserror')
        Invoke-DotNet -Arguments @('test', '.\tests\CodexUpdater.Tests\CodexUpdater.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore')
    }

    & (Join-Path $PSScriptRoot 'build-support\Prepare-WebView2.ps1')
    $project = '.\src\CodexUpdater.App\CodexUpdater.App.csproj'
    if ($FrameworkDependent) {
        Invoke-DotNet -Arguments @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', '.\publish')
        Get-Item -LiteralPath '.\publish\CodexUpdater.App.exe'
    } else {
        Invoke-DotNet -Arguments @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-o', '.\publish-exe', '/p:PublishSingleFile=true', '/p:IncludeNativeLibrariesForSelfExtract=true',
            '/p:EnableCompressionInSingleFile=true')
        New-Item -ItemType Directory -Path '.\dist' -Force | Out-Null
        Copy-Item -LiteralPath '.\publish-exe\CodexUpdater.App.exe' -Destination '.\dist\CodexInstaller-win-x64.exe' -Force
        Get-Item -LiteralPath '.\dist\CodexInstaller-win-x64.exe'
        Get-FileHash -LiteralPath '.\dist\CodexInstaller-win-x64.exe' -Algorithm SHA256
    }
} finally {
    Pop-Location
}
